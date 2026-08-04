using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeCode.VisualStudio.Services
{
    /// <summary>
    /// Options controlling how the <c>claude</c> CLI is launched for a chat session.
    /// </summary>
    public sealed class ClaudeSessionOptions
    {
        public string WorkingDirectory;
        public string Model;                 // null/"default" -> DefaultModel (latest Opus, 1M ctx)
        public string PermissionMode = "acceptEdits";
        public string Effort;                // none|low|medium|high -> --max-thinking-tokens
        public string ResumeSessionId;       // for continuing a prior session
        public IDictionary<string, string> ExtraEnvironment;
    }

    /// <summary>
    /// Drives a long-lived <c>claude</c> process in bidirectional stream-json mode:
    /// writes user turns to stdin and parses the NDJSON event stream from stdout,
    /// raising normalized events that the UI renders.
    /// </summary>
    public sealed class ClaudeSession : IDisposable
    {
        private readonly ClaudeSessionOptions _options;
        private Process _process;
        private StreamWriter _stdin;
        private readonly object _writeLock = new object();
        private int _controlSeq;

        // ---- Interactive permission prompts (the "Ask before edits" mode) -------------------
        // In headless stream-json the CLI can't pop a terminal prompt, so we register an in-process
        // SDK MCP server ("vsperm") via the control-protocol `initialize`, and pass
        // `--permission-prompt-tool mcp__vsperm__approve`. The CLI then drives that server over
        // `mcp_message` control requests (JSON-RPC initialize/tools/list/tools/call); a tools/call
        // of `approve` is surfaced as a permission card and the user's allow/deny becomes the tool's
        // result. Only enabled for the "default" permission mode — every other mode launches exactly
        // as before (no initialize, no prompt tool), so existing behavior is untouched.
        private const string PermServer = "vsperm";
        private const string PermTool = "approve";

        // Wire id behind the picker's "Default (recommended)" entry — mirrors MODEL_WIRE.default
        // in app.js. Deliberately an *alias*, not a pinned/dated id: the CLI resolves "opus" to
        // the newest Opus at launch time, so a new model release is picked up without shipping a
        // new extension build. The "[1m]" suffix keeps the 1M-context variant.
        private const string DefaultModel = "opus[1m]";

        private bool PermissionPromptEnabled =>
            string.Equals(_options.PermissionMode, "default", StringComparison.Ordinal);

        private sealed class PendingPerm { public object JsonRpcId; public string ControlRequestId; public string OriginalInputJson; }
        private readonly Dictionary<string, PendingPerm> _pendingPerms = new Dictionary<string, PendingPerm>(StringComparer.Ordinal);
        // Guards _pendingPerms and _pendingModeSwitch: cards are raised on the stdout read loop and
        // answered from the UI thread (or from a mode switch).
        private readonly object _permLock = new object();

        // In-flight set_permission_mode control requests: our request_id -> requested mode. The CLI
        // answers with a control_response; success applies the mode to the *running* process (no
        // restart), an error tells the caller to fall back to relaunching with the new mode.
        private readonly Dictionary<string, string> _pendingModeSwitch = new Dictionary<string, string>(StringComparer.Ordinal);

        // The mode the running process is actually in (launch mode, then whatever a successful
        // set_permission_mode switched it to).
        private string _liveMode;

        // Per-message streaming state: content block index -> kind/tool accumulation.
        private readonly Dictionary<int, BlockState> _blocks = new Dictionary<int, BlockState>();

        // Usage of the most recent API request (see ContextUsageInfo) — the live context size.
        private ContextUsageInfo _liveUsage;

        public string SessionId { get; private set; }
        public bool IsRunning => _process != null && !_process.HasExited;

        // True when this process was launched with --permission-prompt-tool, i.e. it can raise
        // permission cards. A session launched without it cannot be switched *into* a prompting mode
        // live — the caller must relaunch instead (see CanSwitchTo).
        public bool PermissionPromptRegistered { get; private set; }

        public event Action<SystemInitInfo> SystemInit;
        public event Action AssistantStart;
        public event Action<string> TextDelta;
        public event Action<string> ThinkingDelta;
        public event Action<ToolUseInfo> ToolUse;
        public event Action<ToolResultInfo> ToolResult;
        public event Action AssistantEnd;
        public event Action<ResultInfo> Result;
        // Live context size, one per API request in the turn (not cumulative).
        public event Action<ContextUsageInfo> ContextUsage;
        public event Action<PermissionRequestInfo> PermissionRequest;
        // A pending permission card was answered by us, not the user (mode switched to Auto).
        public event Action<string> PermissionAutoAllowed;
        // The CLI refused a live mode switch; the caller should relaunch with the new mode.
        public event Action<string> PermissionModeChangeFailed;
        public event Action<CompactInfo> Compacted;
        public event Action<string> ErrorEvent;
        public event Action<int> Exited;
        public event Action<string> Diagnostic;

        public ClaudeSession(ClaudeSessionOptions options)
        {
            _options = options ?? new ClaudeSessionOptions();
        }

        private sealed class BlockState
        {
            public string Kind;        // "text" | "thinking" | "tool_use"
            public string ToolId;
            public string ToolName;
            public StringBuilder ToolInput = new StringBuilder();
        }

        public void Start()
        {
            if (IsRunning) return;

            var cli = ClaudeCliLocator.Locate();
            var args = BuildArguments(cli);
            Log.Write("Start(): file=" + cli.FileName + " resolved=" + cli.ResolvedPath + " cwd=" + _options.WorkingDirectory);
            Log.Write("Start(): args=" + args);

            var psi = new ProcessStartInfo
            {
                FileName = cli.FileName,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = string.IsNullOrEmpty(_options.WorkingDirectory)
                    ? Environment.CurrentDirectory
                    : _options.WorkingDirectory,
            };

            // Force non-interactive, machine-readable behavior.
            psi.EnvironmentVariables["FORCE_COLOR"] = "0";
            psi.EnvironmentVariables["NO_COLOR"] = "1";
            psi.EnvironmentVariables["CLAUDE_CODE_ENTRYPOINT"] = "vs-extension";
            if (_options.ExtraEnvironment != null)
            {
                foreach (var kv in _options.ExtraEnvironment)
                {
                    psi.EnvironmentVariables[kv.Key] = kv.Value;
                }
            }

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.Exited += (s, e) =>
            {
                int code = 0;
                try { code = _process.ExitCode; } catch { }
                Log.Write("Exited code=" + code);
                Exited?.Invoke(code);
            };

            Diagnostic?.Invoke("Launching: " + cli.ResolvedPath + " " + args);

            try
            {
                _process.Start();
                Log.Write("Process.Start OK pid=" + _process.Id);
            }
            catch (Exception ex)
            {
                Log.Write("Process.Start FAILED: " + ex);
                Diagnostic?.Invoke("spawn failed: " + ex.Message);
                ErrorEvent?.Invoke("Could not launch claude (" + cli.ResolvedPath + "): " + ex.Message);
                throw;
            }

            _stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false))
            {
                AutoFlush = true,
                NewLine = "\n",
            };

            _ = Task.Run(() => ReadLoopAsync(_process.StandardOutput));
            _ = Task.Run(() => ErrorLoopAsync(_process.StandardError));

            _liveMode = _options.PermissionMode;
            PermissionPromptRegistered = PermissionPromptEnabled;
            if (PermissionPromptEnabled) SendInitialize();
        }

        // Can the running process be moved to `mode` without a relaunch? Every mode except "default"
        // either needs no prompts (bypassPermissions) or only prompts for tools our launch args
        // already cover, so the one blocker is switching into a prompting mode in a process that was
        // started without --permission-prompt-tool.
        public bool CanSwitchTo(string mode)
        {
            if (!IsRunning) return false;
            return PermissionPromptRegistered || !string.Equals(mode, "default", StringComparison.Ordinal);
        }

        /// <summary>
        /// Applies a permission mode to the *running* CLI process via the control protocol, so a
        /// switch takes effect immediately — including mid-turn, which is when users reach for it
        /// (they flip to Auto precisely because a prompt is on screen). Returns false when the
        /// switch is not possible; the caller then relaunches. A failure reported later by the CLI
        /// raises PermissionModeChangeFailed.
        /// </summary>
        public bool SetPermissionMode(string mode)
        {
            if (string.IsNullOrEmpty(mode) || !CanSwitchTo(mode)) return false;
            if (string.Equals(mode, _liveMode, StringComparison.Ordinal)) return true;

            var id = "req_mode_" + Interlocked.Increment(ref _controlSeq).ToString(CultureInfo.InvariantCulture);
            lock (_permLock) { _pendingModeSwitch[id] = mode; }

            var msg = new Dictionary<string, object>
            {
                ["type"] = "control_request",
                ["request_id"] = id,
                ["request"] = new Dictionary<string, object>
                {
                    ["subtype"] = "set_permission_mode",
                    ["mode"] = mode,
                },
            };
            WriteLine(JsonSerializer.Serialize(msg));
            return true;
        }

        // Answers every permission card still on screen with "allow". Only called after the CLI
        // confirms a switch to bypassPermissions ("Claude runs any tool automatically") — otherwise
        // the queued prompt would keep the turn blocked in a mode that no longer asks.
        private void AutoAllowPendingPermissions()
        {
            List<PendingPerm> pending;
            lock (_permLock)
            {
                if (_pendingPerms.Count == 0) return;
                pending = new List<PendingPerm>(_pendingPerms.Values);
                _pendingPerms.Clear();
            }
            foreach (var p in pending)
            {
                RespondToMcpPermission(p, "allow", null);
                PermissionAutoAllowed?.Invoke(p.ControlRequestId);
            }
        }

        // Registers our in-process SDK MCP permission server with the CLI so it can call back for
        // per-tool approval. Sent once at session start (only in the "Ask before edits" mode).
        private void SendInitialize()
        {
            var id = "req_init_" + Interlocked.Increment(ref _controlSeq).ToString(CultureInfo.InvariantCulture);
            var msg = new Dictionary<string, object>
            {
                ["type"] = "control_request",
                ["request_id"] = id,
                ["request"] = new Dictionary<string, object>
                {
                    ["subtype"] = "initialize",
                    ["sdkMcpServers"] = new[] { PermServer },
                },
            };
            WriteLine(JsonSerializer.Serialize(msg));
        }

        private string BuildArguments(ClaudeCliLocator.Result cli)
        {
            var sb = new StringBuilder();
            sb.Append(cli.ArgumentPrefix);
            sb.Append("--print");
            sb.Append(" --input-format stream-json");
            sb.Append(" --output-format stream-json");
            sb.Append(" --verbose");
            sb.Append(" --include-partial-messages");

            if (!string.IsNullOrEmpty(_options.PermissionMode))
            {
                sb.Append(" --permission-mode ").Append(_options.PermissionMode);
            }
            if (PermissionPromptEnabled)
            {
                // Route per-tool approval through our in-process SDK MCP server (see field docs).
                sb.Append(" --permission-prompt-tool mcp__").Append(PermServer).Append("__").Append(PermTool);
            }
            // "default"/null resolves to the advertised default model so the launched session
            // matches the picker label. Every picker id ("fable"/"sonnet"/"haiku") is likewise a
            // CLI alias, so all four entries follow the newest model in their family automatically.
            var model = (string.IsNullOrEmpty(_options.Model) ||
                         string.Equals(_options.Model, "default", StringComparison.OrdinalIgnoreCase))
                ? DefaultModel
                : _options.Model;
            sb.Append(" --model ").Append(model);
            int thinking = ThinkingTokensForEffort(_options.Effort);
            if (thinking > 0)
            {
                sb.Append(" --max-thinking-tokens ").Append(thinking.ToString(CultureInfo.InvariantCulture));
            }
            if (!string.IsNullOrEmpty(_options.ResumeSessionId) && IsSafeSessionId(_options.ResumeSessionId))
            {
                sb.Append(" --resume ").Append(_options.ResumeSessionId);
            }
            return sb.ToString();
        }

        // Defense-in-depth: the resume id is the CLI's own session id, but validate it is a
        // plain id (UUID-like) so it can never carry extra CLI flags / shell metacharacters.
        private static bool IsSafeSessionId(string s)
        {
            if (s.Length == 0 || s.Length > 100) return false;
            // First char must be alphanumeric so the value can't parse as another CLI flag.
            if (!char.IsLetterOrDigit(s[0])) return false;
            foreach (var c in s)
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_')) return false;
            return true;
        }

        internal static int ThinkingTokensForEffort(string effort)
        {
            switch ((effort ?? "").ToLowerInvariant())
            {
                case "low": return 4096;
                case "medium": return 10000;
                case "high": return 16000;
                case "extrahigh": return 24000;
                case "max": return 31999;          // max thinking budget
                case "ultracode": return 31999;    // max thinking (+ workflow-style prompting)
                default: return 0; // "none"/null -> omit (model default, no extended thinking)
            }
        }

        // ---- Sending -----------------------------------------------------
        public void SendUserMessage(string text, IReadOnlyList<ImageInput> images = null)
        {
            if (!IsRunning)
            {
                Start();
            }

            var content = new List<object>();
            if (images != null)
            {
                foreach (var img in images)
                {
                    content.Add(new
                    {
                        type = "image",
                        source = new { type = "base64", media_type = img.MediaType, data = img.Data },
                    });
                }
            }
            content.Add(new { type = "text", text = text ?? string.Empty });

            var message = new Dictionary<string, object>
            {
                ["type"] = "user",
                ["message"] = new { role = "user", content = content },
            };

            WriteLine(JsonSerializer.Serialize(message));
        }

        public void SendInterrupt()
        {
            var id = "req_" + Interlocked.Increment(ref _controlSeq).ToString(CultureInfo.InvariantCulture);
            var msg = new Dictionary<string, object>
            {
                ["type"] = "control_request",
                ["request_id"] = id,
                ["request"] = new { subtype = "interrupt" },
            };
            WriteLine(JsonSerializer.Serialize(msg));
        }

        public void RespondToPermission(string requestId, string behavior, string message)
        {
            // behavior: "allow" | "deny". The card was raised either from an mcp_message tools/call
            // (our SDK permission server) or, on older paths, a raw can_use_tool control request.
            PendingPerm pending;
            bool wasPending;
            lock (_permLock)
            {
                wasPending = _pendingPerms.TryGetValue(requestId, out pending);
                if (wasPending) _pendingPerms.Remove(requestId);
            }
            if (wasPending)
            {
                RespondToMcpPermission(pending, behavior, message);
                return;
            }

            object response = behavior == "deny"
                ? (object)new { behavior = "deny", message = message ?? "Denied by user" }
                : new { behavior = "allow" };

            var msg = new Dictionary<string, object>
            {
                ["type"] = "control_response",
                ["response"] = new
                {
                    subtype = "success",
                    request_id = requestId,
                    response = response,
                },
            };
            WriteLine(JsonSerializer.Serialize(msg));
        }

        // Returns the allow/deny decision as the result of the `approve` tool call (the contract the
        // CLI's permission-prompt tool expects): allow echoes back the original input as updatedInput;
        // deny carries a message. The decision JSON is the tool result's text content.
        private void RespondToMcpPermission(PendingPerm pending, string behavior, string message)
        {
            string decisionJson = behavior == "deny"
                ? "{\"behavior\":\"deny\",\"message\":" + JsonSerializer.Serialize(message ?? "Denied by user") + "}"
                : "{\"behavior\":\"allow\",\"updatedInput\":" + (pending.OriginalInputJson ?? "{}") + "}";

            var result = new Dictionary<string, object>
            {
                ["content"] = new object[] { new Dictionary<string, object> { ["type"] = "text", ["text"] = decisionJson } },
            };
            AckControl(pending.ControlRequestId, McpResult(pending.JsonRpcId, result));
        }

        private void WriteLine(string json)
        {
            try
            {
                lock (_writeLock)
                {
                    _stdin?.Write(json);
                    _stdin?.Write("\n");
                    _stdin?.Flush();
                }
                Log.WriteVerbose("IN  " + Trunc(json));
            }
            catch (Exception ex)
            {
                Log.Write("WriteLine FAILED: " + ex);
                ErrorEvent?.Invoke("Failed to send to claude: " + ex.Message);
            }
        }

        // ---- Reading -----------------------------------------------------
        private async Task ReadLoopAsync(StreamReader reader)
        {
            try
            {
                string line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (line.Length == 0) continue;
                    Log.WriteVerbose("OUT " + Trunc(line));
                    try { HandleLine(line); }
                    catch (Exception ex) { Diagnostic?.Invoke("parse error: " + ex.Message + " :: " + Trunc(line)); }
                }
                Log.Write("stdout closed");
            }
            catch (Exception ex)
            {
                Diagnostic?.Invoke("read loop ended: " + ex.Message);
            }
        }

        private async Task ErrorLoopAsync(StreamReader reader)
        {
            try
            {
                string line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (line.Length > 0) { Log.WriteVerbose("ERR " + line); Diagnostic?.Invoke("stderr: " + line); }
                }
            }
            catch { }
        }

        private void HandleLine(string line)
        {
            using (var doc = JsonDocument.Parse(line))
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl)) return;
                var type = typeEl.GetString();

                switch (type)
                {
                    case "system": HandleSystem(root); break;
                    case "stream_event": HandleStreamEvent(root); break;
                    case "user": HandleUser(root); break;
                    case "assistant": /* ignored: covered by stream_event */ break;
                    case "result": HandleResult(root); break;
                    case "control_request": HandleControlRequest(root); break;
                    case "control_response": HandleControlResponse(root); break;
                    case "rate_limit_event": break;
                    default: break;
                }
            }
        }

        private void HandleSystem(JsonElement root)
        {
            var subtype = GetString(root, "subtype");

            // The CLI compacted its own context (either from a /compact we sent, or automatically
            // when the window filled). It reuses the same session — nothing to restart on our side,
            // and no assistant text is emitted, so the transcript stays clean.
            if (subtype == "compact_boundary")
            {
                var ci = new CompactInfo();
                if (root.TryGetProperty("compact_metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
                {
                    ci.Trigger = GetString(meta, "trigger");
                    ci.PreTokens = GetLong(meta, "pre_tokens");
                    ci.PostTokens = GetLong(meta, "post_tokens");
                    ci.DurationMs = GetLong(meta, "duration_ms");
                }
                Log.Write("compact_boundary trigger=" + ci.Trigger + " " + ci.PreTokens + " -> " + ci.PostTokens);
                Compacted?.Invoke(ci);
                return;
            }

            if (subtype != "init") return;

            var info = new SystemInitInfo
            {
                SessionId = GetString(root, "session_id"),
                Model = GetString(root, "model"),
                Cwd = GetString(root, "cwd"),
                PermissionMode = GetString(root, "permissionMode"),
                Version = GetString(root, "claude_code_version"),
            };
            if (root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tools.EnumerateArray()) info.Tools.Add(t.GetString());
            }
            // Commands are split across "slash_commands" and a "skills" array (skills invoke as
            // /<name> too). Merge both, deduped, so the palette matches what the CLI exposes.
            var seenCmds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in new[] { "slash_commands", "skills" })
            {
                if (root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var c in arr.EnumerateArray())
                    {
                        var name = c.GetString();
                        if (!string.IsNullOrEmpty(name) && seenCmds.Add(name)) info.SlashCommands.Add(name);
                    }
            }
            if (root.TryGetProperty("mcp_servers", out var mcps) && mcps.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in mcps.EnumerateArray())
                {
                    var name = GetString(m, "name");
                    if (string.IsNullOrEmpty(name)) continue;
                    var status = GetString(m, "status");
                    info.McpServers.Add(string.IsNullOrEmpty(status) ? name : name + " (" + status + ")");
                }
            }
            SessionId = info.SessionId;
            SystemInit?.Invoke(info);
        }

        private void HandleStreamEvent(JsonElement root)
        {
            if (!root.TryGetProperty("event", out var ev)) return;
            var etype = GetString(ev, "type");

            // Sub-agent (Task) turns stream through here too, tagged with parent_tool_use_id. Their
            // requests carry the sub-agent's own context, which says nothing about ours — usage from
            // them must not move the ring.
            bool ownTurn = !root.TryGetProperty("parent_tool_use_id", out var parentId) ||
                           parentId.ValueKind == JsonValueKind.Null;

            switch (etype)
            {
                case "message_start":
                    _blocks.Clear();
                    // Per-request usage: the prompt of THIS request is the live context size.
                    if (ownTurn &&
                        ev.TryGetProperty("message", out var startMsg) &&
                        startMsg.TryGetProperty("usage", out var startUsage))
                    {
                        _liveUsage = new ContextUsageInfo
                        {
                            InputTokens = GetLong(startUsage, "input_tokens"),
                            CacheReadTokens = GetLong(startUsage, "cache_read_input_tokens"),
                            CacheCreationTokens = GetLong(startUsage, "cache_creation_input_tokens"),
                            OutputTokens = GetLong(startUsage, "output_tokens"),
                        };
                        ContextUsage?.Invoke(_liveUsage);
                    }
                    AssistantStart?.Invoke();
                    break;

                case "message_delta":
                    // Carries the final output_tokens for the response; add it to the prompt so the
                    // ring reflects what the NEXT request will carry.
                    if (ownTurn && _liveUsage != null && ev.TryGetProperty("usage", out var deltaUsage))
                    {
                        _liveUsage.OutputTokens = GetLong(deltaUsage, "output_tokens");
                        ContextUsage?.Invoke(_liveUsage);
                    }
                    break;

                case "content_block_start":
                {
                    int idx = GetInt(ev, "index");
                    var cb = ev.GetProperty("content_block");
                    var kind = GetString(cb, "type");
                    var state = new BlockState { Kind = kind };
                    if (kind == "tool_use")
                    {
                        state.ToolId = GetString(cb, "id");
                        state.ToolName = GetString(cb, "name");
                    }
                    _blocks[idx] = state;
                    break;
                }

                case "content_block_delta":
                {
                    int idx = GetInt(ev, "index");
                    if (!ev.TryGetProperty("delta", out var delta)) break;
                    var dtype = GetString(delta, "type");
                    if (dtype == "text_delta")
                    {
                        TextDelta?.Invoke(GetString(delta, "text"));
                    }
                    else if (dtype == "thinking_delta")
                    {
                        ThinkingDelta?.Invoke(GetString(delta, "thinking"));
                    }
                    else if (dtype == "input_json_delta")
                    {
                        if (_blocks.TryGetValue(idx, out var st))
                            st.ToolInput.Append(GetString(delta, "partial_json"));
                    }
                    break;
                }

                case "content_block_stop":
                {
                    int idx = GetInt(ev, "index");
                    if (_blocks.TryGetValue(idx, out var st) && st.Kind == "tool_use")
                    {
                        ToolUse?.Invoke(new ToolUseInfo
                        {
                            Id = st.ToolId,
                            Name = st.ToolName,
                            InputJson = st.ToolInput.Length > 0 ? st.ToolInput.ToString() : "{}",
                        });
                    }
                    break;
                }

                case "message_stop":
                    AssistantEnd?.Invoke();
                    break;
            }
        }

        private void HandleUser(JsonElement root)
        {
            // Tool results arrive as user messages with tool_result content blocks.
            if (!root.TryGetProperty("message", out var msg)) return;
            if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;

            foreach (var block in content.EnumerateArray())
            {
                if (GetString(block, "type") != "tool_result") continue;
                ToolResult?.Invoke(new ToolResultInfo
                {
                    ToolUseId = GetString(block, "tool_use_id"),
                    IsError = block.TryGetProperty("is_error", out var er) && er.ValueKind == JsonValueKind.True,
                    Content = ExtractResultContent(block),
                });
            }
        }

        private static string ExtractResultContent(JsonElement block)
        {
            if (!block.TryGetProperty("content", out var c)) return string.Empty;
            if (c.ValueKind == JsonValueKind.String) return c.GetString();
            if (c.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var part in c.EnumerateArray())
                {
                    if (GetString(part, "type") == "text") sb.AppendLine(GetString(part, "text"));
                }
                return sb.ToString();
            }
            return c.ToString();
        }

        private void HandleResult(JsonElement root)
        {
            var info = new ResultInfo
            {
                IsError = root.TryGetProperty("is_error", out var er) && er.ValueKind == JsonValueKind.True,
                Text = GetString(root, "result"),
                SessionId = GetString(root, "session_id"),
                DurationMs = GetLong(root, "duration_ms"),
                CostUsd = GetDouble(root, "total_cost_usd"),
            };
            if (root.TryGetProperty("usage", out var usage))
            {
                info.InputTokens = GetLong(usage, "input_tokens");
                info.OutputTokens = GetLong(usage, "output_tokens");
                info.CacheReadTokens = GetLong(usage, "cache_read_input_tokens");
                info.CacheCreationTokens = GetLong(usage, "cache_creation_input_tokens");
            }
            // modelUsage is keyed by model id; pull the context window + model name from it.
            if (root.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in mu.EnumerateObject())
                {
                    info.Model = prop.Name;
                    info.ContextWindow = GetLong(prop.Value, "contextWindow");
                    break;
                }
            }
            if (!string.IsNullOrEmpty(info.SessionId)) SessionId = info.SessionId;
            Result?.Invoke(info);
        }

        // Replies to control requests *we* sent. Only set_permission_mode is tracked: success means
        // the running process is now in that mode, error means it refused and the caller must
        // relaunch to apply it.
        private void HandleControlResponse(JsonElement root)
        {
            if (!root.TryGetProperty("response", out var resp)) return;
            var requestId = GetString(resp, "request_id");
            if (string.IsNullOrEmpty(requestId)) return;

            string mode;
            lock (_permLock)
            {
                if (!_pendingModeSwitch.TryGetValue(requestId, out mode)) return;
                _pendingModeSwitch.Remove(requestId);
            }

            if (string.Equals(GetString(resp, "subtype"), "error", StringComparison.Ordinal))
            {
                Log.Write("set_permission_mode " + mode + " rejected: " + (GetString(resp, "error") ?? "unknown"));
                PermissionModeChangeFailed?.Invoke(mode);
                return;
            }

            _liveMode = mode;
            _options.PermissionMode = mode;   // keeps a later relaunch (model/effort change) in sync
            Log.Write("permission mode switched live to " + mode);
            if (string.Equals(mode, "bypassPermissions", StringComparison.Ordinal)) AutoAllowPendingPermissions();
        }

        private void HandleControlRequest(JsonElement root)
        {
            // Permission requests (can_use_tool) arrive here when running with
            // interactive permissions. Surface them to the UI for a decision.
            if (!root.TryGetProperty("request", out var req)) return;
            var subtype = GetString(req, "subtype");
            var requestId = GetString(root, "request_id");
            if (string.IsNullOrEmpty(requestId)) requestId = GetString(req, "request_id");

            if (subtype == "mcp_message")
            {
                HandleMcpMessage(requestId, req);
                return;
            }

            if (subtype == "can_use_tool" || subtype == "permission")
            {
                var toolName = GetString(req, "tool_name");
                string inputJson = "{}";
                if (req.TryGetProperty("input", out var input)) inputJson = input.GetRawText();
                else if (req.TryGetProperty("tool_input", out var ti)) inputJson = ti.GetRawText();

                PermissionRequest?.Invoke(new PermissionRequestInfo
                {
                    RequestId = requestId,
                    ToolName = toolName,
                    InputJson = inputJson,
                });
            }
            else
            {
                // Acknowledge anything else so the CLI is not left waiting.
                AckControl(requestId, null);
            }
        }

        // The CLI drives our in-process "vsperm" SDK MCP server with JSON-RPC requests wrapped in
        // mcp_message control requests. We answer initialize/tools/list immediately; a tools/call of
        // `approve` is surfaced as a permission card and answered later via RespondToPermission.
        private void HandleMcpMessage(string controlRequestId, JsonElement req)
        {
            var serverName = GetString(req, "server_name");
            if (!string.Equals(serverName, PermServer, StringComparison.Ordinal) ||
                !req.TryGetProperty("message", out var rpc))
            {
                AckControl(controlRequestId, null); // unknown server: ack so the CLI is not blocked
                return;
            }

            var method = GetString(rpc, "method");
            object rpcId = rpc.TryGetProperty("id", out var idEl) ? (object)idEl.Clone() : null;

            if (method == "initialize")
            {
                AckControl(controlRequestId, McpResult(rpcId, new Dictionary<string, object>
                {
                    ["protocolVersion"] = "2025-06-18",
                    ["capabilities"] = new Dictionary<string, object> { ["tools"] = new Dictionary<string, object>() },
                    ["serverInfo"] = new Dictionary<string, object> { ["name"] = PermServer, ["version"] = "1.0.0" },
                }));
            }
            else if (method == "tools/list")
            {
                var tool = new Dictionary<string, object>
                {
                    ["name"] = PermTool,
                    ["description"] = "Prompt the user to allow or deny a tool call",
                    ["inputSchema"] = new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>(),
                        ["additionalProperties"] = true,
                    },
                };
                AckControl(controlRequestId, McpResult(rpcId, new Dictionary<string, object>
                {
                    ["tools"] = new object[] { tool },
                }));
            }
            else if (method == "tools/call")
            {
                // Surface the card; the decision is sent later from RespondToPermission.
                string toolName = null, inputJson = "{}";
                if (rpc.TryGetProperty("params", out var prm) && prm.TryGetProperty("arguments", out var args))
                {
                    toolName = GetString(args, "tool_name");
                    if (args.TryGetProperty("input", out var inp)) inputJson = inp.GetRawText();
                }
                lock (_permLock)
                {
                    _pendingPerms[controlRequestId] = new PendingPerm
                    {
                        JsonRpcId = rpcId,
                        ControlRequestId = controlRequestId,
                        OriginalInputJson = inputJson,
                    };
                }
                PermissionRequest?.Invoke(new PermissionRequestInfo
                {
                    RequestId = controlRequestId,
                    ToolName = toolName,
                    InputJson = inputJson,
                });
            }
            else
            {
                // JSON-RPC notification (e.g. notifications/initialized) — no result, just ack.
                AckControl(controlRequestId, new Dictionary<string, object> { ["mcp_response"] = null });
            }
        }

        // Wraps a JSON-RPC response object as the mcp_response payload of a control_response.
        private static Dictionary<string, object> McpResult(object rpcId, object result)
        {
            return new Dictionary<string, object>
            {
                ["mcp_response"] = new Dictionary<string, object>
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = rpcId,
                    ["result"] = result,
                },
            };
        }

        // Sends a success control_response for the given request, optionally carrying a response body.
        private void AckControl(string requestId, object response)
        {
            var inner = new Dictionary<string, object> { ["subtype"] = "success", ["request_id"] = requestId };
            if (response != null) inner["response"] = response;
            var msg = new Dictionary<string, object> { ["type"] = "control_response", ["response"] = inner };
            WriteLine(JsonSerializer.Serialize(msg));
        }

        // ---- JSON helpers ------------------------------------------------
        private static string GetString(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int GetInt(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

        private static long GetLong(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.TryGetInt64(out var i) ? i : 0;

        private static double GetDouble(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.TryGetDouble(out var d) ? d : 0;

        private static string Trunc(string s) => s.Length > 200 ? s.Substring(0, 200) + "…" : s;

        // ---- Lifecycle ---------------------------------------------------
        public void Stop()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    try { _stdin?.Close(); } catch { }
                    if (!_process.WaitForExit(800)) _process.Kill();
                }
            }
            catch { }
        }

        public void Dispose()
        {
            Stop();
            try { _process?.Dispose(); } catch { }
            _process = null;
        }
    }
}

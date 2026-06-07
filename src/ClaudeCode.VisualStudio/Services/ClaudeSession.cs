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
        public string Model;                 // null/"default" -> let CLI choose
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

        // Per-message streaming state: content block index -> kind/tool accumulation.
        private readonly Dictionary<int, BlockState> _blocks = new Dictionary<int, BlockState>();

        public string SessionId { get; private set; }
        public bool IsRunning => _process != null && !_process.HasExited;

        public event Action<SystemInitInfo> SystemInit;
        public event Action AssistantStart;
        public event Action<string> TextDelta;
        public event Action<string> ThinkingDelta;
        public event Action<ToolUseInfo> ToolUse;
        public event Action<ToolResultInfo> ToolResult;
        public event Action AssistantEnd;
        public event Action<ResultInfo> Result;
        public event Action<PermissionRequestInfo> PermissionRequest;
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

            _ = Task.Run(() => ReadLoop(_process.StandardOutput));
            _ = Task.Run(() => ErrorLoop(_process.StandardError));
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
            if (!string.IsNullOrEmpty(_options.Model) &&
                !string.Equals(_options.Model, "default", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(" --model ").Append(_options.Model);
            }
            int thinking = ThinkingTokensForEffort(_options.Effort);
            if (thinking > 0)
            {
                sb.Append(" --max-thinking-tokens ").Append(thinking.ToString(CultureInfo.InvariantCulture));
            }
            if (!string.IsNullOrEmpty(_options.ResumeSessionId))
            {
                sb.Append(" --resume ").Append(_options.ResumeSessionId);
            }
            return sb.ToString();
        }

        private static int ThinkingTokensForEffort(string effort)
        {
            switch ((effort ?? "").ToLowerInvariant())
            {
                case "low": return 4096;
                case "medium": return 12000;
                case "high": return 31999;
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
            // behavior: "allow" | "deny"
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
                Log.Write("IN  " + Trunc(json));
            }
            catch (Exception ex)
            {
                Log.Write("WriteLine FAILED: " + ex);
                ErrorEvent?.Invoke("Failed to send to claude: " + ex.Message);
            }
        }

        // ---- Reading -----------------------------------------------------
        private async Task ReadLoop(StreamReader reader)
        {
            try
            {
                string line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (line.Length == 0) continue;
                    Log.Write("OUT " + Trunc(line));
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

        private async Task ErrorLoop(StreamReader reader)
        {
            try
            {
                string line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                {
                    if (line.Length > 0) { Log.Write("ERR " + line); Diagnostic?.Invoke("stderr: " + line); }
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
                    case "control_response": break;
                    case "rate_limit_event": break;
                    default: break;
                }
            }
        }

        private void HandleSystem(JsonElement root)
        {
            var subtype = GetString(root, "subtype");
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
            if (root.TryGetProperty("slash_commands", out var cmds) && cmds.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cmds.EnumerateArray()) info.SlashCommands.Add(c.GetString());
            }
            SessionId = info.SessionId;
            SystemInit?.Invoke(info);
        }

        private void HandleStreamEvent(JsonElement root)
        {
            if (!root.TryGetProperty("event", out var ev)) return;
            var etype = GetString(ev, "type");

            switch (etype)
            {
                case "message_start":
                    _blocks.Clear();
                    AssistantStart?.Invoke();
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

        private void HandleControlRequest(JsonElement root)
        {
            // Permission requests (can_use_tool) arrive here when running with
            // interactive permissions. Surface them to the UI for a decision.
            if (!root.TryGetProperty("request", out var req)) return;
            var subtype = GetString(req, "subtype");
            var requestId = GetString(root, "request_id");
            if (string.IsNullOrEmpty(requestId)) requestId = GetString(req, "request_id");

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
                var ack = new Dictionary<string, object>
                {
                    ["type"] = "control_response",
                    ["response"] = new { subtype = "success", request_id = requestId },
                };
                WriteLine(JsonSerializer.Serialize(ack));
            }
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

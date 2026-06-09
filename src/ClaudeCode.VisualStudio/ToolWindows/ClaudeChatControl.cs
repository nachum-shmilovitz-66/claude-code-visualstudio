using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ClaudeCode.VisualStudio.Services;
using ClaudeCode.VisualStudio.WebView;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.Web.WebView2.Wpf;

namespace ClaudeCode.VisualStudio
{
    /// <summary>
    /// Chat tool window content: a WebView2 hosting the Claude Code chat UI, wired to a
    /// <see cref="ClaudeSession"/> that drives the real <c>claude</c> CLI.
    /// </summary>
    public class ClaudeChatControl : UserControl
    {
        private readonly WebView2 _webView;
        private readonly WebViewHost _host;

        private ClaudeSession _session;
        private string _model = "default";
        private string _permissionMode = "default";   // safest default: ask before edits
        private string _effort = "none";
        private bool _showThinking = true;

        private bool _optionsDirty;
        private bool _compacting;

        private readonly IdeContextService _ide = new IdeContextService();
        private readonly ThemeService _theme = new ThemeService();
        private readonly Dictionary<string, EditSnapshot> _editedFiles = new Dictionary<string, EditSnapshot>();

        private sealed class EditSnapshot { public string Path; public string OldText; }
        private List<string> _tools = new List<string>();
        private List<string> _mcpServers = new List<string>();
        private SessionRecord _record;        // persisted transcript for the current cwd
        private string _pendingResumeId;      // CLI session id to --resume on next start (restore)

        // Working directory for claude. Defaults to the user profile and is upgraded to the
        // solution directory once known. Cached so the send path never blocks on VS services.
        private string _cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public ClaudeChatControl()
        {
            _webView = new WebView2
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
            };
            Content = _webView;

            _host = new WebViewHost(_webView);
            _host.MessageReceived += OnMessageReceived;
            _theme.ThemeChanged += vars => _host.PostMessage("theme", vars);

            Loaded += OnLoaded;
            Unloaded += (s, e) => _session?.Dispose();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = "Event handler")]
        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            try
            {
                await _host.InitializeAsync();
            }
            catch (Exception ex)
            {
                Content = new TextBlock
                {
                    Text = "Failed to start Claude Code WebView:\n" + ex.Message,
                    Margin = new Thickness(12),
                    TextWrapping = TextWrapping.Wrap,
                };
                return;
            }

            // Best-effort: upgrade cwd to the solution directory. Never blocks the chat.
            try
            {
                var folders = await _ide.GetWorkspaceFoldersAsync();
                if (folders != null && folders.Count > 0) _cwd = folders[0];
            }
            catch { }
        }

        private void OnMessageReceived(WebMessage message)
        {
            Log.Write("WebMessage: " + message.Type);
            switch (message.Type)
            {
                case "ready":
                    SendInit();
                    break;
                case "send":
                    HandleSend(message.Payload);
                    break;
                case "interrupt":
                    _session?.SendInterrupt();
                    break;
                case "newSession":
                    ResetSession();
                    break;
                case "setModel":
                    _model = InputValidation.SanitizeChoice(GetStr(message.Payload, "model"), InputValidation.AllowedModels, "default");
                    _optionsDirty = true;
                    SaveOptions();
                    break;
                case "setPermissionMode":
                    _permissionMode = InputValidation.SanitizeChoice(GetStr(message.Payload, "mode"), InputValidation.AllowedModes, "default");
                    _optionsDirty = true;
                    SaveOptions();
                    break;
                case "setEffort":
                    _effort = InputValidation.SanitizeChoice(GetStr(message.Payload, "effort"), InputValidation.AllowedEfforts, "none");
                    _optionsDirty = true;
                    SaveOptions();
                    break;
                case "setShowThinking":
                    _showThinking = GetBool(message.Payload, "on", true);
                    SaveOptions();
                    break;
                case "getContext":
                    SendContext();
                    break;
                case "getFiles":
                    SendFiles();
                    break;
                case "getUsage":
                    FetchAndSendAccountData();
                    break;
                case "getMcp":
                    SendMcp();
                    break;
                case "getCommands":
                    SendCommands();
                    break;
                case "mcpAuth":
                    LaunchClaudeTerminal();
                    break;
                case "openClaudeTerminal":
                    LaunchClaudeTerminal();
                    break;
                case "installCli":
                    LaunchCliInstall();
                    break;
                case "recheckSetup":
                    SendSetupStatus();
                    break;
                case "pickImage":
                    PickImage();
                    break;
                case "pickFile":
                    PickFile();
                    break;
                case "compact":
                    HandleCompact();
                    break;
                case "permissionResponse":
                    HandlePermissionResponse(message.Payload);
                    break;
                case "openExternal":
                    TryOpenExternal(GetStr(message.Payload, "url"));
                    break;
            }
        }

        private void FetchAndSendAccountData()
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                var data = await AccountService.FetchAsync();
                var limits = new List<object>();
                foreach (var l in data.Limits)
                    limits.Add(new { name = l.Name, percent = l.Percent, resetsIn = l.ResetsIn });

                _host.PostMessage("accountData", new
                {
                    authMethod = data.AuthMethod,
                    email = data.Email,
                    organization = data.Organization,
                    plan = data.Plan,
                    limits = limits.ToArray(),
                    manageUrl = data.ManageUrl,
                    error = data.Error,
                });
            });
        }

        private void SendContext()
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                var sel = await _ide.GetActiveSelectionAsync();
                var openFiles = await _ide.GetOpenFilesAsync();
                var folders = await _ide.GetWorkspaceFoldersAsync();

                // Refresh cwd from the open solution if no session has pinned it yet and the user
                // hasn't chosen one — the tool window often loads before the solution finished
                // opening, leaving the early (user-home) fallback showing here.
                if (_session == null)
                {
                    try { var d = await GetWorkingDirectoryAsync(); if (!string.IsNullOrEmpty(d)) _cwd = d; }
                    catch { }
                }

                // CLAUDE.md project/user memory the CLI will load for this working dir.
                string projectMd = null, userMd = null;
                try
                {
                    if (!string.IsNullOrEmpty(_cwd))
                    {
                        var p = Path.Combine(_cwd, "CLAUDE.md");
                        if (File.Exists(p)) projectMd = p;
                    }
                    var u = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "CLAUDE.md");
                    if (File.Exists(u)) userMd = u;
                }
                catch { }

                _host.PostMessage("context", new
                {
                    cwd = _cwd,
                    workspaceFolders = folders,
                    activeFile = sel?.FilePath,
                    languageId = sel?.LanguageId,
                    hasSelection = sel?.HasSelection ?? false,
                    selStart = sel?.StartLine ?? 0,
                    selEnd = sel?.EndLine ?? 0,
                    openFiles = openFiles,
                    claudeMdProject = projectMd,
                    claudeMdUser = userMd,
                    tools = _tools,
                    mcpServers = _mcpServers,
                    model = _model,
                    effort = _effort,
                    permissionMode = _permissionMode,
                    sessionId = _session?.SessionId,
                });
            }).FireAndForget();
        }

        // Runs `claude mcp list` out-of-band so the /mcp screen can show configured servers
        // (with live health) even before the first message starts a chat session.
        private void SendMcp()
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    if (_session == null)
                    {
                        try { var d = await GetWorkingDirectoryAsync(); if (!string.IsNullOrEmpty(d)) _cwd = d; }
                        catch { }
                    }
                    var servers = await McpService.ListAsync(_cwd);
                    var list = new List<object>();
                    foreach (var s in servers)
                        list.Add(new { name = s.Name, detail = s.Detail, status = s.Status, ok = s.Ok, scope = s.Scope, missingEnv = s.MissingEnv, envMaybeInvalid = s.EnvMaybeInvalid });
                    _host.PostMessage("mcpList", new { servers = list });
                }
                catch (Exception ex)
                {
                    Log.Write("SendMcp: " + ex.Message);
                    _host.PostMessage("mcpList", new { servers = new List<object>(), error = ex.Message });
                }
            });
        }

        // Fetches the CLI's full slash-command set out-of-band so the / palette shows the
        // complete list (built-ins + project .claude/commands) before the first message
        // starts a session. The live session's system/init refreshes the same "commands"
        // message later. Stale-while-revalidate: a per-cwd cache is shown instantly, then the
        // live fetch refreshes both the UI and the cache. On a cold cache the UI shows a
        // "loading" note for the few seconds the fetch (CLI startup + SessionStart hooks) takes.
        private void SendCommands()
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    if (_session == null)
                    {
                        try { var d = await GetWorkingDirectoryAsync(); if (!string.IsNullOrEmpty(d)) _cwd = d; }
                        catch { }
                    }

                    // 1) Instant fill from cache, or signal "loading" when the cache is cold.
                    var cached = SlashCommandCache.Load(_cwd);
                    if (cached != null && cached.Count > 0)
                        _host.PostMessage("commands", new { commands = cached });
                    else
                        _host.PostMessage("commandsLoading", new { on = true });

                    // 2) Live fetch refreshes the UI + cache (then clears the loading note).
                    try
                    {
                        var commands = await SlashCommandService.ListAsync(_cwd);
                        if (commands.Count > 0)
                        {
                            SlashCommandCache.Save(_cwd, commands);
                            _host.PostMessage("commands", new { commands = commands });
                        }
                    }
                    finally
                    {
                        _host.PostMessage("commandsLoading", new { on = false });
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("SendCommands: " + ex.Message);
                }
            });
        }

        // First-run readiness: is the claude CLI installed, and is there a stored login? Drives
        // the onboarding banner so a new user is guided to install / log in instead of hitting a
        // raw "could not launch" / exit-code error. No network call (token validity is not checked
        // here — an expired token still reports loggedIn=true; a failed turn then guides re-login).
        private void SendSetupStatus()
        {
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    bool cliFound = ClaudeCliLocator.IsInstalled();
                    bool loggedIn = cliFound && AccountService.HasStoredToken();
                    // npm presence decides whether the banner offers a one-click "Install CLI"
                    // (runs npm in a visible terminal) or just a "get Node.js" link.
                    bool npmFound = !cliFound && IsNpmAvailable();
                    _host.PostMessage("setup", new { cliFound = cliFound, loggedIn = loggedIn, npmFound = npmFound });
                }
                catch (Exception ex) { Log.Write("SendSetupStatus: " + ex.Message); }
            });
        }

        // True when an npm launcher (npm.cmd / npm.exe / npm) is on PATH — used to gate the
        // optional one-click CLI install. We don't try to bootstrap Node itself.
        private static bool IsNpmAvailable()
        {
            try
            {
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                foreach (var dir in pathEnv.Split(Path.PathSeparator))
                {
                    if (string.IsNullOrWhiteSpace(dir)) continue;
                    string d;
                    try { d = dir.Trim(); } catch { continue; }
                    if (File.Exists(Path.Combine(d, "npm.cmd")) ||
                        File.Exists(Path.Combine(d, "npm.exe")) ||
                        File.Exists(Path.Combine(d, "npm")))
                        return true;
                }
            }
            catch { }
            return false;
        }

        // Runs the global CLI install in a VISIBLE terminal so the user sees progress + any errors
        // and explicitly consents — never a silent background install. The command is a fixed
        // literal (no webview input), so there is no injection surface. After it finishes the user
        // clicks "Re-check" (a VS restart may be needed for the new claude to be on VS's PATH).
        private void LaunchCliInstall()
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/k npm install -g @anthropic-ai/claude-code",
                    WorkingDirectory = string.IsNullOrEmpty(_cwd) ? Environment.CurrentDirectory : _cwd,
                    UseShellExecute = true,
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex) { Log.Write("LaunchCliInstall: " + ex.Message); }
        }

        // Opens an interactive `claude` session in a console at the working dir. Used for the
        // first-run login (`/login`) and for completing MCP OAuth via `/mcp` — the headless CLI
        // has no non-interactive auth path. Once authenticated, the credentials are shared, so
        // this extension's sessions pick them up. No webview-controlled input reaches the command
        // line (the only argument is the located CLI path), so there is no injection surface.
        private void LaunchClaudeTerminal()
        {
            try
            {
                var cli = ClaudeCliLocator.Locate();
                string launch = (!string.IsNullOrEmpty(cli.ResolvedPath) && File.Exists(cli.ResolvedPath))
                    ? "\"" + cli.ResolvedPath + "\""
                    : "claude";
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/k " + launch,
                    WorkingDirectory = string.IsNullOrEmpty(_cwd) ? Environment.CurrentDirectory : _cwd,
                    UseShellExecute = true,
                };
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex) { Log.Write("LaunchMcpAuthTerminal: " + ex.Message); }
        }

        // Effort levels available per model. Opus exposes the full extended-thinking
        // range plus Ultracode workflows; Sonnet/Haiku expose progressively fewer.
        private static object BuildEffortsByModel()
        {
            var off = new { id = "none", name = "Off" };
            var low = new { id = "low", name = "Low" };
            var medium = new { id = "medium", name = "Medium" };
            var high = new { id = "high", name = "High" };
            var extrahigh = new { id = "extrahigh", name = "Extra high" };
            var max = new { id = "max", name = "Max" };
            var ultracode = new { id = "ultracode", name = "Ultracode" };

            var opus = new object[] { off, low, medium, high, extrahigh, max, ultracode };
            var sonnet = new object[] { off, low, medium, high, max };
            var haiku = new object[] { off, low, medium, high };

            return new System.Collections.Generic.Dictionary<string, object[]>
            {
                ["default"] = opus,
                ["sonnet"] = sonnet,
                ["haiku"] = haiku,
            };
        }

        private void SendInit()
        {
            _host.PostMessage("init", new
            {
                version = "0.2.24",
                theme = _theme.GetThemeVariables(),
                model = _model,
                effort = _effort,
                permissionMode = _permissionMode,
                showThinking = _showThinking,
                models = new object[]
                {
                    new { id = "default", name = "Default (recommended)", desc = "Opus 4.8 with 1M context · Most capable for complex work" },
                    new { id = "sonnet", name = "Sonnet", desc = "Sonnet 4.6 · Best for everyday tasks" },
                    new { id = "haiku", name = "Haiku", desc = "Haiku 4.5 · Fastest for quick answers" },
                },
                modes = new object[]
                {
                    new { id = "default", name = "Ask before edits", desc = "Claude asks for approval before each edit", icon = "✋" },
                    new { id = "acceptEdits", name = "Edit automatically", desc = "Claude edits files without asking", icon = "✎" },
                    new { id = "plan", name = "Plan mode", desc = "Explore and present a plan before editing", icon = "▤" },
                    new { id = "bypassPermissions", name = "Auto mode", desc = "Claude runs any tool automatically", icon = "⚡" },
                },
                effortsByModel = BuildEffortsByModel(),
            });

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    var dir = await GetWorkingDirectoryAsync();
                    if (!string.IsNullOrEmpty(dir)) { _cwd = dir; _host.PostMessage("init", new { cwd = _cwd }); }

                    // Populate the / palette with the full CLI command set up front, now that
                    // cwd is resolved (so project .claude/commands are included) — before the
                    // user sends a first message and the live session would otherwise be needed.
                    SendCommands();

                    // First-run readiness (CLI installed? logged in?) drives the onboarding banner.
                    SendSetupStatus();

                    // Restore the prior options (and conversation, if any) for this working dir.
                    if (_record == null)
                    {
                        var rec = SessionStore.Load(_cwd);
                        if (rec != null)
                        {
                            bool hasMsgs = rec.Messages != null && rec.Messages.Count > 0;
                            _record = rec;
                            if (hasMsgs && !string.IsNullOrEmpty(rec.SessionId)) _pendingResumeId = rec.SessionId;
                            _model = InputValidation.SanitizeChoice(rec.Model, InputValidation.AllowedModels, "default");
                            _permissionMode = InputValidation.SanitizeChoice(rec.Mode, InputValidation.AllowedModes, "default");
                            _effort = InputValidation.SanitizeChoice(rec.Effort, InputValidation.AllowedEfforts, "none");
                            _showThinking = rec.ShowThinking;
                            _host.PostMessage("restore", new
                            {
                                messages = hasMsgs ? rec.Messages : new System.Collections.Generic.List<StoredMessage>(),
                                model = _model,
                                mode = _permissionMode,
                                effort = _effort,
                                showThinking = _showThinking,
                            });
                        }
                    }
                }
                catch { }
            }).FireAndForget();
        }

        private void HandleSend(JsonElement payload)
        {
            string text = GetStr(payload, "text") ?? string.Empty;
            var images = ParseImages(payload);

            Log.WriteVerbose("HandleSend: text=" + (text.Length > 60 ? text.Substring(0, 60) : text));
            _host.PostMessage("status", new { state = "thinking" });

            // Spawn/send on a background thread so nothing on the UI thread can block it.
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    // Best-effort: attach the active file/selection as context (with a timeout
                    // so a slow IDE call can never hold up the message).
                    string prefix = string.Empty;
                    try
                    {
                        var ctx = BuildContextPrefixAsync();
                        if (await System.Threading.Tasks.Task.WhenAny(ctx, System.Threading.Tasks.Task.Delay(2000)) == ctx)
                            prefix = await ctx ?? string.Empty;
                    }
                    catch { }

                    await EnsureWorkingDirectoryAsync();
                    EnsureSession();
                    _session.SendUserMessage(prefix + text, images);
                    AppendHistory("user", text);
                    Log.Write("HandleSend: message sent");
                }
                catch (Exception ex)
                {
                    Log.Write("HandleSend EXCEPTION: " + ex);
                    _host.PostMessage("error", new { message = ex.ToString() });
                    _host.PostMessage("status", new { state = "idle" });
                }
            });
        }

        private async System.Threading.Tasks.Task<string> BuildContextPrefixAsync()
        {
            try
            {
                var sel = await _ide.GetActiveSelectionAsync();
                var openFiles = await _ide.GetOpenFilesAsync();
                var diags = await _ide.GetDiagnosticsAsync(30);

                bool any = false;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("<ide-context>");

                if (sel != null && !string.IsNullOrEmpty(sel.FilePath))
                {
                    any = true;
                    sb.Append("Active file: ").AppendLine(sel.FilePath);
                    if (sel.HasSelection)
                    {
                        sb.AppendLine("Selected lines " + sel.StartLine + "-" + sel.EndLine + ":");
                        sb.AppendLine("```" + sel.LanguageId);
                        var text = sel.Text.Length > 4000 ? sel.Text.Substring(0, 4000) + "\n…(truncated)" : sel.Text;
                        sb.AppendLine(text);
                        sb.AppendLine("```");
                    }
                }

                if (openFiles != null && openFiles.Count > 0)
                {
                    any = true;
                    sb.AppendLine("Open editors:");
                    for (int i = 0; i < openFiles.Count && i < 20; i++) sb.Append("- ").AppendLine(openFiles[i]);
                }

                if (diags != null && diags.Count > 0)
                {
                    any = true;
                    sb.AppendLine("Problems (VS Error List):");
                    for (int i = 0; i < diags.Count && i < 30; i++)
                    {
                        var d = diags[i];
                        sb.AppendLine("- [" + d.Level + "] " + d.File + ":" + d.Line + " " + d.Description);
                    }
                }

                sb.AppendLine("</ide-context>");
                sb.AppendLine();
                return any ? sb.ToString() : string.Empty;
            }
            catch { return string.Empty; }
        }

        private void EnsureSession()
        {
            if (_session != null && _session.IsRunning && !_optionsDirty)
            {
                return;
            }

            string resume = null;
            if (_session != null)
            {
                // Restart to apply new model/mode but keep the conversation via --resume.
                resume = _session.SessionId;
                _session.Dispose();
                _session = null;
            }

            // First start after a restore: resume the persisted CLI session.
            if (resume == null && !string.IsNullOrEmpty(_pendingResumeId))
            {
                resume = _pendingResumeId;
                _pendingResumeId = null;
            }

            _optionsDirty = false;

            var options = new ClaudeSessionOptions
            {
                WorkingDirectory = _cwd,
                Model = _model,
                PermissionMode = _permissionMode,
                Effort = _effort,
                ResumeSessionId = resume,
            };

            Log.Write("starting claude session, cwd=" + _cwd);
            _session = new ClaudeSession(options);
            HookSession(_session);
            _session.Start();
        }

        private void HookSession(ClaudeSession s)
        {
            s.SystemInit += i =>
            {
                _tools = i.Tools ?? new List<string>();
                _mcpServers = i.McpServers ?? new List<string>();
                _host.PostMessage("system", new { subtype = "init", model = i.Model, cwd = i.Cwd });
                _host.PostMessage("commands", new { commands = i.SlashCommands });
            };
            s.AssistantStart += () => _host.PostMessage("assistantStart", new { });
            s.TextDelta += t => _host.PostMessage("assistantDelta", new { text = t });
            s.ThinkingDelta += t => _host.PostMessage("thinkingDelta", new { text = t });
            s.ToolUse += t =>
            {
                _host.PostMessage("toolUse", new { id = t.Id, name = t.Name, input = RawJson(t.InputJson) });
                TrackEditedFile(t);
            };
            s.ToolResult += r =>
            {
                _host.PostMessage("toolResult", new { id = r.ToolUseId, content = r.Content, isError = r.IsError });
                if (!r.IsError && r.ToolUseId != null && _editedFiles.TryGetValue(r.ToolUseId, out var snap))
                {
                    _editedFiles.Remove(r.ToolUseId);
                    ThreadHelper.JoinableTaskFactory.RunAsync(async () => await ShowEditAsync(snap)).FireAndForget();
                }
            };
            s.AssistantEnd += () => _host.PostMessage("assistantEnd", new { });
            s.Result += r =>
            {
                _host.PostMessage("result", new
                {
                    costUsd = r.CostUsd,
                    inputTokens = r.InputTokens,
                    outputTokens = r.OutputTokens,
                    cacheReadTokens = r.CacheReadTokens,
                    cacheCreationTokens = r.CacheCreationTokens,
                    contextWindow = r.ContextWindow,
                    model = r.Model,
                    durationMs = r.DurationMs,
                });
                _host.PostMessage("status", new { state = "idle" });

                if (!_compacting && !r.IsError)
                {
                    AppendHistory("assistant", r.Text);
                }

                if (_compacting)
                {
                    _compacting = false;
                    var summary = r.Text ?? "";
                    _ = System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            _session?.Dispose();
                            _session = null;
                            _optionsDirty = false;       // fresh session, no --resume -> shrinks context
                            _host.PostMessage("compacted", new { summary });
                            EnsureSession();
                            _session.SendUserMessage(
                                "[Compacted summary of our prior conversation — continue from here]\n\n" + summary, null);
                        }
                        catch (Exception ex) { _host.PostMessage("error", new { message = ex.Message }); }
                    });
                }
            };
            s.PermissionRequest += p => _host.PostMessage("permission", new { id = p.RequestId, tool = p.ToolName, input = RawJson(p.InputJson) });
            s.ErrorEvent += m => _host.PostMessage("error", new { message = m });
            s.Exited += code =>
            {
                _host.PostMessage("status", new { state = "idle" });
                Log.Write("claude process exited (code " + code + ")");
                if (code != 0) _host.PostMessage("error", new { message = "claude exited (code " + code + "). Check that you are logged in (run 'claude' once in a terminal)." });
            };
            s.Diagnostic += d => Log.Write("diag: " + d);
        }

        private void TrackEditedFile(ToolUseInfo t)
        {
            if (t?.Name == null) return;
            switch (t.Name)
            {
                case "Edit":
                case "Write":
                case "MultiEdit":
                case "NotebookEdit":
                    try
                    {
                        var input = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrEmpty(t.InputJson) ? "{}" : t.InputJson);
                        if (input.TryGetProperty("file_path", out var fp) && fp.ValueKind == JsonValueKind.String)
                        {
                            var path = fp.GetString();
                            string old = null;
                            try { if (File.Exists(path)) old = File.ReadAllText(path); } catch { }
                            _editedFiles[t.Id] = new EditSnapshot { Path = path, OldText = old };
                        }
                    }
                    catch { }
                    break;
            }
        }

        private void PickImage()
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Attach image",
                    Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|All files|*.*",
                    Multiselect = true,
                };
                if (dlg.ShowDialog() != true) return;
                foreach (var f in dlg.FileNames)
                {
                    try
                    {
                        var data = Convert.ToBase64String(File.ReadAllBytes(f));
                        _host.PostMessage("attachImage", new
                        {
                            mediaType = MediaTypeForExt(Path.GetExtension(f)),
                            data,
                            name = Path.GetFileName(f),
                        });
                    }
                    catch { }
                }
            }).FireAndForget();
        }

        private void PickFile()
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Add file to context",
                    Filter = "All files|*.*",
                    Multiselect = true,
                };
                if (dlg.ShowDialog() != true) return;
                var refs = string.Join(" ", System.Array.ConvertAll(dlg.FileNames, p => "@" + p));
                _host.PostMessage("insertText", new { text = refs + " " });
            }).FireAndForget();
        }

        // Open a VS diff window comparing the file before Claude's edit (temp) to the new content.
        private async System.Threading.Tasks.Task ShowEditAsync(EditSnapshot snap)
        {
            if (snap == null || string.IsNullOrEmpty(snap.Path)) return;
            try
            {
                if (snap.OldText == null) { await _ide.OpenFileAsync(snap.Path); return; }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var dir = Path.Combine(Path.GetTempPath(), "ClaudeCodeVS", "diff");
                Directory.CreateDirectory(dir);
                var tmp = Path.Combine(dir, Guid.NewGuid().ToString("N") + Path.GetExtension(snap.Path));
                File.WriteAllText(tmp, snap.OldText);

                var diff = await VS.GetServiceAsync<SVsDifferenceService, IVsDifferenceService>();
                var name = Path.GetFileName(snap.Path);
                diff?.OpenComparisonWindow2(tmp, snap.Path, "Claude edit: " + name, name,
                    "Before", "After (Claude)", null, null,
                    (uint)__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary);
            }
            catch (Exception ex)
            {
                Log.Write("ShowEdit failed: " + ex.Message);
                try { await _ide.OpenFileAsync(snap.Path); } catch { }
            }
        }

        // Enumerate workspace files for the @-mention picker.
        private void SendFiles()
        {
            var cwd = _cwd;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var files = IdeContextService.EnumerateWorkspaceFiles(cwd, 800);
                    _host.PostMessage("files", new { files });
                }
                catch { }
            });
        }

        private void HandleCompact()
        {
            if (_session == null || !_session.IsRunning) return;
            _compacting = true;
            _host.PostMessage("status", new { state = "thinking" });
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    _session.SendUserMessage(
                        "Summarize our entire conversation so far into a concise but complete context brief: " +
                        "key decisions, code/files changed (with paths), current goal, and open tasks. Output ONLY the brief.",
                        null);
                }
                catch (Exception ex) { _host.PostMessage("error", new { message = ex.Message }); _compacting = false; }
            });
        }

        private static string MediaTypeForExt(string ext)
        {
            switch ((ext ?? "").ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".bmp": return "image/bmp";
                default: return "image/png";
            }
        }

        private void HandlePermissionResponse(JsonElement payload)
        {
            string id = GetStr(payload, "id");
            string behavior = GetStr(payload, "behavior") ?? "deny";
            if (behavior == "allow_always") behavior = "allow";
            _session?.RespondToPermission(id, behavior == "deny" ? "deny" : "allow", null);
        }

        private void ResetSession()
        {
            _session?.Dispose();
            _session = null;
            _pendingResumeId = null;
            _record = null;
            SessionStore.Clear(_cwd);
            _host.PostMessage("clear", new { });
        }

        // Persist a turn to the per-cwd session store so the conversation can be restored later.
        private void AppendHistory(string role, string text)
        {
            try
            {
                if (_record == null) _record = new SessionRecord();
                _record.Messages.Add(new StoredMessage { Role = role, Text = text ?? string.Empty });
                _record.SessionId = _session?.SessionId ?? _record.SessionId;
                _record.Model = _model;
                _record.Mode = _permissionMode;
                _record.Effort = _effort;
                _record.ShowThinking = _showThinking;
                SessionStore.Save(_cwd, _record);
            }
            catch { }
        }

        // Persist the current composer options (model / permission mode / effort / show-thinking)
        // immediately when the user changes one, even before any message is sent — otherwise an
        // option change followed by closing VS would be lost.
        private void SaveOptions()
        {
            try
            {
                if (_record == null) _record = new SessionRecord();
                _record.Model = _model;
                _record.Mode = _permissionMode;
                _record.Effort = _effort;
                _record.ShowThinking = _showThinking;
                SessionStore.Save(_cwd, _record);
            }
            catch { }
        }

        private static List<ImageInput> ParseImages(JsonElement payload)
        {
            var list = new List<ImageInput>();
            if (payload.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
            {
                foreach (var img in imgs.EnumerateArray())
                {
                    list.Add(new ImageInput
                    {
                        MediaType = img.TryGetProperty("mediaType", out var m) ? m.GetString() : "image/png",
                        Data = img.TryGetProperty("data", out var d) ? d.GetString() : null,
                    });
                }
            }
            return list.Count > 0 ? list : null;
        }

        private async System.Threading.Tasks.Task EnsureWorkingDirectoryAsync()
        {
            // The claude process inherits its cwd at launch and can't change it afterwards,
            // so resolve only before the first start. The tool window is usually restored
            // (docked next to Solution Explorer) before the solution finishes opening, which
            // leaves the OnLoaded value stale (the user-home fallback) — re-resolve here once
            // the solution is actually loaded so claude runs in the project folder.
            if (_session != null) return;
            try
            {
                var dir = await GetWorkingDirectoryAsync();
                if (!string.IsNullOrEmpty(dir)) _cwd = dir;
            }
            catch { }
        }

        private async System.Threading.Tasks.Task<string> GetWorkingDirectoryAsync()
        {
            try
            {
                var solution = await VS.Solutions.GetCurrentSolutionAsync();
                if (solution?.FullPath != null)
                {
                    var dir = Path.GetDirectoryName(solution.FullPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
                }
            }
            catch { }

            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        private static object RawJson(string json)
        {
            try { return JsonSerializer.Deserialize<JsonElement>(string.IsNullOrEmpty(json) ? "{}" : json); }
            catch { return new { }; }
        }

        private static string GetStr(JsonElement el, string name)
            => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        private static bool GetBool(JsonElement el, string name, bool fallback)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v))
            {
                if (v.ValueKind == JsonValueKind.True) return true;
                if (v.ValueKind == JsonValueKind.False) return false;
            }
            return fallback;
        }

        private void TryOpenExternal(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            // Security: ShellExecute will launch ANY string (apps, files, scripts). Restrict to
            // real web URLs so a crafted "openExternal" message can't run a local program.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                Log.Write("openExternal blocked (non-http url): " + url);
                return;
            }
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch { }
        }
    }
}

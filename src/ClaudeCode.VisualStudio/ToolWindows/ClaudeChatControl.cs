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
        private string _permissionMode = "acceptEdits";
        private string _effort = "none";
        private bool _optionsDirty;
        private bool _compacting;

        private readonly IdeContextService _ide = new IdeContextService();
        private readonly ThemeService _theme = new ThemeService();
        private readonly Dictionary<string, string> _editedFiles = new Dictionary<string, string>();

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
                    _model = GetStr(message.Payload, "model") ?? "default";
                    _optionsDirty = true;
                    break;
                case "setPermissionMode":
                    _permissionMode = GetStr(message.Payload, "mode") ?? "acceptEdits";
                    _optionsDirty = true;
                    break;
                case "setEffort":
                    _effort = GetStr(message.Payload, "effort") ?? "none";
                    _optionsDirty = true;
                    break;
                case "getContext":
                    SendContext();
                    break;
                case "getUsage":
                    FetchAndSendAccountData();
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
                    model = _model,
                    effort = _effort,
                    permissionMode = _permissionMode,
                    sessionId = _session?.SessionId,
                });
            }).FireAndForget();
        }

        private void SendInit()
        {
            _host.PostMessage("init", new
            {
                version = "0.1.0",
                theme = _theme.GetThemeVariables(),
                model = _model,
                effort = _effort,
                permissionMode = _permissionMode,
                models = new object[]
                {
                    new { id = "default", name = "Default (recommended)", desc = "Opus 4.8 with 1M context · Most capable for complex work" },
                    new { id = "opus", name = "Opus", desc = "Opus 4.8 · Most capable" },
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
                efforts = new object[]
                {
                    new { id = "none", name = "Off" },
                    new { id = "low", name = "Low" },
                    new { id = "medium", name = "Medium" },
                    new { id = "high", name = "High" },
                },
            });

            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                var folders = await _ide.GetWorkspaceFoldersAsync();
                if (folders.Count > 0)
                {
                    _host.PostMessage("init", new { cwd = folders[0] });
                }
            }).FireAndForget();
        }

        private void HandleSend(JsonElement payload)
        {
            string text = GetStr(payload, "text") ?? string.Empty;
            var images = ParseImages(payload);

            Log.Write("HandleSend: text=" + (text.Length > 60 ? text.Substring(0, 60) : text));
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

                    EnsureSession();
                    _session.SendUserMessage(prefix + text, images);
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
                if (sel == null || string.IsNullOrEmpty(sel.FilePath)) return string.Empty;

                var sb = new System.Text.StringBuilder();
                sb.AppendLine("<ide-context>");
                sb.Append("Active file: ").AppendLine(sel.FilePath);
                if (sel.HasSelection)
                {
                    sb.AppendLine("Selected lines " + sel.StartLine + "-" + sel.EndLine + ":");
                    sb.AppendLine("```" + sel.LanguageId);
                    var text = sel.Text.Length > 4000 ? sel.Text.Substring(0, 4000) + "\n…(truncated)" : sel.Text;
                    sb.AppendLine(text);
                    sb.AppendLine("```");
                }
                sb.AppendLine("</ide-context>");
                sb.AppendLine();
                return sb.ToString();
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
                _host.PostMessage("system", new { subtype = "init", model = i.Model, cwd = i.Cwd });
                _host.PostMessage("commands", new { commands = i.SlashCommands });
            };
            s.AssistantStart += () => _host.PostMessage("assistantStart", new { });
            s.TextDelta += t => _host.PostMessage("assistantDelta", new { text = t });
            s.ThinkingDelta += t => { /* thinking stream available; hidden by default */ };
            s.ToolUse += t =>
            {
                _host.PostMessage("toolUse", new { id = t.Id, name = t.Name, input = RawJson(t.InputJson) });
                TrackEditedFile(t);
            };
            s.ToolResult += r =>
            {
                _host.PostMessage("toolResult", new { id = r.ToolUseId, content = r.Content, isError = r.IsError });
                if (!r.IsError && r.ToolUseId != null && _editedFiles.TryGetValue(r.ToolUseId, out var path))
                {
                    _editedFiles.Remove(r.ToolUseId);
                    ThreadHelper.JoinableTaskFactory.RunAsync(async () => await _ide.OpenFileAsync(path)).FireAndForget();
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
                            _host.PostMessage("clear", new { });
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
                            _editedFiles[t.Id] = fp.GetString();
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
            _host.PostMessage("clear", new { });
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

        private void TryOpenExternal(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }
    }
}

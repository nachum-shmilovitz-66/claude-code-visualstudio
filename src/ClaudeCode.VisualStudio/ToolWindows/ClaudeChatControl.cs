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
        private readonly DebugContextService _debug = new DebugContextService();
        private readonly ThemeService _theme = new ThemeService();
        private readonly Dictionary<string, EditSnapshot> _editedFiles = new Dictionary<string, EditSnapshot>();

        private sealed class EditSnapshot { public string Path; public string OldText; }
        private List<string> _tools = new List<string>();
        private List<string> _mcpServers = new List<string>();
        private SessionRecord _record;        // persisted transcript for the current cwd
        private string _pendingResumeId;      // CLI session id to --resume on next start (restore)
        private bool _solutionHooked;         // subscribed to solution-load events (restore retry)
        private bool _updateWatchRunning;     // polling for a `claude update` to land
        private bool _updateRunning;          // a background `claude update` process is in flight

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
            Unloaded += (s, e) => { UnhookSolutionLoad(); _session?.Dispose(); };
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD100:Avoid async void methods", Justification = "Event handler")]
        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            try
            {
                // Pass the current VS theme so the WebView paints in-theme from the first frame
                // (no white flash before app.js applies colors).
                await _host.InitializeAsync(_theme.GetThemeVariables());
            }
            catch (Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
            {
                // VS 2022+ installs the WebView2 Runtime; older Windows / VS 2019 machines may
                // lack it, so point at the Evergreen installer instead of dumping the exception.
                var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(12) };
                panel.Children.Add(new TextBlock
                {
                    Text = "Claude Code needs the Microsoft Edge WebView2 Runtime, which is not installed.",
                    TextWrapping = TextWrapping.Wrap,
                });
                var link = new System.Windows.Documents.Hyperlink(
                    new System.Windows.Documents.Run("Download the WebView2 Runtime"))
                {
                    NavigateUri = new Uri("https://go.microsoft.com/fwlink/p/?LinkId=2124703"),
                };
                link.RequestNavigate += (s2, e2) =>
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e2.Uri.AbsoluteUri) { UseShellExecute = true });
                panel.Children.Add(new TextBlock(link) { Margin = new Thickness(0, 8, 0, 0) });
                panel.Children.Add(new TextBlock
                {
                    Text = "Then reopen this window.",
                    Margin = new Thickness(0, 8, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                });
                Content = panel;
                return;
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

            // Listen for debugger break events so the chat can surface a live exception/pause.
            try
            {
                _debug.Break += OnDebugBreak;
                await _debug.StartAsync();
            }
            catch { }
        }

        // The debugger paused (breakpoint / step / thrown exception). Surface it in the
        // transcript so the user can ask Claude about the live runtime state.
        private void OnDebugBreak(DebugBreakInfo info)
        {
            if (info == null) return;
            _host.PostMessage("debugBreak", new
            {
                reason = info.Reason,
                exception = info.Exception,
                file = info.File,
                line = info.Line,
                function = info.Function,
            });
        }

        private void OnMessageReceived(WebMessage message)
        {
            Log.WriteVerbose("WebMessage: " + message.Type);
            switch (message.Type)
            {
                case "ready":
                    SendInit();
                    break;
                case "diag":
                    // Layout/scroll breadcrumbs from the page. Release builds ship without
                    // DevTools, so this is the only window into WebView-side behaviour. The
                    // page sends numbers only; cap the length so it can't be used to bulk-write.
                    {
                        var t = GetStr(message.Payload, "text") ?? string.Empty;
                        Log.Write("web: " + (t.Length > 200 ? t.Substring(0, 200) : t));
                    }
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
                    _model = InputValidation.SanitizeModel(GetStr(message.Payload, "model"), "default");
                    _optionsDirty = true;
                    SaveOptions();
                    break;
                case "setPermissionMode":
                    _permissionMode = InputValidation.SanitizeChoice(GetStr(message.Payload, "mode"), InputValidation.AllowedModes, "default");
                    // Apply to the live process when it can take it — a mode is usually switched
                    // *because* a prompt is on screen, and waiting for the next turn's relaunch
                    // would keep asking through the rest of the current one. Otherwise fall back to
                    // relaunching with the new mode on the next send.
                    if (_session == null || !_session.SetPermissionMode(_permissionMode)) _optionsDirty = true;
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
                case "updateCli":
                    LaunchCliUpdate();
                    break;
                case "updateCliInTerminal":
                    LaunchCliUpdateInTerminal();
                    break;
                case "recheckSetup":
                    // An explicit user re-check must mean "ask everything again" — including the
                    // npm "latest", which is otherwise cached for the lifetime of the process.
                    SendSetupStatus(forceRefresh: true);
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
                case "openFile":
                    OpenFileFromWebview(message.Payload);
                    break;
                case "openDiff":
                    OpenDiffFromWebview(message.Payload);
                    break;
            }
        }

        // The inline diff card's "Open diff" button — open the full native VS Before/After diff for
        // a specific edit, on demand (looked up by tool-use id from the kept pre-edit snapshot).
        private void OpenDiffFromWebview(JsonElement payload)
        {
            string id = GetStr(payload, "id");
            if (string.IsNullOrEmpty(id)) return;
            if (_editedFiles.TryGetValue(id, out var snap))
                ThreadHelper.JoinableTaskFactory.RunAsync(async () => await ShowEditAsync(snap)).FireAndForget();
        }

        // Open a local file (optionally at a line) in the VS editor — used by the selection
        // chip on a sent message. Only opens an existing file in the editor; never executes.
        //
        // Security: the path arrives from the (local but untrusted) WebView, so it is confined to
        // the workspace before being opened — otherwise a crafted openFile message could pull any
        // readable file on disk (a credential store, another project's source) into the editor,
        // where its contents then become attachable chat context. The legitimate sender is the
        // selection chip, which can only ever name a file that is open in the IDE, so
        // currently-open documents are allowed too — that keeps the chip working for files edited
        // from outside the solution folder.
        private void OpenFileFromWebview(JsonElement payload)
        {
            string path = GetStr(payload, "path");
            if (string.IsNullOrEmpty(path)) return;
            int line = GetInt(payload, "line", 0);
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    var roots = new List<string>();
                    if (!string.IsNullOrEmpty(_cwd)) roots.Add(_cwd);
                    try
                    {
                        var folders = await _ide.GetWorkspaceFoldersAsync();
                        if (folders != null) roots.AddRange(folders);
                    }
                    catch { }

                    if (!InputValidation.IsUnderAnyRoot(path, roots))
                    {
                        List<string> open = null;
                        try { open = await _ide.GetOpenFilesAsync(); } catch { }
                        if (!InputValidation.IsSamePath(path, open))
                        {
                            Log.Write("openFile blocked (outside workspace and not an open document)");
                            return;
                        }
                    }

                    await _ide.OpenFileAsync(path, line > 0 ? (int?)line : null);
                }
                catch { }
            }).FireAndForget();
        }

        private void FetchAndSendAccountData()
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                var data = await AccountService.FetchAsync();
                var limits = new List<object>();
                foreach (var l in data.Limits)
                    limits.Add(new { name = l.Name, percent = l.Percent, resetsIn = l.ResetsIn, severity = l.Severity });

                UsagePeriodInsights day = null, week = null;
                try { UsageInsightsService.Compute(out day, out week); }
                catch (Exception ex) { Log.Write("FetchAndSendAccountData insights: " + ex.Message); }

                _host.PostMessage("accountData", new
                {
                    authMethod = data.AuthMethod,
                    email = data.Email,
                    organization = data.Organization,
                    plan = data.Plan,
                    limits = limits.ToArray(),
                    extraUsage = data.ExtraUsage == null ? (object)null : new
                    {
                        enabled = data.ExtraUsage.Enabled,
                        usedCredits = data.ExtraUsage.UsedCredits,
                        monthlyLimit = data.ExtraUsage.MonthlyLimit,
                        utilization = data.ExtraUsage.Utilization,
                        currency = data.ExtraUsage.Currency,
                        decimalPlaces = data.ExtraUsage.DecimalPlaces,
                    },
                    insights = new
                    {
                        day = day == null ? (object)null : new { totalTokens = day.TotalTokens, sessions = day.Sessions, pctOver150k = day.PctOver150k, pctSidechain = day.PctSidechain },
                        week = week == null ? (object)null : new { totalTokens = week.TotalTokens, sessions = week.Sessions, pctOver150k = week.PctOver150k, pctSidechain = week.PctSidechain },
                    },
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
                var dbg = await _debug.GetDebugStateAsync();

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
                    dbgActive = dbg?.IsActive ?? false,
                    dbgMode = dbg?.Mode,
                    dbgProcess = dbg?.ProcessName,
                    dbgFunction = dbg?.Function,
                    dbgFile = dbg?.File,
                    dbgLine = dbg?.Line ?? 0,
                    dbgException = dbg?.Exception,
                    dbgLocals = dbg?.Locals?.Count ?? 0,
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
        // Cached for the process lifetime: the npm "latest" tag changes rarely, and we re-read the
        // *installed* version each time, so the outdated warning clears as soon as the user updates.
        private static string _cachedLatestCli;

        private void SendSetupStatus(bool forceRefresh = false)
        {
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    if (forceRefresh) _cachedLatestCli = null;
                    bool cliFound = ClaudeCliLocator.IsInstalled();
                    bool loggedIn = cliFound && AccountService.HasStoredToken();
                    // npm presence decides whether the banner offers a one-click "Install CLI"
                    // (runs npm in a visible terminal) or just a "get Node.js" link.
                    bool npmFound = !cliFound && IsNpmAvailable();

                    // Version check is general / future-proof: read the installed version and the
                    // current npm "latest" at runtime, compare numerically — no hardcoded versions.
                    // Only run it once the CLI is usable so the not-installed / not-logged-in
                    // banners still post instantly (this does a process + network call).
                    string cliVersion = null, latestCliVersion = null;
                    bool cliOutdated = false;
                    if (cliFound && loggedIn)
                    {
                        cliVersion = GetInstalledCliVersion();
                        latestCliVersion = GetLatestCliVersion();
                        cliOutdated = IsCliOutdated(cliVersion, latestCliVersion);
                        Log.Write("setup: cli=" + (cliVersion ?? "?") + " latest=" +
                                  (latestCliVersion ?? "?") + " outdated=" + cliOutdated +
                                  (forceRefresh ? " (forced)" : ""));
                    }

                    _host.PostMessage("setup", new
                    {
                        cliFound = cliFound,
                        loggedIn = loggedIn,
                        npmFound = npmFound,
                        cliVersion = cliVersion,
                        latestCliVersion = latestCliVersion,
                        cliOutdated = cliOutdated,
                    });
                }
                catch (Exception ex) { Log.Write("SendSetupStatus: " + ex.Message); }
            });
        }

        // Runs `claude --version` and pulls the X.Y.Z it prints (e.g. "2.1.170 (Claude Code)").
        private static string GetInstalledCliVersion()
        {
            try
            {
                var cli = ClaudeCliLocator.Locate();
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = cli.FileName,
                    Arguments = cli.ArgumentPrefix + "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (p == null) return null;
                    string outp = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } return null; }
                    var m = System.Text.RegularExpressions.Regex.Match(outp ?? string.Empty, @"\d+\.\d+\.\d+");
                    return m.Success ? m.Value : null;
                }
            }
            catch (Exception ex) { Log.Write("GetInstalledCliVersion: " + ex.Message); return null; }
        }

        // Latest published version from the npm registry (the "latest" dist-tag). Cached after the
        // first success; returns null on any failure (offline etc) so we simply don't warn.
        private static string GetLatestCliVersion()
        {
            if (!string.IsNullOrEmpty(_cachedLatestCli)) return _cachedLatestCli;
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                using (var wc = new System.Net.WebClient())
                {
                    wc.Headers.Add("User-Agent", "ClaudeCode-VS-Extension");
                    string json = wc.DownloadString("https://registry.npmjs.org/@anthropic-ai/claude-code/latest");
                    using (var doc = JsonDocument.Parse(json))
                    {
                        if (doc.RootElement.TryGetProperty("version", out var v))
                        {
                            _cachedLatestCli = v.GetString();
                            return _cachedLatestCli;
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Write("GetLatestCliVersion: " + ex.Message); }
            return null;
        }

        // True only when both versions parse and latest > installed (numeric major.minor.patch).
        private static bool IsCliOutdated(string installed, string latest)
        {
            var a = ParseVersion(installed);
            var b = ParseVersion(latest);
            if (a == null || b == null) return false;
            for (int i = 0; i < 3; i++)
            {
                if (b[i] > a[i]) return true;
                if (b[i] < a[i]) return false;
            }
            return false;
        }

        private static int[] ParseVersion(string v)
        {
            if (string.IsNullOrEmpty(v)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(v, @"(\d+)\.(\d+)\.(\d+)");
            if (!m.Success) return null;
            return new[]
            {
                int.Parse(m.Groups[1].Value),
                int.Parse(m.Groups[2].Value),
                int.Parse(m.Groups[3].Value),
            };
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

        // Updates the SAME claude the extension actually runs, via its built-in self-updater
        // (`claude update`). Using the LOCATED binary is what makes this correct: a hardcoded
        // `npm i -g` updates the npm copy, but the extension may resolve a different install
        // (e.g. the native ~/.local/bin build) — updating that one is the only thing that moves
        // the version the extension uses. Native self-update needs neither npm nor node.
        // Runs in a VISIBLE terminal (same consent model as install). The target is the located
        // CLI path (filesystem, never webview input), so there is no injection surface.
        private const int UpdateTimeoutMs = 10 * 60 * 1000;

        /// <summary>
        /// Runs <c>claude update</c> without a console window, reporting progress in the banner.
        /// <para>
        /// The visible terminal used to be the only sign the update had done anything, so it earned
        /// its place; now that completion is detected and confirmed in the UI, the window is just a
        /// leftover to close (<c>cmd /k</c> deliberately stays open). Output is captured instead —
        /// but a hidden process that fails or stalls must never be silent, so a non-zero exit or a
        /// timeout surfaces the output in the banner with a "Run in terminal" escape hatch, which
        /// is also the way out if the updater ever needs interactive input.
        /// </para>
        /// </summary>
        private void LaunchCliUpdate()
        {
            if (_updateRunning) { Log.Write("LaunchCliUpdate: already running"); return; }
            _updateRunning = true;

            _host.PostMessage("cliUpdate", new { state = "running" });
            WatchForCliUpdate();   // safety net: catches a swap that lands after the process exits

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                string before = GetInstalledCliVersion();
                string output = string.Empty;
                int exit = -1;
                bool timedOut = false;

                try
                {
                    // Same launcher resolution the extension uses everywhere else, so the binary
                    // that gets updated is the one the chat actually runs (npm shim included).
                    var cli = ClaudeCliLocator.Locate();
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = cli.FileName,
                        Arguments = cli.ArgumentPrefix + "update",
                        WorkingDirectory = string.IsNullOrEmpty(_cwd) ? Environment.CurrentDirectory : _cwd,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };

                    Log.Write("cli update (background): " + cli.ResolvedPath);
                    using (var p = System.Diagnostics.Process.Start(psi))
                    {
                        if (p == null) throw new InvalidOperationException("process did not start");

                        // Read both pipes before waiting: a full stderr buffer would deadlock us.
                        var stdout = p.StandardOutput.ReadToEndAsync();
                        var stderr = p.StandardError.ReadToEndAsync();

                        if (!p.WaitForExit(UpdateTimeoutMs))
                        {
                            timedOut = true;
                            try { p.Kill(); } catch { }
                        }
                        else
                        {
                            exit = p.ExitCode;
                        }

                        output = ((await stdout.ConfigureAwait(false) ?? string.Empty) + "\n" +
                                  (await stderr.ConfigureAwait(false) ?? string.Empty)).Trim();
                    }
                }
                catch (Exception ex)
                {
                    output = ex.Message;
                    Log.Write("LaunchCliUpdate: " + ex.Message);
                }
                finally { _updateRunning = false; }

                if (timedOut || exit != 0)
                {
                    Log.Write("cli update FAILED exit=" + (timedOut ? "timeout" : exit.ToString()));
                    _host.PostMessage("cliUpdate", new
                    {
                        state = "failed",
                        detail = timedOut
                            ? "Timed out after 10 minutes."
                            : Tail(output, 400),
                    });
                    return;
                }

                string after = GetInstalledCliVersion();
                Log.Write("cli update ok: " + (before ?? "?") + " -> " + (after ?? "?"));

                // Report the outcome directly rather than relying on the status message racing it.
                _host.PostMessage("cliUpdate", new
                {
                    state = "done",
                    version = after,
                    changed = !string.IsNullOrEmpty(after) && after != before,
                });
                SendSetupStatus(forceRefresh: true);
            });
        }

        // The original visible-terminal path, kept as the escape hatch offered when the background
        // update fails — some failures (auth, a prompt) are only resolvable interactively.
        private void LaunchCliUpdateInTerminal()
        {
            try
            {
                var cli = ClaudeCliLocator.Locate();
                string target = (!string.IsNullOrEmpty(cli.ResolvedPath) && File.Exists(cli.ResolvedPath))
                    ? cli.ResolvedPath
                    : "claude";
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/k \"\"" + target + "\" update\"",
                    WorkingDirectory = string.IsNullOrEmpty(_cwd) ? Environment.CurrentDirectory : _cwd,
                    UseShellExecute = true,
                };
                System.Diagnostics.Process.Start(psi);
                WatchForCliUpdate();
            }
            catch (Exception ex) { Log.Write("LaunchCliUpdateInTerminal: " + ex.Message); }
        }

        private static string Tail(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Trim();
            return s.Length <= max ? s : "…" + s.Substring(s.Length - max);
        }

        /// <summary>
        /// Notice when the update actually lands and refresh the banner by itself.
        /// <para>
        /// The updater runs in a detached <c>cmd /k</c> window, which stays open after the command
        /// finishes, so process exit says nothing about when the update completed — and nothing
        /// else was watching, which left the "update available" banner up even after a successful
        /// update. Poll the version the banner itself reports (<c>claude --version</c>) and push a
        /// fresh status the moment it moves.
        /// </para>
        /// </summary>
        private void WatchForCliUpdate()
        {
            if (_updateWatchRunning) return;
            _updateWatchRunning = true;

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    string before = GetInstalledCliVersion();
                    Log.Write("update watch: started at " + (before ?? "?"));

                    // ~5 minutes. A native self-update is usually seconds, but it can queue behind
                    // a download, and the binary cannot be replaced while a claude session holds
                    // it open — so give a slow swap room to happen rather than giving up early.
                    for (int i = 0; i < 60; i++)
                    {
                        await System.Threading.Tasks.Task.Delay(5000).ConfigureAwait(false);

                        string now = GetInstalledCliVersion();
                        if (string.IsNullOrEmpty(now) || now == before) continue;

                        Log.Write("update watch: " + (before ?? "?") + " -> " + now);
                        SendSetupStatus(forceRefresh: true);
                        return;
                    }
                    Log.Write("update watch: gave up, still at " + (before ?? "?"));
                }
                catch (Exception ex) { Log.Write("WatchForCliUpdate: " + ex.Message); }
                finally { _updateWatchRunning = false; }
            });
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

        // Effort levels available per model. Opus and Fable expose the full extended-thinking
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
                ["opus[1m]"] = opus,     // explicit Opus row; same range as Default
                ["fable"] = opus,
                ["sonnet"] = sonnet,
                ["haiku"] = haiku,
            };
        }

        private void SendInit()
        {
            _host.PostMessage("init", new
            {
                version = "1.0.11",
                theme = _theme.GetThemeVariables(),
                model = _model,
                effort = _effort,
                permissionMode = _permissionMode,
                showThinking = _showThinking,
                // Laid out like the VS Code panel: each row reads "<model> · <what it is for>",
                // with an explicit Opus row alongside Default.
                //
                // `label` is only a FALLBACK for the model name. Every id is a CLI alias that
                // resolves to the newest model in its family at launch, so a hardcoded "Opus 5"
                // would go stale the day Opus 6 ships. app.js therefore replaces the label with
                // the id the CLI actually resolved (from the session `init` event) as soon as it
                // knows it — the label only shows before the first turn of a fresh install.
                //
                // ids double as the --model value, so they must stay valid CLI aliases.
                // ratio = per-token price relative to Haiku (cheapest). Input and
                // output prices scale by the same factor, so one number covers both:
                // Haiku $1/$5, Sonnet $3/$15, Opus $5/$25, Fable $10/$50 per MTok.
                models = new object[]
                {
                    new { id = "default",  name = "Default (recommended)", label = "Opus 5 with 1M context", desc = "Best for everyday, complex tasks", ratio = 5.0 },
                    new { id = "opus[1m]", name = "Opus (1M context)",     label = "Opus 5 with 1M context", desc = "Best for everyday, complex tasks", ratio = 5.0 },
                    new { id = "fable",    name = "Fable",                 label = "Fable 5",   desc = "Most capable for your hardest and longest-running tasks", ratio = 10.0 },
                    new { id = "sonnet",   name = "Sonnet",                label = "Sonnet 5",  desc = "Efficient for routine tasks", ratio = 3.0 },
                    new { id = "haiku",    name = "Haiku",                 label = "Haiku 4.5", desc = "Fastest for quick answers", ratio = 1.0 },
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
                    TryRestoreForCwd();

                    // The tool window is typically restored — docked next to Solution Explorer —
                    // before the solution finishes opening, so the cwd above is often still the
                    // user-home fallback and the lookup above missed. Watch for the solution
                    // landing and try again against the real project directory.
                    HookSolutionLoad();
                }
                catch { }
            }).FireAndForget();
        }

        /// <summary>
        /// Load this working directory's persisted transcript and push it to the UI. No-op once a
        /// record is in hand or a live session owns the transcript, so it is safe to call again
        /// when the working directory is re-resolved.
        /// </summary>
        private void TryRestoreForCwd()
        {
            if (_record != null || _session != null) return;

            var rec = SessionStore.Load(_cwd);
            if (rec == null)
            {
                Log.Write("restore: no stored session for cwd=" + _cwd);
                return;
            }

            bool hasMsgs = rec.Messages != null && rec.Messages.Count > 0;
            _record = rec;
            if (hasMsgs && !string.IsNullOrEmpty(rec.SessionId)) _pendingResumeId = rec.SessionId;
            _model = InputValidation.SanitizeModel(rec.Model, "default");
            _permissionMode = InputValidation.SanitizeChoice(rec.Mode, InputValidation.AllowedModes, "default");
            _effort = InputValidation.SanitizeChoice(rec.Effort, InputValidation.AllowedEfforts, "none");
            _showThinking = rec.ShowThinking;
            Log.Write("restore: " + (hasMsgs ? rec.Messages.Count : 0) + " message(s) for cwd=" + _cwd);
            _host.PostMessage("restore", new
            {
                messages = hasMsgs ? rec.Messages : new System.Collections.Generic.List<StoredMessage>(),
                model = _model,
                mode = _permissionMode,
                effort = _effort,
                showThinking = _showThinking,
            });
        }

        /// <summary>
        /// Subscribe (once) to solution-load events so a transcript that could not be found during
        /// startup — because no solution was open yet — is restored when one arrives.
        /// </summary>
        private void HookSolutionLoad()
        {
            if (_solutionHooked) return;
            _solutionHooked = true;
            try
            {
                VS.Events.SolutionEvents.OnAfterOpenSolution += OnSolutionLoaded;
                VS.Events.SolutionEvents.OnAfterOpenFolder += OnFolderLoaded;
            }
            catch (Exception ex) { Log.Write("HookSolutionLoad failed: " + ex.Message); }
        }

        private void UnhookSolutionLoad()
        {
            if (!_solutionHooked) return;
            _solutionHooked = false;
            try
            {
                VS.Events.SolutionEvents.OnAfterOpenSolution -= OnSolutionLoaded;
                VS.Events.SolutionEvents.OnAfterOpenFolder -= OnFolderLoaded;
            }
            catch { }
        }

        private void OnSolutionLoaded(Solution solution) => ReresolveWorkingDirectory();

        private void OnFolderLoaded(string folder) => ReresolveWorkingDirectory();

        /// <summary>
        /// Re-resolve the working directory after a solution/folder opens and, if it moved,
        /// re-point the UI at it and retry the transcript restore.
        /// </summary>
        private void ReresolveWorkingDirectory()
        {
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                    // claude inherits its cwd at launch and can't be moved afterwards, and the
                    // transcript is persisted per cwd — so once a session is live, leave it be.
                    if (_session != null) return;

                    var dir = await GetWorkingDirectoryAsync();
                    if (string.IsNullOrEmpty(dir) ||
                        string.Equals(dir, _cwd, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    Log.Write("cwd re-resolved after solution load: " + _cwd + " -> " + dir);
                    _cwd = dir;
                    _host.PostMessage("init", new { cwd = _cwd });

                    // The / palette is cwd-scoped (project .claude/commands), so refresh it too.
                    SendCommands();
                    TryRestoreForCwd();
                }
                catch (Exception ex) { Log.Write("ReresolveWorkingDirectory failed: " + ex.Message); }
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
                    SelectionContext attachedSel = null;
                    try
                    {
                        var ctx = BuildContextPrefixAsync();
                        if (await System.Threading.Tasks.Task.WhenAny(ctx, System.Threading.Tasks.Task.Delay(2000)) == ctx)
                        {
                            var cp = await ctx;
                            if (cp != null) { prefix = cp.Text ?? string.Empty; attachedSel = cp.Selection; }
                        }
                    }
                    catch { }

                    // Echo the captured selection so the sent user bubble shows a chip of what
                    // was attached (mirrors the official VS Code ext's ide_selection marker).
                    if (attachedSel != null && attachedSel.HasSelection)
                        _host.PostMessage("sentSelection", new
                        {
                            filePath = attachedSel.FilePath,
                            startLine = attachedSel.StartLine,
                            endLine = attachedSel.EndLine,
                        });

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

        private sealed class ContextPrefix { public string Text = string.Empty; public SelectionContext Selection; }

        private async System.Threading.Tasks.Task<ContextPrefix> BuildContextPrefixAsync()
        {
            try
            {
                var sel = await _ide.GetActiveSelectionAsync();
                var openFiles = await _ide.GetOpenFilesAsync();
                var diags = await _ide.GetDiagnosticsAsync(30);
                var dbg = await _debug.GetDebugStateAsync();

                bool any = false;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("<ide-context>");

                // Live debugger state — only when actually debugging, so it stays silent at
                // design time. When paused, this carries the current location, exception,
                // call stack and locals so Claude can reason about the running app.
                if (dbg != null && dbg.IsActive)
                {
                    any = true;
                    sb.AppendLine("Debugger: " + dbg.Mode + (string.IsNullOrEmpty(dbg.ProcessName) ? "" : " — process " + dbg.ProcessName));
                    if (dbg.IsPaused)
                    {
                        if (!string.IsNullOrEmpty(dbg.Function))
                            sb.AppendLine("Stopped in: " + dbg.Function + (dbg.Line > 0 ? " (" + (dbg.File ?? "") + ":" + dbg.Line + ")" : ""));
                        if (!string.IsNullOrEmpty(dbg.Exception))
                            sb.AppendLine("Exception: " + dbg.Exception);
                        if (dbg.CallStack != null && dbg.CallStack.Count > 0)
                        {
                            sb.AppendLine("Call stack:");
                            foreach (var f in dbg.CallStack) sb.Append("- ").AppendLine(f);
                        }
                        if (dbg.Locals != null && dbg.Locals.Count > 0)
                        {
                            sb.AppendLine("Locals:");
                            foreach (var l in dbg.Locals)
                                sb.AppendLine("- " + l.Name + " (" + l.Type + ") = " + l.Value);
                        }
                    }
                }

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
                return new ContextPrefix { Text = any ? sb.ToString() : string.Empty, Selection = sel };
            }
            catch { return new ContextPrefix(); }
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

            // Last resort, from the persisted record itself. ResetSession clears the record, so a
            // deliberately-new session can never pick a conversation back up here. This keeps the
            // resolution in step with ResumableSessionId(), which callers use to decide whether
            // starting the CLI would actually restore a conversation.
            if (resume == null && !string.IsNullOrEmpty(_record?.SessionId))
            {
                resume = _record.SessionId;
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
                // The chat now renders the diff inline (red/green) per edit. We no longer auto-pop a
                // native VS diff window for every edit (tab clutter during agentic loops); the pre-edit
                // snapshot is kept so the card's "Open diff" button can open the full native diff on
                // demand. Drop the snapshot on a failed edit — there's nothing to compare.
                if (r.IsError && r.ToolUseId != null) _editedFiles.Remove(r.ToolUseId);
            };
            s.AssistantEnd += () => _host.PostMessage("assistantEnd", new { });
            // Real context size, measured per API request. The `result` totals below are cumulative
            // over the turn and must not drive the ring.
            s.ContextUsage += u => _host.PostMessage("contextUsage", new
            {
                promptTokens = u.PromptTokens,
                totalTokens = u.TotalTokens,
                cacheCreationTokens = u.CacheCreationTokens,
                cacheReadTokens = u.CacheReadTokens,
                inputTokens = u.InputTokens,
            });
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

                // A /compact turn produces no assistant reply worth keeping, so it is not appended
                // to the transcript. The turn simply ends here — the CLI compacted in place and
                // reported it via the Compacted event below; there is no session to restart.
                if (!_compacting && !r.IsError)
                {
                    AppendHistory("assistant", r.Text);
                }
                _compacting = false;
            };
            s.Compacted += c => _host.PostMessage("compacted", new
            {
                trigger = c.Trigger,          // "manual" from the button or /compact, "auto" when the window filled
                preTokens = c.PreTokens,
                postTokens = c.PostTokens,
                durationMs = c.DurationMs,
            });
            s.PermissionRequest += p => _host.PostMessage("permission", new { id = p.RequestId, tool = p.ToolName, input = RawJson(p.InputJson) });
            // Auto mode answered a card that was still on screen: close it out in the transcript.
            s.PermissionAutoAllowed += id => _host.PostMessage("permissionResolved", new { id, behavior = "allow" });
            // The CLI refused the live switch — fall back to relaunching with the new mode.
            s.PermissionModeChangeFailed += m => _optionsDirty = true;
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
                            // Kept for the on-demand "Open diff" button. Bound the memory: a very long
                            // agentic session could touch many large files.
                            if (_editedFiles.Count > 200) _editedFiles.Clear();
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
                PruneOldDiffTemps(dir);
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

        // The diff window holds its "before" temp open while shown, so the file we just wrote can't
        // be deleted now. Instead best-effort sweep older snapshots left from prior diff windows /
        // sessions so pre-edit file contents don't accumulate as plaintext in %TEMP%.
        private static void PruneOldDiffTemps(string dir)
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddHours(-6);
                foreach (var f in Directory.GetFiles(dir))
                {
                    try { if (File.GetLastWriteTimeUtc(f) < cutoff) File.Delete(f); }
                    catch { }
                }
            }
            catch { }
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

        // Compaction is delegated to the CLI's own /compact, which the headless stream-json input
        // accepts like any other slash command. The CLI rewrites its context in place, keeps the
        // same session, and reports the result as a system/compact_boundary event — no assistant
        // text is produced, so nothing lands in the transcript.
        //
        // This previously asked the model to write a summary brief and then restarted the session
        // seeded with it. That cost an extra full turn every time, and the brief streamed into the
        // chat as an ordinary reply — which is why pressing the button printed a wall of markdown.
        //
        // Reached from both the ring button and /compact in the slash palette (the palette entry is
        // a built-in that posts the same "compact" message, and shadows the CLI's own /compact).
        private void HandleCompact()
        {
            // A conversation restored from disk — or one whose process has since exited — has no
            // live CLI holding it, but its context is still on disk and resumable. Refusing there
            // was wrong, and worst exactly when compaction is wanted: a long conversation reopened
            // the next day, ring full, button dead. Start (resuming) the same way a send does, and
            // keep the refusal only for the case where there is genuinely nothing to compact.
            bool live = _session != null && _session.IsRunning;
            if (!live && string.IsNullOrEmpty(ResumableSessionId()))
            {
                _host.PostMessage("error", new { message = "Nothing to compact yet — send a message first." });
                return;
            }

            _compacting = true;
            _host.PostMessage("status", new { state = "thinking" });
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (!live) Log.Write("compact: no live session, resuming " + ResumableSessionId());
                    await EnsureWorkingDirectoryAsync();
                    EnsureSession();
                    _session.SendUserMessage("/compact", null);
                }
                catch (Exception ex)
                {
                    _compacting = false;
                    Log.Write("HandleCompact: " + ex.Message);
                    _host.PostMessage("error", new { message = ex.Message });
                    _host.PostMessage("status", new { state = "idle" });
                }
            }).FireAndForget();
        }

        /// <summary>
        /// The CLI session id a restart would resume, or null when there is nothing to continue.
        /// Mirrors the order <see cref="EnsureSession"/> resolves it in, so a caller can tell
        /// whether starting the CLI would actually bring a conversation back with it.
        /// </summary>
        private string ResumableSessionId()
        {
            if (!string.IsNullOrEmpty(_session?.SessionId)) return _session.SessionId;
            if (!string.IsNullOrEmpty(_pendingResumeId)) return _pendingResumeId;
            return _record?.SessionId;
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

        private static int GetInt(JsonElement el, string name, int fallback)
            => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
                && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;

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

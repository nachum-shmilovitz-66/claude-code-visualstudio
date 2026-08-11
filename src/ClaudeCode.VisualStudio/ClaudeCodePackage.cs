using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCode.VisualStudio
{
    /// <summary>
    /// The Claude Code Visual Studio package. Registers the chat tool window and the
    /// command that opens it. Loads in the background once the shell is ready.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("Claude Code", "Agentic coding assistant for Visual Studio.", "1.0.7")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    // Dock as a tab next to Solution Explorer (its window GUID), instead of a free-floating right pane.
    [ProvideToolWindow(typeof(ClaudeChatToolWindow.Pane), Style = VsDockStyle.Tabbed, Window = "3AE79031-E1BC-11D0-8F78-00A0C9110057")]
    // Auto-show the chat when a debug session starts. VS loads a separate "Debug" window layout
    // on F5, which otherwise hides tool windows that were open in the design layout — so the user
    // had to re-open Claude every time they started debugging. Tying it to the Debugging UI context
    // makes VS surface it automatically.
    [ProvideToolWindowVisibility(typeof(ClaudeChatToolWindow.Pane), Microsoft.VisualStudio.VSConstants.UICONTEXT.Debugging_string)]
    // Load at shell startup (background) so we can surface the chat on first install. VS never
    // auto-opens a tool window until it has been shown once, so a fresh install leaves the user
    // hunting in View → Claude Code; loading here lets InitializeAsync show it the first time.
    [ProvideAutoLoad(Microsoft.VisualStudio.VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuids.ClaudeCodePackageString)]
    public sealed class ClaudeCodePackage : ToolkitPackage
    {
#if VS2017 || VS2019
        // Must be in place before anything touches System.Text.Json; the package type loads
        // before any of our code runs, so the static ctor is the earliest reliable hook.
        static ClaudeCodePackage()
        {
            AssemblyResolver.Install();
        }
#endif

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.RegisterCommandsAsync();
            this.RegisterToolWindows();
            ScheduleFirstRunShow();   // must NOT be awaited — see below
        }

        // Open the chat once, the very first time the extension ever loads on this machine, so a
        // fresh install greets the user with the panel instead of an empty View menu. A marker
        // file records that we've done it — afterwards VS's own per-user window persistence takes
        // over, so we never reopen a window the user has deliberately closed.
        //
        // CRITICAL: this is fire-and-forget and runs only after the shell is idle. Awaiting a
        // tool-window show inside InitializeAsync deadlocks VS at the start window (the package
        // loads on ShellInitialized; showing the WebView pane there blocks the UI thread that the
        // show itself needs — VS hangs "Not Responding"). Yielding off the init path avoids that.
        private void ScheduleFirstRunShow()
        {
            string marker;
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeCodeVS");
                // Key the marker by VS major version so each edition (2017/2019/2022/2026) greets the
                // user once on its own first install. A single machine-global flag meant that showing
                // the panel on one edition suppressed the first-run greeting on every other edition.
                marker = Path.Combine(dir, $"shown-{VsMajorVersion()}.flag");
                if (File.Exists(marker)) return;
            }
            catch { return; }

            JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    // Let the shell finish coming up before we touch the UI thread.
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    await Task.Yield();

                    await ClaudeChatToolWindow.ShowAsync();

                    var dir = Path.GetDirectoryName(marker);
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));  // only after a successful show
                }
                catch { }
            }).FireAndForget();
        }

        // VS major version of the running devenv (17 = 2022, 18 = 2026, 16 = 2019, 15 = 2017),
        // used to scope the first-run marker per edition. Falls back to "x" if unavailable.
        private static string VsMajorVersion()
        {
            try
            {
                var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                return System.Diagnostics.FileVersionInfo.GetVersionInfo(exe)
                    .FileMajorPart.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { return "x"; }
        }
    }
}

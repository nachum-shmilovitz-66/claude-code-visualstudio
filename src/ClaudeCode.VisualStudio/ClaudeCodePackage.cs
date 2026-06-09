using System;
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
    [InstalledProductRegistration("Claude Code", "Agentic coding assistant for Visual Studio.", "0.2.19")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    // Dock as a tab next to Solution Explorer (its window GUID), instead of a free-floating right pane.
    [ProvideToolWindow(typeof(ClaudeChatToolWindow.Pane), Style = VsDockStyle.Tabbed, Window = "3AE79031-E1BC-11D0-8F78-00A0C9110057")]
    [Guid(PackageGuids.ClaudeCodePackageString)]
    public sealed class ClaudeCodePackage : ToolkitPackage
    {
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await this.RegisterCommandsAsync();
            this.RegisterToolWindows();
        }
    }
}

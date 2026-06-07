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
    [InstalledProductRegistration("Claude Code", "Agentic coding assistant for Visual Studio.", "0.1.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideToolWindow(typeof(ClaudeChatToolWindow.Pane), Style = VsDockStyle.Tabbed, Orientation = ToolWindowOrientation.Right)]
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

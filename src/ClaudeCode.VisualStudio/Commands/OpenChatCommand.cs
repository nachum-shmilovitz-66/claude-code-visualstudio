using System;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCode.VisualStudio
{
    /// <summary>
    /// Tools &gt; Claude Code (and View &gt; Other Windows &gt; Claude Code). A checkable toggle:
    /// shows the chat tool window when unchecked, hides it when checked. The check reflects
    /// whether the window is currently open in the VS layout.
    /// </summary>
    [Command(PackageGuids.ClaudeCodeCmdSetString, PackageIds.OpenClaudeChat)]
    internal sealed class OpenChatCommand : BaseCommand<OpenChatCommand>
    {
        protected override Task InitializeCompletedAsync()
        {
            Command.BeforeQueryStatus += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                Command.Checked = TryGetFrame(false, out var frame) && IsVisible(frame);
            };
            return Task.CompletedTask;
        }

        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (TryGetFrame(false, out var frame) && IsVisible(frame))
            {
                frame.Hide();
            }
            else
            {
                await ClaudeChatToolWindow.ShowAsync();
            }
        }

        private static bool TryGetFrame(bool create, out IVsWindowFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            frame = null;
            var shell = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(SVsUIShell)) as IVsUIShell;
            if (shell == null) return false;
            var guid = new Guid(PackageGuids.ClaudeChatToolWindowString);
            int hr = shell.FindToolWindow(create ? (uint)__VSFINDTOOLWIN.FTW_fForceCreate : 0u, ref guid, out frame);
            return ErrorHandler.Succeeded(hr) && frame != null;
        }

        private static bool IsVisible(IVsWindowFrame frame)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return frame != null && frame.IsVisible() == VSConstants.S_OK;
        }
    }
}

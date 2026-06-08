using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCode.VisualStudio
{
    /// <summary>
    /// View &gt; Claude Code (and View &gt; Other Windows &gt; Claude Code). Behaves like the
    /// GitHub Copilot Chat command: a plain button that shows / activates the chat tool window.
    /// No checked state and no show/hide toggle — invoking it always brings the window up.
    /// </summary>
    [Command(PackageGuids.ClaudeCodeCmdSetString, PackageIds.OpenClaudeChat)]
    internal sealed class OpenChatCommand : BaseCommand<OpenChatCommand>
    {
        protected override async Task ExecuteAsync(OleMenuCmdEventArgs e)
        {
            await ClaudeChatToolWindow.ShowAsync();
        }
    }
}

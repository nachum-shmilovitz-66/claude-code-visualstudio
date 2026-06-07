using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace ClaudeCode.VisualStudio
{
    /// <summary>
    /// View &gt; Other Windows &gt; Claude Code. Shows the chat tool window.
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

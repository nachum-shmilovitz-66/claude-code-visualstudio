using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Community.VisualStudio.Toolkit;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
// The VS 2019 SDK still ships the legacy Microsoft.VisualStudio.Shell.Task, which makes a
// bare "Task" ambiguous there.
using Task = System.Threading.Tasks.Task;

namespace ClaudeCode.VisualStudio
{
    /// <summary>
    /// The Claude Code chat tool window. Hosts the WebView2 chat surface.
    /// </summary>
    public class ClaudeChatToolWindow : BaseToolWindow<ClaudeChatToolWindow>
    {
        public override string GetTitle(int toolWindowId) => "Claude Code";

        public override Type PaneType => typeof(Pane);

        public override Task<FrameworkElement> CreateAsync(int toolWindowId, CancellationToken cancellationToken)
        {
            return Task.FromResult<FrameworkElement>(new ClaudeChatControl());
        }

        [Guid(PackageGuids.ClaudeChatToolWindowString)]
        internal class Pane : ToolWindowPane
        {
            public Pane()
            {
#if VS2017 || VS2019
                // CommentSparkle was added in the VS 2022 (17.x) image catalog; the VS 2017/2019
                // catalogs only have Comment.
                BitmapImageMoniker = KnownMonikers.Comment;
#else
                BitmapImageMoniker = KnownMonikers.CommentSparkle;
#endif
            }
        }
    }
}

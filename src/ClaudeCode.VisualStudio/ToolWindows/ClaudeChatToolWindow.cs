using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using Community.VisualStudio.Toolkit;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;

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
                BitmapImageMoniker = KnownMonikers.StatusInformation;
            }
        }
    }
}

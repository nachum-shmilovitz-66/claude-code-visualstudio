using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
// The VS 2019 SDK still ships the legacy Microsoft.VisualStudio.Shell.Task, which makes a
// bare "Task" ambiguous there.
using Task = System.Threading.Tasks.Task;

namespace ClaudeCode.VisualStudio.WebView
{
    /// <summary>
    /// Wraps a <see cref="WebView2"/> control: boots the runtime, serves the bundled
    /// chat UI from a virtual host, and brokers JSON messages both directions.
    /// </summary>
    public sealed class WebViewHost
    {
        private const string VirtualHost = "claudecode.local";

        private readonly WebView2 _webView;
        private readonly Dispatcher _dispatcher;
        private bool _initialized;

        /// <summary>Raised on the UI thread for every message the WebView posts to us.</summary>
        public event Action<WebMessage> MessageReceived;

        /// <summary>Raised once the WebView core is ready and the page has been navigated.</summary>
        public event Action Ready;

        public WebViewHost(WebView2 webView)
        {
            _webView = webView;
            _dispatcher = webView.Dispatcher;
        }

        public async Task InitializeAsync(Dictionary<string, string> theme = null)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;

            // Kill the white flash: WebView2's control background defaults to white and shows for
            // the moment between navigation and the first paint of our (themed) HTML. Paint it the
            // VS tool-window background instead so a dark IDE never flashes white on open.
            try
            {
                if (theme != null && theme.TryGetValue("--bg", out var bgHex) && TryParseHex(bgHex, out var bg))
                    _webView.DefaultBackgroundColor = bg;
                else
                    _webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(30, 30, 30);
            }
            catch { }

            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
#if VS2017
                // Per-flavor folder: a WebView2 user-data dir can't be shared by hosts whose
                // environment options/runtime differ, and VS versions may run side by side.
                "ClaudeCodeVS", "WebView2-vs15");
#elif VS2019
                "ClaudeCodeVS", "WebView2-vs16");
#else
                "ClaudeCodeVS", "WebView2");
#endif
            Directory.CreateDirectory(userData);

            // By far the biggest single cost of a cold open: creating the environment spins up
            // the WebView2 runtime and its browser/renderer processes.
            long t = Services.Perf.Now;
            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            Services.Perf.Step("webview: CreateAsync (runtime boot)", t);

            t = Services.Perf.Now;
            await _webView.EnsureCoreWebView2Async(env);
            Services.Perf.Step("webview: EnsureCoreWebView2Async", t);

            var core = _webView.CoreWebView2;
            var settings = core.Settings;
            // Security: DevTools exposes the host<->WebView message protocol and in-memory data.
            // Enable only in Debug builds.
#if DEBUG
            settings.AreDevToolsEnabled = true;
#else
            settings.AreDevToolsEnabled = false;
#endif
            settings.AreDefaultContextMenusEnabled = true;
            settings.IsStatusBarEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsZoomControlEnabled = false;

            core.WebMessageReceived += OnWebMessageReceived;

            // Security: keep the WebView pinned to our local UI. A target="_blank" link opens a
            // NewWindowRequested; any attempt to navigate the frame elsewhere is cancelled. Real
            // web URLs are handed to the system browser instead of loading inside the control.
            core.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;
                OpenInBrowser(e.Uri);
            };
            core.NavigationStarting += (s, e) =>
            {
                if (e.Uri != null &&
                    !e.Uri.StartsWith("https://" + VirtualHost, StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    OpenInBrowser(e.Uri);
                }
            };

            var mediaPath = Path.Combine(
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty,
                "media");
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost, mediaPath, CoreWebView2HostResourceAccessKind.Allow);

            // Seed the VS theme into :root BEFORE the page renders, so the very first paint matches
            // the IDE (no white→dark→theme flash). Runs on document-created, ahead of app.js, which
            // later refines the same variables on the init message and on live theme switches.
            if (theme != null && theme.Count > 0)
            {
                try { await core.AddScriptToExecuteOnDocumentCreatedAsync(BuildThemeSeedScript(theme)); }
                catch { }
            }

            // Cache-bust the top-level page so HTML changes always load fresh.
            core.Navigate($"https://{VirtualHost}/index.html?cb={Guid.NewGuid():N}");
            Services.Perf.Mark("webview: navigate issued");

            Ready?.Invoke();
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            // The page has spoken, so the WebView is alive: anything that piled up while it was
            // being re-created can go out now. This is the trigger that matters after a re-create —
            // the page's own "ready" arrives here before anything else tries to post.
            if (_pending.Count > 0)
            {
                var core = TryGetCore();
                if (core != null) FlushPending(core);
            }

            string json;
            try
            {
                json = e.WebMessageAsJson;
            }
            catch
            {
                return;
            }

            if (string.IsNullOrEmpty(json))
            {
                return;
            }

            WebMessage message;
            try
            {
                message = WebMessage.Parse(json);
            }
            catch
            {
                return;
            }

            if (message != null)
            {
                MessageReceived?.Invoke(message);
            }
        }

        /// <summary>Post a typed message to the WebView. Safe to call from any thread.</summary>
        public void PostMessage(string type, object payload)
        {
            string json;
            try
            {
                json = WebMessage.Build(type, payload);
            }
            catch (Exception ex)
            {
                Services.Log.Write("PostMessage Build FAILED type=" + type + " : " + ex.Message);
                return;
            }

            // Marshal to the VS main thread (which owns the WebView) via the JoinableTaskFactory.
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                PostRaw(json);
            }).FireAndForget();
        }

        /// <summary>
        /// Messages posted while the WebView was unavailable, replayed when it comes back.
        /// <para>
        /// The control is not always there to post to: it is still booting early in the load, and
        /// Visual Studio disposes and re-creates it while it settles the window layout at startup.
        /// Both used to end the same way — the message was thrown away, the disposed case logged as
        /// "PostRaw EXCEPTION: Cannot access a disposed object", which reads like a crash for what
        /// is an ordinary race. A dropped message is a rendering that never happens: a lost
        /// <c>setup</c> is a banner that never appears, a lost <c>restore</c> an empty transcript.
        /// </para>
        /// </summary>
        private readonly BoundedMessageQueue _pending = new BoundedMessageQueue(200);

        private void PostRaw(string json)
        {
            var core = TryGetCore();
            if (core == null)
            {
                _pending.Enqueue(json);
                Services.Log.Write("PostRaw: WebView unavailable, queued (" + _pending.Count + " waiting)");
                Services.Log.WriteVerbose("PostRaw queued: " + Head(json));
                return;
            }

            // Drain first, so a replayed message never overtakes the one that triggered the drain.
            FlushPending(core);
            if (!TryPost(core, json))
            {
                _pending.Enqueue(json);
                Services.Log.Write("PostRaw: WebView went away mid-post, queued (" + _pending.Count + " waiting)");
            }
        }

        /// <summary>
        /// Replay whatever is waiting. Called on any inbound message too: the page speaking is
        /// proof the WebView is alive again, and without that trigger a queue filled during a
        /// re-create would sit there until something else happened to be posted.
        /// </summary>
        private void FlushPending(CoreWebView2 core)
        {
            if (_pending.Count == 0) return;

            int sent = 0;
            while (_pending.TryDequeue(out var queued))
            {
                if (!TryPost(core, queued))
                {
                    _pending.PushFront(queued);   // still not there; keep it for the next attempt
                    break;
                }
                sent++;
            }

            if (sent > 0)
            {
                Services.Log.Write("PostRaw: replayed " + sent + " queued message(s)" +
                                   (_pending.DroppedCount > 0 ? ", " + _pending.DroppedCount + " dropped at the cap" : ""));
            }
        }

        /// <summary>The core, or null when the control is not there to ask — booting, or disposed.</summary>
        private CoreWebView2 TryGetCore()
        {
            try { return _webView.CoreWebView2; }
            catch (ObjectDisposedException) { return null; }
        }

        /// <summary>True when the message was handed over, or failed for a reason retrying cannot fix.</summary>
        private bool TryPost(CoreWebView2 core, string json)
        {
            try
            {
                core.PostWebMessageAsJson(json);
                // Security: the envelope head carries message content — assistant text, account
                // details (the accountData envelope reaches the email inside 80 chars). Gate it
                // behind Verbose so Release builds stay quiet, per the Log policy.
                Services.Log.WriteVerbose("PostRaw OK " + Head(json));
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;   // the control went away under us — worth queueing and retrying
            }
            catch (Exception ex)
            {
                // A real failure. Record it, but do not queue: retrying malformed or rejected
                // content would replay the same failure for ever.
                Services.Log.Write("PostRaw EXCEPTION: " + ex.Message);
                Services.Log.WriteVerbose("PostRaw EXCEPTION payload: " + Head(json));
                return true;
            }
        }

        private static void OpenInBrowser(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                Services.Log.Write("WebView navigation blocked: " + url);
                return;
            }
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch { }
        }

        private static string Head(string s) => s == null ? "" : (s.Length > 80 ? s.Substring(0, 80) : s);

        // Inline script that sets each --var on :root (inline style beats the stylesheet's defaults)
        // plus an explicit documentElement background, so the page paints in-theme from frame one.
        private static string BuildThemeSeedScript(Dictionary<string, string> theme)
        {
            var sb = new StringBuilder("(function(){try{var r=document.documentElement.style;");
            foreach (var kv in theme)
                sb.Append("r.setProperty(").Append(JsStr(kv.Key)).Append(',').Append(JsStr(kv.Value)).Append(");");
            if (theme.TryGetValue("--bg", out var bg))
                sb.Append("r.background=").Append(JsStr(bg)).Append(';');
            sb.Append("}catch(e){}})();");
            return sb.ToString();
        }

        private static string JsStr(string s)
        {
            return "'" + (s ?? string.Empty).Replace("\\", "\\\\").Replace("'", "\\'") + "'";
        }

        private static bool TryParseHex(string s, out System.Drawing.Color color)
        {
            color = System.Drawing.Color.Empty;
            if (string.IsNullOrEmpty(s) || s[0] != '#' || s.Length != 7) return false;
            if (int.TryParse(s.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            {
                color = System.Drawing.Color.FromArgb(255, (v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
                return true;
            }
            return false;
        }
    }
}

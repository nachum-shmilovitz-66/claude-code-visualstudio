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

            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await _webView.EnsureCoreWebView2Async(env);

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

            Ready?.Invoke();
        }

        private void OnWebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
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

        private void PostRaw(string json)
        {
            try
            {
                var core = _webView.CoreWebView2;
                if (core == null)
                {
                    Services.Log.Write("PostRaw: CoreWebView2 NULL, dropping " + Head(json));
                    return;
                }
                core.PostWebMessageAsJson(json);
                Services.Log.Write("PostRaw OK " + Head(json));
            }
            catch (Exception ex)
            {
                Services.Log.Write("PostRaw EXCEPTION: " + ex.Message + " :: " + Head(json));
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

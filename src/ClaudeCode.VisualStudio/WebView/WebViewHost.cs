using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

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

        public async Task InitializeAsync()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;

            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClaudeCodeVS", "WebView2");
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
    }
}

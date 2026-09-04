using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCode.VisualStudio.Tests
{
    /// <summary>
    /// Runs the WebView UI suite (tests/webview) and reports it as one MSTest result, so the chat
    /// panel's JavaScript is covered by the same `dotnet test` / Test Explorer run as everything
    /// else rather than relying on somebody remembering to run node by hand.
    /// <para>
    /// The JS suite itself is plain Node with no npm dependencies — see tests/webview/README.md.
    /// </para>
    /// </summary>
    [TestClass]
    public class WebViewUiTests
    {
        [TestMethod]
        public void WebViewUiSuite_Passes()
        {
            var runner = FindRepoFile(Path.Combine("tests", "webview", "run.js"));
            Assert.IsNotNull(runner, "could not locate tests/webview/run.js from " + AppDomain.CurrentDomain.BaseDirectory);

            var psi = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = "\"" + runner + "\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(runner),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            string output;
            int exit;
            try
            {
                using (var p = Process.Start(psi))
                {
                    Assert.IsNotNull(p, "node did not start");
                    output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(120000))
                    {
                        try { p.Kill(); } catch { }
                        Assert.Fail("the webview suite did not finish within 120s");
                    }
                    exit = p.ExitCode;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // No node on this machine (or not on PATH). Say so rather than reporting a pass:
                // an inconclusive result is visible, a silent skip is not.
                Assert.Inconclusive("node was not found on PATH — run `node tests/webview/run.js` manually");
                return;
            }

            Console.WriteLine(output);
            Assert.AreEqual(0, exit, "the webview UI suite failed:" + Environment.NewLine + output);
        }

        /// <summary>Walk up from the test assembly until the given repo-relative path exists.</summary>
        private static string FindRepoFile(string relative)
        {
            var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }
}

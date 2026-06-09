using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace ClaudeCode.VisualStudio.Services
{
    public sealed class McpServerInfo
    {
        public string Name;     // "github", "atlassian-work", "claude.ai Gmail"
        public string Detail;   // url / command + transport, e.g. "https://… (HTTP)"
        public string Status;   // raw status text, e.g. "Connected", "Failed to connect"
        public bool Ok;         // true when the server reports a healthy connection
    }

    /// <summary>
    /// Runs <c>claude mcp list</c> as a one-shot subprocess and parses the human-readable
    /// output into structured server entries. This works without an active chat session,
    /// so the /mcp screen can show servers before the user sends a first message.
    /// </summary>
    public static class McpService
    {
        public static async Task<List<McpServerInfo>> ListAsync(string workingDir, int timeoutMs = 30000)
        {
            var result = new List<McpServerInfo>();
            try
            {
                var cli = ClaudeCliLocator.Locate();
                var psi = new ProcessStartInfo
                {
                    FileName = cli.FileName,
                    Arguments = cli.ArgumentPrefix + "mcp list",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false),
                    WorkingDirectory = string.IsNullOrEmpty(workingDir)
                        ? Environment.CurrentDirectory
                        : workingDir,
                };
                psi.EnvironmentVariables["FORCE_COLOR"] = "0";
                psi.EnvironmentVariables["NO_COLOR"] = "1";
                psi.EnvironmentVariables["CLAUDE_CODE_ENTRYPOINT"] = "vs-extension";

                using (var proc = new Process { StartInfo = psi })
                {
                    var sb = new StringBuilder();
                    proc.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                    proc.ErrorDataReceived += (s, e) => { if (e.Data != null) Log.WriteVerbose("mcp list stderr: " + e.Data); };

                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    var exited = await Task.Run(() => proc.WaitForExit(timeoutMs));
                    if (!exited)
                    {
                        try { proc.Kill(); } catch { }
                        Log.Write("McpService: 'mcp list' timed out after " + timeoutMs + "ms");
                    }

                    Parse(sb.ToString(), result);
                }
            }
            catch (Exception ex)
            {
                Log.Write("McpService.ListAsync: " + ex.Message);
            }
            return result;
        }

        // Parses lines of the form:
        //   "github: https://api.githubcopilot.com/mcp/ (HTTP) - ✗ Failed to connect"
        //   "atlassian-work: https://mcp.atlassian.com/v1/mcp (HTTP) - ✓ Connected"
        //   "claude.ai Gmail: https://gmailmcp.googleapis.com/mcp/v1 - ! Needs authentication"
        // The server name can contain spaces; URLs use "://" (never ": "), so the first
        // ": " safely splits name from detail, and the last " - " splits detail from status.
        internal static void Parse(string output, List<McpServerInfo> into)
        {
            if (string.IsNullOrEmpty(output)) return;
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("Checking", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.IndexOf("No MCP servers", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                int colon = line.IndexOf(": ", StringComparison.Ordinal);
                if (colon <= 0) continue;

                var name = line.Substring(0, colon).Trim();
                var remainder = line.Substring(colon + 2).Trim();

                string detail = remainder, status = string.Empty;
                int dash = remainder.LastIndexOf(" - ", StringComparison.Ordinal);
                if (dash >= 0)
                {
                    detail = remainder.Substring(0, dash).Trim();
                    status = remainder.Substring(dash + 3).Trim();
                }

                // Strip leading health glyphs (✓ ✗ !) from the status text for clean display.
                var clean = status.TrimStart('✓', '✗', '!', '✔', '✖', ' ').Trim();
                bool ok = status.IndexOf("✓", StringComparison.Ordinal) >= 0 ||
                          status.IndexOf("Connected", StringComparison.OrdinalIgnoreCase) >= 0;

                into.Add(new McpServerInfo
                {
                    Name = name,
                    Detail = detail,
                    Status = string.IsNullOrEmpty(clean) ? status : clean,
                    Ok = ok,
                });
            }
        }
    }
}

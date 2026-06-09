using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ClaudeCode.VisualStudio.Services
{
    public sealed class McpServerInfo
    {
        public string Name;     // "github", "atlassian-work", "claude.ai Gmail"
        public string Detail;   // url / command + transport, e.g. "https://… (HTTP)"
        public string Status;   // raw status text, e.g. "Connected", "Failed to connect"
        public bool Ok;         // true when the server reports a healthy connection
        public string Scope;    // "Project" (project .mcp.json) | "User" | "claude.ai"
        public string MissingEnv; // name of a ${ENV} the project config references that is unset/empty, if any
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

                EnrichScopes(result, workingDir);
            }
            catch (Exception ex)
            {
                Log.Write("McpService.ListAsync: " + ex.Message);
            }
            return result;
        }

        // Assigns a display scope to each server and, for project-scoped servers, flags the first
        // ${ENV} placeholder in their .mcp.json config that is currently unset/empty (the usual
        // cause of a token-based server showing "Failed to connect"). Reads only the project
        // .mcp.json; user/global servers fall back to the "User" group (claude.ai by name prefix).
        // We deliberately do NOT shell `claude mcp get` — its output prints the resolved Bearer token.
        internal static void EnrichScopes(List<McpServerInfo> servers, string workingDir)
        {
            foreach (var s in servers)
                s.Scope = (s.Name != null && s.Name.StartsWith("claude.ai", StringComparison.OrdinalIgnoreCase))
                    ? "claude.ai" : "User";

            try
            {
                if (string.IsNullOrEmpty(workingDir)) return;
                var path = Path.Combine(workingDir, ".mcp.json");
                if (!File.Exists(path)) return;

                using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    if (!doc.RootElement.TryGetProperty("mcpServers", out var ms) || ms.ValueKind != JsonValueKind.Object)
                        return;

                    foreach (var prop in ms.EnumerateObject())
                    {
                        var srv = servers.Find(x => string.Equals(x.Name, prop.Name, StringComparison.Ordinal));
                        if (srv == null) continue;
                        srv.Scope = "Project";

                        // Flag the first ${VAR} this server references that is unset/empty in the process env.
                        foreach (Match m in Regex.Matches(prop.Value.GetRawText(), @"\$\{([A-Za-z_][A-Za-z0-9_]*)\}"))
                        {
                            var name = m.Groups[1].Value;
                            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
                            {
                                srv.MissingEnv = name;
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.WriteVerbose("McpService.EnrichScopes: " + ex.Message);
            }
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

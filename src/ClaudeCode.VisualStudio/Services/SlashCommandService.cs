using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ClaudeCode.VisualStudio.Services
{
    /// <summary>
    /// Fetches the CLI's full slash-command set out-of-band, before the first chat turn.
    /// The set is only carried on the <c>system/init</c> event, which the CLI emits only
    /// after it receives the first stdin message — not at startup. So this spins up a
    /// short-lived throwaway <c>claude</c> in stream-json mode, writes a single empty user
    /// message purely to trigger init, reads <c>slash_commands</c> off that init line, then
    /// kills the process before the model turn runs (no token cost, separate from the user's
    /// real session). Lets the slash palette show the complete set (incl. project
    /// <c>.claude/commands</c>) up front, instead of only the built-ins until the first turn.
    /// </summary>
    public static class SlashCommandService
    {
        public static async Task<List<string>> ListAsync(string workingDir, int timeoutMs = 20000)
        {
            var result = new List<string>();
            try
            {
                var cli = ClaudeCliLocator.Locate();
                var psi = new ProcessStartInfo
                {
                    FileName = cli.FileName,
                    // Same machine-readable session shape as ClaudeSession, minus model/permission
                    // flags (the slash-command set does not depend on them).
                    Arguments = cli.ArgumentPrefix +
                        "--print --input-format stream-json --output-format stream-json --verbose",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
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
                    var done = new TaskCompletionSource<bool>();
                    proc.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data == null || done.Task.IsCompleted) return;
                        if (TryParseInit(e.Data, result)) done.TrySetResult(true);
                    };
                    proc.ErrorDataReceived += (s, e) => { if (e.Data != null) Log.WriteVerbose("slash init stderr: " + e.Data); };

                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    // The CLI emits system/init only after it reads the first stdin message.
                    // Send one empty user message to trigger it (newline-terminated, stdin left
                    // open); we kill the process the moment init arrives (below), before any
                    // model turn runs.
                    try
                    {
                        await proc.StandardInput.WriteAsync(
                            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"\"}]}}\n");
                        await proc.StandardInput.FlushAsync();
                    }
                    catch (Exception ex) { Log.WriteVerbose("slash init stdin: " + ex.Message); }

                    // Wait for the init line or the timeout, whichever comes first.
                    var finished = await Task.WhenAny(done.Task, Task.Delay(timeoutMs));
                    if (finished != done.Task)
                        Log.Write("SlashCommandService: timed out after " + timeoutMs + "ms");

                    try { if (!proc.HasExited) proc.Kill(); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Write("SlashCommandService.ListAsync: " + ex.Message);
            }
            return result;
        }

        // Returns true (and fills <paramref name="into"/>) when the line is the system/init
        // event. The slash_commands array carries the complete CLI command set as bare names.
        internal static bool TryParseInit(string line, List<string> into)
        {
            line = line == null ? null : line.Trim();
            if (string.IsNullOrEmpty(line) || line[0] != '{') return false;
            try
            {
                using (var doc = JsonDocument.Parse(line))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return false;
                    if (!root.TryGetProperty("type", out var t) || t.GetString() != "system") return false;
                    if (!root.TryGetProperty("subtype", out var st) || st.GetString() != "init") return false;
                    // init reports commands across "slash_commands" and a "skills" array (a skill is
                    // invoked as /<name> too). Merge both, deduped, preserving first-seen order.
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var key in new[] { "slash_commands", "skills" })
                    {
                        if (root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                            foreach (var c in arr.EnumerateArray())
                            {
                                var name = c.GetString();
                                if (!string.IsNullOrEmpty(name) && seen.Add(name)) into.Add(name);
                            }
                    }
                    return true;
                }
            }
            catch { return false; }
        }
    }
}

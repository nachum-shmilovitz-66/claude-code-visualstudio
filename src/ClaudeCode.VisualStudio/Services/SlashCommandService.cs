using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ClaudeCode.VisualStudio.Services
{
    /// <summary>What one startup probe of the CLI found.</summary>
    public sealed class CliProbeResult
    {
        /// <summary>The full slash-command set, as bare names. Empty when the probe failed.</summary>
        public List<string> Commands = new List<string>();

        /// <summary>
        /// The model picker rows the CLI offers, or null when it did not answer the initialize
        /// request (an older CLI, a failed launch) - in which case the caller keeps what it has.
        /// </summary>
        public List<CliModelInfo> Models;
    }

    /// <summary>
    /// Fetches the CLI's full slash-command set and model list out-of-band, before the first chat
    /// turn. Neither is available at startup: the command set is only carried on the
    /// <c>system/init</c> event, which the CLI emits only after it receives the first stdin
    /// message, and the model list only on the reply to an <c>initialize</c> control request. So
    /// this spins up a short-lived throwaway <c>claude</c> in stream-json mode, writes the
    /// initialize request and a single empty user message (purely to trigger init), reads
    /// <c>models</c> off the control response and <c>slash_commands</c> off the init line, then
    /// kills the process before the model turn runs (no token cost, separate from the user's real
    /// session). Lets the slash palette show the complete set (incl. project
    /// <c>.claude/commands</c>) and the picker show the models the CLI actually has - with their
    /// current names - up front, instead of the built-ins until the first turn.
    /// </summary>
    public static class SlashCommandService
    {
        // How long to keep waiting for the control response once the init line is in hand. The
        // CLI normally answers the initialize request first, so this rarely runs; it only bounds
        // the wait on a CLI that never answers, so a missing model list cannot hold up the palette.
        private const int ModelsGraceMs = 2000;

        /// <summary>Commands only; see <see cref="ProbeAsync"/>.</summary>
        public static async Task<List<string>> ListAsync(string workingDir, int timeoutMs = 20000)
        {
            var r = await ProbeAsync(workingDir, timeoutMs).ConfigureAwait(false);
            return r.Commands;
        }

        public static async Task<CliProbeResult> ProbeAsync(string workingDir, int timeoutMs = 20000)
        {
            var result = new CliProbeResult();
            var models = new List<CliModelInfo>();
            try
            {
                var cli = ClaudeCliLocator.Locate();
                var psi = new ProcessStartInfo
                {
                    FileName = cli.FileName,
                    // Same machine-readable session shape as ClaudeSession, minus model/permission
                    // flags (neither list depends on them).
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
                    var gate = new object();
                    bool initSeen = false, modelsSeen = false;
                    proc.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data == null || done.Task.IsCompleted) return;
                        bool finish = false, startGrace = false;
                        lock (gate)
                        {
                            if (!modelsSeen && CliModelList.TryParseControlResponse(e.Data, models))
                            {
                                modelsSeen = true;
                                finish = initSeen;
                            }
                            else if (!initSeen && TryParseInit(e.Data, result.Commands))
                            {
                                initSeen = true;
                                finish = modelsSeen;
                                startGrace = !modelsSeen;
                            }
                        }
                        if (finish) done.TrySetResult(true);
                        else if (startGrace)
                            Task.Delay(ModelsGraceMs).ContinueWith(_ => done.TrySetResult(true));
                    };
                    proc.ErrorDataReceived += (s, e) => { if (e.Data != null) Log.WriteVerbose("slash init stderr: " + e.Data); };

                    proc.Start();
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();

                    // The model list comes back on the reply to an initialize request; the CLI
                    // emits system/init only after it reads the first stdin message. Send both
                    // (newline-terminated, stdin left open); the process is killed the moment
                    // both replies are in (above), before any model turn runs.
                    try
                    {
                        await proc.StandardInput.WriteAsync(
                            "{\"type\":\"control_request\",\"request_id\":\"req_probe_init\",\"request\":{\"subtype\":\"initialize\"}}\n" +
                            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"\"}]}}\n");
                        await proc.StandardInput.FlushAsync();
                    }
                    catch (Exception ex) { Log.WriteVerbose("slash init stdin: " + ex.Message); }

                    // Wait for both replies or the timeout, whichever comes first.
                    var finished = await Task.WhenAny(done.Task, Task.Delay(timeoutMs));
                    if (finished != done.Task)
                        Log.Write("SlashCommandService: timed out after " + timeoutMs + "ms");

                    try { if (!proc.HasExited) proc.Kill(); } catch { }

                    lock (gate)
                    {
                        if (modelsSeen && models.Count > 0) result.Models = new List<CliModelInfo>(models);
                        else if (modelsSeen) Log.Write("SlashCommandService: CLI answered initialize without a usable model list");
                        else Log.Write("SlashCommandService: no initialize reply (model list kept as is)");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("SlashCommandService.ProbeAsync: " + ex.Message);
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

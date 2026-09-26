using System;
using System.Collections.Generic;
using System.IO;

namespace ClaudeCode.VisualStudio.Services
{
    /// <summary>
    /// Locates the installed <c>claude</c> CLI. Prefers a native <c>claude.exe</c>
    /// (no shell shim needed); falls back to the npm <c>claude.cmd</c> launched via cmd.exe.
    /// </summary>
    public static class ClaudeCliLocator
    {
        public sealed class Result
        {
            /// <summary>Executable to launch (claude.exe, or cmd.exe for the .cmd shim).</summary>
            public string FileName;

            /// <summary>Leading arguments (e.g. "/c \"...claude.cmd\"") when using the shim.</summary>
            public string ArgumentPrefix = string.Empty;

            /// <summary>True when launched through cmd.exe.</summary>
            public bool ViaCmd;

            /// <summary>The resolved path to the claude launcher, for display/diagnostics.</summary>
            public string ResolvedPath;
        }

        public static Result Locate()
        {
            // 1) explicit override
            var overridePath = Environment.GetEnvironmentVariable("CLAUDE_CODE_VS_CLI");
            if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
            {
                return Wrap(overridePath);
            }

            foreach (var candidate in Candidates())
            {
                if (File.Exists(candidate))
                {
                    return Wrap(candidate);
                }
            }

            // Last resort: assume "claude" is on PATH and let the OS resolve it.
            return new Result { FileName = "claude", ResolvedPath = "claude (PATH)" };
        }

        /// <summary>
        /// True when an actual claude launcher file was found (override or a known/PATH candidate).
        /// False means we'd fall back to a bare "claude" on PATH — i.e. the CLI is likely not
        /// installed. Used to drive the first-run "install the CLI" guidance.
        /// </summary>
        public static bool IsInstalled()
        {
            var overridePath = Environment.GetEnvironmentVariable("CLAUDE_CODE_VS_CLI");
            if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath)) return true;
            foreach (var candidate in Candidates())
                if (File.Exists(candidate)) return true;
            return false;
        }

        /// <summary>
        /// The file that holds the CLI program itself, from the launcher path: a native claude.exe
        /// is the program; an npm install's claude.cmd shim sits next to node_modules holding
        /// cli.js. Null when there is no such local file (a bare "claude" on PATH, an unknown
        /// layout). Reading it needs no process - see <see cref="ModelPricing"/> and the update watch.
        /// </summary>
        internal static string ProgramFile(string launcher)
        {
            try
            {
                if (string.IsNullOrEmpty(launcher) || !Path.IsPathRooted(launcher) || !File.Exists(launcher)) return null;
                if (launcher.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return launcher;
                if (launcher.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || launcher.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                {
                    var pkg = Path.Combine(Path.GetDirectoryName(launcher), "node_modules", "@anthropic-ai", "claude-code");
                    foreach (var c in new[] { Path.Combine(pkg, "cli.js"), Path.Combine(pkg, "bin", "claude.exe") })
                        if (File.Exists(c)) return c;
                }
            }
            catch { }
            return null;
        }

        internal static Result Wrap(string path)
        {
            var isCmd = path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
            if (isCmd)
            {
                return new Result
                {
                    FileName = "cmd.exe",
                    ArgumentPrefix = "/c \"" + path + "\" ",
                    ViaCmd = true,
                    ResolvedPath = path,
                };
            }
            return new Result { FileName = path, ResolvedPath = path };
        }

        private static IEnumerable<string> Candidates()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            yield return Path.Combine(home, ".local", "bin", "claude.exe");
            yield return Path.Combine(appData, "npm", "claude.exe");
            yield return Path.Combine(appData, "npm", "claude.cmd");

            // Walk PATH for claude.exe / claude.cmd
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string trimmed;
                try { trimmed = dir.Trim(); } catch { continue; }
                yield return Path.Combine(trimmed, "claude.exe");
                yield return Path.Combine(trimmed, "claude.cmd");
            }
        }
    }
}

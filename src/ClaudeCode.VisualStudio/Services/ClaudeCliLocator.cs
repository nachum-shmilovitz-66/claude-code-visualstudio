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

            /// <summary>
            /// True when an actual launcher file was found on disk. When false the CLI is not
            /// installed and <see cref="FileName"/> is null — callers must not spawn.
            /// </summary>
            public bool Found;
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

            // Security: no bare-name fallback. Handing "claude" to CreateProcess lets Windows
            // resolve it against the *calling* process's current directory (devenv's), which is
            // the repo the user opened when a .sln is launched from Explorer — so a repo shipping
            // claude.exe would be executed. Report not-found instead and let callers surface the
            // first-run "install the CLI" guidance.
            return new Result { Found = false };
        }

        /// <summary>
        /// Absolute path to an npm launcher on PATH, or null. Security: callers must pass this
        /// absolute path to cmd.exe rather than the bare name "npm" — cmd resolves unqualified
        /// names from its current directory before PATH, so a repo containing npm.cmd would win.
        /// </summary>
        public static string LocateNpm()
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string trimmed;
                try { trimmed = dir.Trim(); } catch { continue; }
                foreach (var name in new[] { "npm.cmd", "npm.exe", "npm.bat" })
                {
                    string candidate;
                    try { candidate = Path.Combine(trimmed, name); } catch { continue; }
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }

        /// <summary>
        /// A working directory that is safe to hand to a cmd.exe launch. Never the opened
        /// solution folder: cmd resolves unqualified command names from its working directory
        /// first, so pointing it at a repository makes that repository's binaries executable.
        /// </summary>
        public static string SafeWorkingDirectory()
        {
            try
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home) && Directory.Exists(home)) return home;
            }
            catch { }
            try { return Environment.GetFolderPath(Environment.SpecialFolder.System); }
            catch { return null; }
        }

        /// <summary>
        /// True when an actual claude launcher file was found (override or a known/PATH candidate).
        /// False means the CLI is not installed — used to drive the first-run "install the CLI"
        /// guidance, and to gate every spawn (there is no bare-name fallback to spawn instead).
        /// </summary>
        public static bool IsInstalled() => Locate().Found;

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
                    Found = true,
                };
            }
            return new Result { FileName = path, ResolvedPath = path, Found = true };
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

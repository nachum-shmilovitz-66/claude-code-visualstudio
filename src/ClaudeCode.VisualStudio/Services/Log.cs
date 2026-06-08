using System;
using System.IO;

namespace ClaudeCode.VisualStudio.Services
{
    /// <summary>
    /// Dead-simple file logger for diagnosing the CLI integration.
    /// Writes to %LOCALAPPDATA%\ClaudeCodeVS\session.log.
    /// </summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        public static readonly string Path = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeCodeVS", "session.log");

        // Security: verbose entries can contain user prompts, model output, and account
        // details. They are written only in Debug, or when the user explicitly opts in via
        // the CLAUDE_CODE_VS_DEBUG environment variable. Release builds stay quiet by default.
#if DEBUG
        public static bool Verbose = true;
#else
        public static bool Verbose =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDE_CODE_VS_DEBUG"));
#endif

        // Cap the log so it can never grow without bound.
        private const long MaxBytes = 5L * 1024 * 1024;

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                    try { var fi = new FileInfo(Path); if (fi.Exists && fi.Length > MaxBytes) File.WriteAllText(Path, string.Empty); }
                    catch { }
                    File.AppendAllText(Path,
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
            }
            catch { }
        }

        /// <summary>
        /// Log only when <see cref="Verbose"/> is enabled. Use for anything that may contain
        /// user content, prompts, model output, credential paths, or account details.
        /// </summary>
        public static void WriteVerbose(string message)
        {
            if (Verbose) Write(message);
        }
    }
}

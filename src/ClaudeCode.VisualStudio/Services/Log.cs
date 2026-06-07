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

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
                    File.AppendAllText(Path,
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}

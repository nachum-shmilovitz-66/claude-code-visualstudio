using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeCode.VisualStudio.Services
{
    /// <summary>
    /// Caches the CLI slash-command list per working directory so the slash palette can show
    /// the full set instantly on the next launch (stale-while-revalidate): the cached list is
    /// loaded synchronously on open, then a background <see cref="SlashCommandService"/> fetch
    /// refreshes both the UI and this cache. Stored under %LOCALAPPDATA%\ClaudeCodeVS\commands,
    /// one file per cwd (the set can differ by project — e.g. project .claude/commands).
    /// </summary>
    public static class SlashCommandCache
    {
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeCodeVS", "commands");

        private static string FileFor(string cwd)
        {
            var key = (cwd ?? string.Empty).Trim().ToLowerInvariant();
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                var sb = new StringBuilder();
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return Path.Combine(Dir, sb.ToString() + ".json");
            }
        }

        public static List<string> Load(string cwd)
        {
            try
            {
                var path = FileFor(cwd);
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
            }
            catch { return null; }
        }

        public static void Save(string cwd, List<string> commands)
        {
            try
            {
                if (commands == null || commands.Count == 0) return;
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FileFor(cwd), JsonSerializer.Serialize(commands));
            }
            catch { }
        }

        public static void Clear(string cwd)
        {
            try { var p = FileFor(cwd); if (File.Exists(p)) File.Delete(p); }
            catch { }
        }
    }
}

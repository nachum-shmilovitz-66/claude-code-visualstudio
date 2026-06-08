using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeCode.VisualStudio.Services
{
    public sealed class StoredMessage
    {
        public string Role { get; set; }   // "user" | "assistant"
        public string Text { get; set; }
    }

    public sealed class SessionRecord
    {
        public string SessionId { get; set; }
        public string Model { get; set; } = "default";
        public string Mode { get; set; } = "default";
        public string Effort { get; set; } = "none";
        public bool ShowThinking { get; set; } = true;
        public List<StoredMessage> Messages { get; set; } = new List<StoredMessage>();
    }

    /// <summary>
    /// Persists a chat session (id + options + transcript) per working directory so the
    /// conversation can be restored when the tool window or Visual Studio is reopened.
    /// Stored under %LOCALAPPDATA%\ClaudeCodeVS\sessions, one file per cwd.
    /// </summary>
    public static class SessionStore
    {
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeCodeVS", "sessions");

        private const int MaxMessages = 200;

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

        public static SessionRecord Load(string cwd)
        {
            try
            {
                var path = FileFor(cwd);
                if (!File.Exists(path)) return null;
                return JsonSerializer.Deserialize<SessionRecord>(File.ReadAllText(path));
            }
            catch { return null; }
        }

        public static void Save(string cwd, SessionRecord rec)
        {
            try
            {
                if (rec == null) return;
                Directory.CreateDirectory(Dir);
                if (rec.Messages != null && rec.Messages.Count > MaxMessages)
                    rec.Messages.RemoveRange(0, rec.Messages.Count - MaxMessages);
                File.WriteAllText(FileFor(cwd), JsonSerializer.Serialize(rec));
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

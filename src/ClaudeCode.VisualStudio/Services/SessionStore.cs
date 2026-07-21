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

        /// <summary>
        /// Working directory <see cref="SessionId"/> was created in. The CLI stores conversations
        /// per directory, so the id is only resumable from here — see <see cref="SessionStore.CanResume"/>.
        /// Null on records written before this field existed.
        /// </summary>
        public string Cwd { get; set; }

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
                var json = ReadDecrypted(path);
                return json == null ? null : JsonSerializer.Deserialize<SessionRecord>(json);
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
                WriteEncrypted(FileFor(cwd), JsonSerializer.Serialize(rec));
            }
            catch { }
        }

        /// <summary>
        /// True when <paramref name="rec"/>'s session id can be passed to <c>--resume</c> for a CLI
        /// launched in <paramref name="cwd"/>.
        ///
        /// The CLI keys its conversation store by working directory (<c>~/.claude/projects/&lt;cwd&gt;/</c>),
        /// so resuming an id from a different directory fails with "No conversation found with session
        /// ID" and a non-zero exit. That happened routinely: the record is loaded once but the cwd is
        /// re-resolved later (the tool window restores before the solution finishes loading, and falls
        /// back to the user profile when no solution is open), so a session started in a solution folder
        /// was saved — and then resumed — under whichever directory was current at the time.
        ///
        /// Legacy records carry no <see cref="SessionRecord.Cwd"/>; those stay resumable so upgrading
        /// does not drop a live conversation. A wrong one now clears itself on the first failure
        /// instead of failing on every send forever.
        /// </summary>
        public static bool CanResume(SessionRecord rec, string cwd)
        {
            if (rec == null || string.IsNullOrEmpty(rec.SessionId)) return false;
            if (string.IsNullOrEmpty(rec.Cwd)) return true;
            return string.Equals(Normalize(rec.Cwd), Normalize(cwd), StringComparison.OrdinalIgnoreCase);
        }

        // Trailing separators are not a different directory; the cwd reaches us from several
        // places (solution path, user profile) that do not agree on one.
        private static string Normalize(string cwd)
            => (cwd ?? string.Empty).Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        public static void Clear(string cwd)
        {
            try { var p = FileFor(cwd); if (File.Exists(p)) File.Delete(p); }
            catch { }
        }

        // The transcript can contain anything discussed in chat (incl. secrets), so it is encrypted
        // at rest with DPAPI — per-user, machine-bound, no key management. A short magic prefix marks
        // an encrypted file; a file without it is read as legacy plaintext (written before encryption
        // was added) and silently re-encrypted on the next Save.
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CCVS1\n");

        private static void WriteEncrypted(string path, string json)
        {
            try
            {
                var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
                var buf = new byte[Magic.Length + cipher.Length];
                Buffer.BlockCopy(Magic, 0, buf, 0, Magic.Length);
                Buffer.BlockCopy(cipher, 0, buf, Magic.Length, cipher.Length);
                File.WriteAllBytes(path, buf);
            }
            catch
            {
                // DPAPI unavailable (rare) — fall back to plaintext so the conversation still persists.
                File.WriteAllText(path, json);
            }
        }

        private static string ReadDecrypted(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (HasMagic(bytes))
            {
                var cipher = new byte[bytes.Length - Magic.Length];
                Buffer.BlockCopy(bytes, Magic.Length, cipher, 0, cipher.Length);
                var plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            // Legacy plaintext JSON (pre-encryption). Read as-is; the next Save upgrades it.
            return Encoding.UTF8.GetString(bytes);
        }

        private static bool HasMagic(byte[] b)
        {
            if (b.Length < Magic.Length) return false;
            for (int i = 0; i < Magic.Length; i++) if (b[i] != Magic[i]) return false;
            return true;
        }
    }
}

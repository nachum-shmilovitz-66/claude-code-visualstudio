using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ClaudeCode.VisualStudio.Services
{
    /// <summary>
    /// Caches the model list the CLI last reported, so the picker shows current names the moment
    /// the panel opens instead of the hardcoded fallback rows for the seconds the startup probe
    /// takes (stale-while-revalidate, like <see cref="SlashCommandCache"/>). One file for the
    /// machine - the list depends on the installed CLI and the signed-in account, not on the
    /// project - at %LOCALAPPDATA%\ClaudeCodeVS\models.json.
    /// <para>
    /// The file carries the <see cref="CliModelList.DefaultsStamp"/> it was written under. A list
    /// cached before this release's fallback rows is dropped, so an update opens on the models it
    /// ships with, not on whatever an older CLI reported, until the live probe answers.
    /// </para>
    /// </summary>
    public static class ModelListCache
    {
        /// <summary>On-disk shape: the rows, and the fallback defaults they were cached under.</summary>
        public sealed class CachedList
        {
            public string Defaults { get; set; }
            public List<CliModelInfo> Models { get; set; }
        }

        private static readonly string DefaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeCodeVS", "models.json");

        // Tests point this at a scratch file so they never touch the real cache.
        internal static string PathOverride { get; set; }

        private static string FilePath => PathOverride ?? DefaultPath;

        /// <summary>The cached rows, or null when there is no usable cache.</summary>
        public static List<CliModelInfo> Load()
        {
            try
            {
                var path = FilePath;
                if (!File.Exists(path)) return null;
                var text = File.ReadAllText(path).TrimStart();
                // A bare array is the format before the stamp existed: older than these defaults.
                if (!text.StartsWith("{", StringComparison.Ordinal)) return null;
                var cached = JsonSerializer.Deserialize<CachedList>(text);
                if (cached == null || cached.Defaults != CliModelList.DefaultsStamp()) return null;
                var rows = cached.Models;
                if (rows == null) return null;
                // Defensive: the file is user-writable, and every id ends up on a command line.
                var ok = new List<CliModelInfo>(rows.Count);
                foreach (var r in rows)
                {
                    if (r == null || string.IsNullOrEmpty(r.Id) || string.IsNullOrEmpty(r.Name)) continue;
                    if (InputValidation.SanitizeModel(r.Id, null) == null) continue;
                    ok.Add(r);
                }
                return ok.Count > 0 ? ok : null;
            }
            catch { return null; }
        }

        public static void Save(List<CliModelInfo> models)
        {
            try
            {
                if (models == null || models.Count == 0) return;
                var path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonSerializer.Serialize(new CachedList { Defaults = CliModelList.DefaultsStamp(), Models = models }));
            }
            catch { }
        }

        public static void Clear()
        {
            try { var p = FilePath; if (File.Exists(p)) File.Delete(p); }
            catch { }
        }
    }
}

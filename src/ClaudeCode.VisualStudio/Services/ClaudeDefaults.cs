using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ClaudeCode.VisualStudio.Services
{
    /// <summary>
    /// The options a new session starts on, taken from Claude's own configuration rather than from
    /// whatever the previous session used. The model is always the CLI's recommendation (the picker's
    /// "default" row, launched without <c>--model</c>), so only effort and permission mode are read
    /// here: from the same settings files and environment the CLI reads.
    /// </summary>
    public static class ClaudeDefaults
    {
        /// <summary>The picker id of the CLI's recommended model.</summary>
        public const string Model = "default";

        // The CLI's own default effort when nothing configures one (its per-model fallback).
        private const string BuiltInEffort = "high";

        // The mode a new session starts in when no settings file names one: Auto.
        private const string BuiltInMode = "bypassPermissions";

        /// <summary>
        /// Effort for a new session: <c>CLAUDE_CODE_EFFORT_LEVEL</c>, else the <c>effortLevel</c>
        /// setting (project local, project, then user settings), else the CLI's built-in default.
        /// </summary>
        public static string Effort(string cwd)
        {
            return EffortFrom(Environment.GetEnvironmentVariable("CLAUDE_CODE_EFFORT_LEVEL"), LoadSettings(cwd));
        }

        /// <summary>
        /// Permission mode for a new session: <c>permissions.defaultMode</c> from the settings files,
        /// else Auto mode.
        /// </summary>
        public static string PermissionMode(string cwd)
        {
            return ModeFrom(LoadSettings(cwd));
        }

        internal static string EffortFrom(string env, IList<JsonElement> settings)
        {
            var e = NormalizeEffort(env);
            if (e != null) return e;
            for (int i = settings.Count - 1; i >= 0; i--)
            {
                e = NormalizeEffort(Str(settings[i], "effortLevel"));
                if (e != null) return e;
            }
            return BuiltInEffort;
        }

        internal static string ModeFrom(IList<JsonElement> settings)
        {
            for (int i = settings.Count - 1; i >= 0; i--)
            {
                JsonElement perms;
                if (!settings[i].TryGetProperty("permissions", out perms) || perms.ValueKind != JsonValueKind.Object) continue;
                var m = NormalizeMode(Str(perms, "defaultMode"));
                if (m != null) return m;
            }
            return BuiltInMode;
        }

        // The CLI's level names onto the slider's ids ("xhigh" is "extrahigh"); null for anything
        // the slider has no step for ("auto", "unset", a typo).
        internal static string NormalizeEffort(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            v = v.Trim().ToLowerInvariant();
            if (v == "xhigh") v = "extrahigh";
            return Array.IndexOf(InputValidation.AllowedEfforts, v) >= 0 ? v : null;
        }

        // The CLI's "auto" mode is the extension's Auto mode; a mode the panel does not offer
        // ("dontAsk") falls through to the next settings file.
        internal static string NormalizeMode(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            v = v.Trim();
            if (string.Equals(v, "auto", StringComparison.OrdinalIgnoreCase)) return "bypassPermissions";
            return Array.IndexOf(InputValidation.AllowedModes, v) >= 0 ? v : null;
        }

        // Settings files in increasing precedence: user, project, project local. Unreadable or
        // malformed files are skipped.
        private static List<JsonElement> LoadSettings(string cwd)
        {
            var paths = new List<string>();
            var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrWhiteSpace(configDir))
                configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            paths.Add(Path.Combine(configDir, "settings.json"));
            if (!string.IsNullOrEmpty(cwd))
            {
                paths.Add(Path.Combine(cwd, ".claude", "settings.json"));
                paths.Add(Path.Combine(cwd, ".claude", "settings.local.json"));
            }

            var list = new List<JsonElement>();
            foreach (var p in paths)
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    using (var doc = JsonDocument.Parse(File.ReadAllText(p), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
                    {
                        if (doc.RootElement.ValueKind == JsonValueKind.Object) list.Add(doc.RootElement.Clone());
                    }
                }
                catch (Exception ex) { Log.Write("ClaudeDefaults: " + p + ": " + ex.Message); }
            }
            return list;
        }

        private static string Str(JsonElement o, string name)
        {
            JsonElement v;
            return o.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ClaudeCode.VisualStudio.Services
{
    /// <summary>
    /// Security: the model / permission-mode / effort values arrive from the (local but
    /// untrusted) WebView and are forwarded to the claude CLI command line. Validate them
    /// first so a crafted value can't inject extra CLI flags — or, through the cmd.exe
    /// shim, shell metacharacters.
    /// </summary>
    internal static class InputValidation
    {
        internal static readonly string[] AllowedModes = { "default", "acceptEdits", "plan", "bypassPermissions" };
        internal static readonly string[] AllowedEfforts = { "none", "low", "medium", "high", "extrahigh", "max", "ultracode" };

        // Model ids are open-ended (new models ship without an extension update), so instead of
        // an allow-list, enforce a strict shape: first char alphanumeric (never '-', so the value
        // can't parse as another CLI flag), then letters/digits/dot/dash/brackets (brackets for
        // context-window suffixes like "[1m]"), max 64 chars. No spaces, quotes, or cmd.exe
        // metacharacters (& | < > ^ % !), so the value can't escape the --model argument.
        // \z (not $): in .NET, $ also matches before a string-final '\n', which would let a
        // trailing newline through to the cmd.exe shim.
        private static readonly Regex ModelIdShape = new Regex(@"^[A-Za-z0-9][A-Za-z0-9.\[\]-]{0,63}\z", RegexOptions.Compiled);

        internal static string SanitizeModel(string value, string fallback)
        {
            return !string.IsNullOrEmpty(value) && ModelIdShape.IsMatch(value) ? value : fallback;
        }

        internal static string SanitizeChoice(string value, string[] allowed, string fallback)
        {
            if (!string.IsNullOrEmpty(value) && allowed != null)
                foreach (var a in allowed)
                    if (string.Equals(a, value, StringComparison.Ordinal)) return value;
            return fallback;
        }

        /// <summary>
        /// True when <paramref name="path"/> resolves to a location inside one of
        /// <paramref name="roots"/>. Used to confine WebView-supplied paths to the workspace.
        /// Compares canonicalized full paths, so "..\" segments, mixed separators and relative
        /// forms cannot walk out of a root; the appended separator stops "C:\src-evil" from
        /// matching the root "C:\src".
        /// </summary>
        internal static bool IsUnderAnyRoot(string path, IEnumerable<string> roots)
        {
            if (string.IsNullOrEmpty(path) || roots == null) return false;
            string full;
            try { full = Path.GetFullPath(path); } catch { return false; }

            foreach (var r in roots)
            {
                if (string.IsNullOrEmpty(r)) continue;
                string root;
                try { root = Path.GetFullPath(r); } catch { continue; }
                if (root.Length == 0) continue;
                if (root[root.Length - 1] != Path.DirectorySeparatorChar)
                    root += Path.DirectorySeparatorChar;
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// True when <paramref name="path"/> is one of <paramref name="candidates"/>, compared as
        /// canonicalized full paths.
        /// </summary>
        internal static bool IsSamePath(string path, IEnumerable<string> candidates)
        {
            if (string.IsNullOrEmpty(path) || candidates == null) return false;
            string full;
            try { full = Path.GetFullPath(path); } catch { return false; }

            foreach (var c in candidates)
            {
                if (string.IsNullOrEmpty(c)) continue;
                try
                {
                    if (string.Equals(Path.GetFullPath(c), full, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }
            }
            return false;
        }
    }
}

using System;
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
    }
}

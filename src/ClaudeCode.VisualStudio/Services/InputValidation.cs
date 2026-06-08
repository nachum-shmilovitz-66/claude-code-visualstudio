using System;

namespace ClaudeCode.VisualStudio.Services
{
    /// <summary>
    /// Security: the model / permission-mode / effort values arrive from the (local but
    /// untrusted) WebView and are forwarded to the claude CLI command line. Validate them
    /// against allow-lists first so a crafted value can't inject extra CLI flags — or, through
    /// the cmd.exe shim, shell metacharacters.
    /// </summary>
    internal static class InputValidation
    {
        internal static readonly string[] AllowedModels = { "default", "opus", "sonnet", "haiku" };
        internal static readonly string[] AllowedModes = { "default", "acceptEdits", "plan", "bypassPermissions" };
        internal static readonly string[] AllowedEfforts = { "none", "low", "medium", "high", "extrahigh", "max", "ultracode" };

        internal static string SanitizeChoice(string value, string[] allowed, string fallback)
        {
            if (!string.IsNullOrEmpty(value) && allowed != null)
                foreach (var a in allowed)
                    if (string.Equals(a, value, StringComparison.Ordinal)) return value;
            return fallback;
        }
    }
}

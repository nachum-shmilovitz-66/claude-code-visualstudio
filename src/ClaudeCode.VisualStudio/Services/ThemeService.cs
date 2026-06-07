using System;
using System.Collections.Generic;
using System.Drawing;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace ClaudeCode.VisualStudio.Services
{
    /// <summary>
    /// Maps the current Visual Studio theme to the CSS custom properties the chat UI uses,
    /// so the WebView matches VS light/dark/blue themes. Raises <see cref="ThemeChanged"/>
    /// when the user switches themes.
    /// </summary>
    public sealed class ThemeService
    {
        public event Action<Dictionary<string, string>> ThemeChanged;

        public ThemeService()
        {
            VSColorTheme.ThemeChanged += _ => ThemeChanged?.Invoke(GetThemeVariables());
        }

        public Dictionary<string, string> GetThemeVariables()
        {
            return new Dictionary<string, string>
            {
                ["--bg"] = Hex(EnvironmentColors.ToolWindowBackgroundColorKey, Color.FromArgb(30, 30, 30)),
                ["--bg-alt"] = Hex(EnvironmentColors.CommandBarGradientBeginColorKey, Color.FromArgb(37, 37, 38)),
                ["--bg-input"] = Hex(EnvironmentColors.ComboBoxBackgroundColorKey, Color.FromArgb(60, 60, 60)),
                ["--fg"] = Hex(EnvironmentColors.ToolWindowTextColorKey, Color.FromArgb(212, 212, 212)),
                ["--fg-dim"] = Hex(EnvironmentColors.SystemGrayTextColorKey, Color.FromArgb(157, 157, 157)),
                ["--border"] = Hex(EnvironmentColors.ToolWindowBorderColorKey, Color.FromArgb(60, 60, 60)),
                ["--code-bg"] = Hex(EnvironmentColors.ToolWindowBackgroundColorKey, Color.FromArgb(27, 27, 27)),
                ["--user-bg"] = Hex(EnvironmentColors.ToolWindowTabSelectedTabColorKey, Color.FromArgb(45, 58, 79)),
                // Claude brand accent stays constant across themes.
                ["--accent"] = "#cc7a3b",
                ["--accent-fg"] = "#ffffff",
            };
        }

        private static string Hex(ThemeResourceKey key, Color fallback)
        {
            try
            {
                var c = VSColorTheme.GetThemedColor(key);
                if (c.A == 0) c = fallback;
                return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
            }
            catch
            {
                return "#" + fallback.R.ToString("X2") + fallback.G.ToString("X2") + fallback.B.ToString("X2");
            }
        }
    }
}

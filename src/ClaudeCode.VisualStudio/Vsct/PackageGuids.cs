using System;

namespace ClaudeCode.VisualStudio
{
    /// <summary>
    /// GUID constants shared between the C# code and the .vsct command table.
    /// </summary>
    internal static class PackageGuids
    {
        public const string ClaudeCodePackageString = "15FC1958-19BD-4195-8BB9-AFD6DD0F334C";
        public static readonly Guid ClaudeCodePackage = new Guid(ClaudeCodePackageString);

        public const string ClaudeCodeCmdSetString = "6DECCA05-E7F8-4DB9-9464-B88215C64A25";
        public static readonly Guid ClaudeCodeCmdSet = new Guid(ClaudeCodeCmdSetString);

        public const string ClaudeChatToolWindowString = "9B6F5F3D-6C8C-4DEE-A846-437DD6CDCD10";
        public static readonly Guid ClaudeChatToolWindow = new Guid(ClaudeChatToolWindowString);
    }
}

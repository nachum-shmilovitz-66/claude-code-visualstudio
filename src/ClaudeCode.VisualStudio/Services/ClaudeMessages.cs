using System.Collections.Generic;

namespace ClaudeCode.VisualStudio.Services
{
    public sealed class SystemInitInfo
    {
        public string SessionId;
        public string Model;
        public string Cwd;
        public string PermissionMode;
        public List<string> Tools = new List<string>();
        public List<string> SlashCommands = new List<string>();
        public List<string> McpServers = new List<string>();   // "name (status)"
        public string Version;
    }

    public sealed class ToolUseInfo
    {
        public string Id;
        public string Name;
        public string InputJson;   // raw JSON string of the tool input
    }

    public sealed class ToolResultInfo
    {
        public string ToolUseId;
        public string Content;
        public bool IsError;
    }

    public sealed class ResultInfo
    {
        public bool IsError;
        public string Text;
        public double CostUsd;
        public long InputTokens;
        public long OutputTokens;
        public long CacheReadTokens;
        public long CacheCreationTokens;
        public long ContextWindow;
        public string Model;
        public long DurationMs;
        public string SessionId;
    }

    public sealed class PermissionRequestInfo
    {
        public string RequestId;
        public string ToolName;
        public string InputJson;
    }

    public sealed class ImageInput
    {
        public string MediaType;
        public string Data;   // base64
    }

    /// <summary>
    /// A <c>system/compact_boundary</c> event: the CLI compacted its own context in place.
    /// Carries the <c>compact_metadata</c> block, so the UI can report how much was reclaimed.
    /// </summary>
    public sealed class CompactInfo
    {
        public string Trigger;      // "manual" (user ran /compact) | "auto" (context filled up)
        public long PreTokens;
        public long PostTokens;
        public long DurationMs;
    }
}

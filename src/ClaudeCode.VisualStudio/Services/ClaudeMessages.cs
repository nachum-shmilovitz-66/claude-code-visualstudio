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

    /// <summary>
    /// Usage of a SINGLE API request, taken from the stream's `message_start`. The prompt fields
    /// describe exactly what was in the model's context for that request, which is what the context
    /// ring wants. The `result` event's usage cannot answer that: it is summed over every request in
    /// the turn, so a turn with N tool round-trips counts the cached prefix N times and easily
    /// exceeds the window.
    /// </summary>
    public sealed class ContextUsageInfo
    {
        public long InputTokens;
        public long CacheReadTokens;
        public long CacheCreationTokens;
        public long OutputTokens;          // of the response being streamed (grows during the turn)
        public long PromptTokens => InputTokens + CacheReadTokens + CacheCreationTokens;
        public long TotalTokens => PromptTokens + OutputTokens;
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

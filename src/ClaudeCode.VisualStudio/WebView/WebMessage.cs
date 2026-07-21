using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeCode.VisualStudio.WebView
{
    /// <summary>
    /// Envelope for messages exchanged between the C# host and the WebView2 chat UI.
    /// <c>type</c> identifies the message; <c>payload</c> carries arbitrary JSON.
    /// </summary>
    public sealed class WebMessage
    {
        [JsonPropertyName("type")]
        public string Type { get; set; }

        [JsonPropertyName("payload")]
        public JsonElement Payload { get; set; }

        public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Relaxed escaping only skips escaping HTML-sensitive characters inside JSON string
            // values; quote and backslash escaping are unaffected, so a value containing markup
            // or quotes still cannot break the envelope. It is safe *because* this JSON is only
            // ever delivered via PostWebMessageAsJson and parsed as JSON — it is never embedded
            // in an HTML or <script> context. Keep it that way; if a caller ever inlines this
            // output into a document, switch to JavaScriptEncoder.Default first.
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        /// <summary>Build an outbound envelope JSON string for posting to the WebView.</summary>
        public static string Build(string type, object payload)
        {
            var dict = new Dictionary<string, object>
            {
                ["type"] = type,
                ["payload"] = payload,
            };
            return JsonSerializer.Serialize(dict, JsonOptions);
        }

        /// <summary>Parse an inbound envelope coming from the WebView.</summary>
        public static WebMessage Parse(string json)
        {
            return JsonSerializer.Deserialize<WebMessage>(json, JsonOptions);
        }
    }
}

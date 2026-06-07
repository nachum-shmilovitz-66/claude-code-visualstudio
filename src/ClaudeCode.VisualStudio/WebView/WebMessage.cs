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

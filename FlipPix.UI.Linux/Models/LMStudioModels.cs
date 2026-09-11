using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FlipPix.UI.Linux.Models
{
    public class LMStudioModel
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("object")]
        public string Object { get; set; } = "model";

        [JsonPropertyName("created")]
        public long Created { get; set; }

        [JsonPropertyName("owned_by")]
        public string OwnedBy { get; set; } = string.Empty;

        // Only llama-server sends "name"; Ollama and LM Studio's /v1/models carry just "id".
        // Callers pick and send models by Name, so fall back to the id rather than relying on
        // each fetch to patch it in — same as the FlipPix.UI copy.
        private string _name = string.Empty;

        [JsonPropertyName("name")]
        public string Name
        {
            get => string.IsNullOrWhiteSpace(_name) ? Id : _name;
            set => _name = value ?? string.Empty;
        }

        public override string ToString()
        {
            return Name;
        }
    }

    public class LMStudioModelsResponse
    {
        [JsonPropertyName("data")]
        public List<LMStudioModel> Data { get; set; } = new List<LMStudioModel>();
    }

    public class LMStudioChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<LMStudioMessage> Messages { get; set; } = new List<LMStudioMessage>();

        [JsonPropertyName("stream")]
        public bool Stream { get; set; } = false;

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; } = 1000;

        [JsonPropertyName("temperature")]
        public double Temperature { get; set; } = 0.7;
    }

    public class LMStudioMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty; // "system", "user", "assistant"

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        // DeepSeek/Gemma reasoning format: thinking goes here when reasoning_in_content=false
        [JsonPropertyName("reasoning_content")]
        public string? ReasoningContent { get; set; }

        // Returns the actual text regardless of which field the model put it in
        [JsonIgnore]
        public string EffectiveContent => string.IsNullOrEmpty(Content)
            ? (ReasoningContent ?? string.Empty)
            : Content;
    }

    public class LMStudioChatResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("object")]
        public string Object { get; set; } = "chat.completion";

        [JsonPropertyName("created")]
        public long Created { get; set; }

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("choices")]
        public List<LMStudioChoice> Choices { get; set; } = new List<LMStudioChoice>();
    }

    public class LMStudioChoice
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("message")]
        public LMStudioMessage Message { get; set; } = new LMStudioMessage();

        [JsonPropertyName("finish_reason")]
        public string FinishReason { get; set; } = string.Empty;
    }

    public class LMStudioVisionRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<LMStudioMessage> Messages { get; set; } = new List<LMStudioMessage>();

        [JsonPropertyName("stream")]
        public bool Stream { get; set; } = false;

        [JsonPropertyName("max_tokens")]
        public int MaxTokens { get; set; } = 1000;

        [JsonPropertyName("temperature")]
        public double Temperature { get; set; } = 0.7;
    }

    public class LMStudioContentPart
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("image_url")]
        public LMStudioImageUrl ImageUrl { get; set; } = new LMStudioImageUrl();
    }

    public class LMStudioImageUrl
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;
    }
}
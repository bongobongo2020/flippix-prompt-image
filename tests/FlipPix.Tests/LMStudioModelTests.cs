using System.Linq;
using System.Text.Json;
using FlipPix.UI.Models;

namespace FlipPix.Tests;

/// <summary>
/// <see cref="LMStudioModel"/> — the Settings window lists, selects and saves models by
/// <see cref="LMStudioModel.Name"/>. Ollama's /v1/models sends no "name", so before the id fallback the
/// dialog reported "2 models" and showed two blank rows that saved as no model at all.
/// </summary>
public class LMStudioModelTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void Name_falls_back_to_the_id_when_the_server_sends_none()
    {
        // Verbatim from Ollama at 10.0.0.10:11434, 2026-09-10.
        const string json =
            "{\"object\":\"list\",\"data\":[" +
            "{\"id\":\"qwen-vl:latest\",\"object\":\"model\",\"created\":1789084207,\"owned_by\":\"library\"}," +
            "{\"id\":\"harmonia:latest\",\"object\":\"model\",\"created\":1789081447,\"owned_by\":\"library\"}]}";

        var models = JsonSerializer.Deserialize<LMStudioModelsResponse>(json, Options)!.Data;

        Assert.Equal(new[] { "qwen-vl:latest", "harmonia:latest" }, models.Select(m => m.Name));
    }

    [Fact]
    public void Name_keeps_the_server_name_when_one_is_sent()
    {
        const string json = "{\"data\":[{\"id\":\"/models/qwen.gguf\",\"name\":\"qwen\"}]}";

        var model = JsonSerializer.Deserialize<LMStudioModelsResponse>(json, Options)!.Data.Single();

        Assert.Equal("qwen", model.Name);
    }
}

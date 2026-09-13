using System.Text.Json.Serialization;

namespace Clash.Json;

internal sealed class DohJsonResponse
{
    [JsonPropertyName("Status")]
    public int Status { get; set; }

    [JsonPropertyName("Answer")]
    public List<DohAnswer>? Answer { get; set; }
}

internal sealed class DohAnswer
{
    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("TTL")]
    public int TTL { get; set; }

    [JsonPropertyName("data")]
    public string? Data { get; set; }
}

[JsonSerializable(typeof(DohJsonResponse))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class ClashJsonContext : JsonSerializerContext;

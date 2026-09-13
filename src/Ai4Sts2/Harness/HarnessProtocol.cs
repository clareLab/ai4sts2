using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ai4Sts2.Harness;

public sealed record HarnessRequest(string Id, string Op, JsonElement? Args);

public sealed record HarnessResult(string Id, bool Ok, string? Error, JsonElement? Payload);

public static class HarnessJson
{
    public static JsonSerializerOptions Options { get; } =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
        };
}

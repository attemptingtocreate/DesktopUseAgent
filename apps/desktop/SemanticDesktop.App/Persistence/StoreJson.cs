using System.Text.Json;
using System.Text.Json.Serialization;
using SemanticDesktop.Core.Serialization;

namespace SemanticDesktop.App.Persistence;

internal static class StoreJson
{
    public static JsonSerializerOptions FileOptions { get; } = CreateFileOptions();

    public static JsonSerializerOptions MemoryOptions => JsonDefaults.Options;

    private static JsonSerializerOptions CreateFileOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static T Clone<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, MemoryOptions);
        return JsonSerializer.Deserialize<T>(json, MemoryOptions)!;
    }
}

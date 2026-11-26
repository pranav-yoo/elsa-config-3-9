using System.Text.Json;
using System.Text.Json.Serialization;

namespace IlWorkflow.Application.Common;

public static class ConfigurationJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new ArgumentException("A JSON object is required.");
}

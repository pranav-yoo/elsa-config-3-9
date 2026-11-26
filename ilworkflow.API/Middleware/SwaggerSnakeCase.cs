using System.Text.Json;
using System.Text.Json.Nodes;

namespace IlWorkflow.API.Middleware;

/// <summary>
/// Elsa's AddSwagger() generates the OpenAPI document with FastEndpoints, which ignores the MVC
/// PropertyNamingPolicy (SnakeCaseLower). Without this, Swagger shows PascalCase for our request and response models
/// while the API only accepts snake_case. Elsa's own schemas keep their camelCase and are not touched.
/// </summary>
public static class SwaggerSnakeCase
{
    public static IApplicationBuilder UseSwaggerSnakeCase(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        if (!(http.Request.Path.Value ?? "").EndsWith("/swagger.json", StringComparison.OrdinalIgnoreCase)) { await next(); return; }
        var original = http.Response.Body;
        using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        try { await next(); } finally { http.Response.Body = original; }
        var bytes = buffer.ToArray();
        try
        {
            if (http.Response.StatusCode == StatusCodes.Status200OK && JsonNode.Parse(bytes) is JsonObject document && document["components"]?["schemas"] is JsonObject schemas)
            {
                foreach (var (name, schema) in schemas.ToArray())
                    if (name.StartsWith("IlWorkflow", StringComparison.Ordinal) && schema is JsonObject target) Rename(target);
                bytes = JsonSerializer.SerializeToUtf8Bytes(document);
            }
        }
        catch (JsonException) { /* not JSON (e.g. compressed): serve the original bytes */ }
        http.Response.ContentLength = bytes.Length;
        await original.WriteAsync(bytes, http.RequestAborted);
    });

    // Only PascalCase names are converted: explicit names (already lower-case) are already what the serializer emits.
    private static string Convert(string name) => char.IsUpper(name[0]) ? JsonNamingPolicy.SnakeCaseLower.ConvertName(name) : name;

    private static void Rename(JsonObject schema)
    {
        if (schema["properties"] is JsonObject properties)
        {
            var items = properties.ToArray();
            properties.Clear();
            foreach (var (name, value) in items) properties[Convert(name)] = value;
        }
        if (schema["required"] is JsonArray required)
        {
            var names = required.Select(x => Convert(x!.GetValue<string>())).ToArray();
            required.Clear();
            foreach (var name in names) required.Add(name);
        }
    }
}

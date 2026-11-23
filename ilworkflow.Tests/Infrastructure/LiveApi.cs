using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ilworkflow.Tests.Infrastructure;

/// <summary>
/// Minimal HTTP client for live acceptance checks against a running ilworkflow API.
/// Tokens are kept in memory only and never written to test output.
/// </summary>
public sealed class LiveApi : IDisposable
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly HttpClient http;

    public LiveApi(TimeSpan? timeout = null)
    {
        BaseUrl = LiveSettings.BaseUrl;
        http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(35) };
    }

    public string BaseUrl { get; }

    public Dictionary<string, string> Tokens { get; } = new();

    /// <summary>Sends a request and asserts that the status is one of <paramref name="expected"/>.</summary>
    /// <param name="body">null, a <see cref="byte"/> array (sent raw), a <see cref="JsonNode"/>, or any serializable object.</param>
    /// <param name="actor">Key in <see cref="Tokens"/>, or null for an anonymous request.</param>
    public async Task<(int Status, JsonNode? Body)> SendAsync(
        string method, string path, object? body = null, string? actor = "admin", params int[] expected)
    {
        if (expected.Length == 0) expected = [200, 201, 202];
        var url = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : BaseUrl + path;
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (actor is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Tokens[actor]);
        // Application /api/* endpoints use snake_case; tests are written with camelCase names. Convert at this boundary.
        var application = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
        if (body is not null)
        {
            var bytes = body switch
            {
                byte[] bytesBody => bytesBody,
                JsonNode node when application => Encoding.UTF8.GetBytes(RenameKeys(node.DeepClone(), ToSnake)!.ToJsonString(Json)),
                JsonNode node => Encoding.UTF8.GetBytes(node.ToJsonString(Json)),
                _ when application => Encoding.UTF8.GetBytes(RenameKeys(JsonSerializer.SerializeToNode(body, Json), ToSnake)!.ToJsonString(Json)),
                _ => JsonSerializer.SerializeToUtf8Bytes(body, Json),
            };
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);
        var status = (int)response.StatusCode;
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(expected.Contains(status),
            $"{method} {path}: HTTP {status} (expected {string.Join("/", expected)}): {Truncate(raw, 1200)}");
        var parsed = string.IsNullOrWhiteSpace(raw) ? null : JsonNode.Parse(raw);
        // Application /api/* endpoints use the company envelope { trace_id, messages, data }; tests assert on data.
        if (parsed is JsonObject envelope && envelope.ContainsKey("trace_id") && envelope.ContainsKey("messages") && envelope["data"] is { } data)
            parsed = RenameKeys(data.DeepClone(), ToCamel);
        return (status, parsed);
    }

    // Property names only; user attribute names have no underscores/uppercase-after-lowercase issues in these tests.
    private static JsonNode? RenameKeys(JsonNode? node, Func<string, string> rename)
    {
        switch (node)
        {
            case JsonObject obj:
                var renamed = new JsonObject();
                foreach (var (key, value) in obj.ToList()) { obj.Remove(key); renamed[rename(key)] = RenameKeys(value, rename); }
                return renamed;
            case JsonArray array:
                var items = array.ToList(); array.Clear();
                foreach (var item in items) array.Add(RenameKeys(item, rename));
                return array;
            default: return node;
        }
    }
    private static string ToSnake(string name) => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(name);
    private static string ToCamel(string name) => name.Contains('_')
        ? string.Concat(name.Split('_').Select((part, i) => i == 0 ? part : part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]))
        : name;

    /// <summary>Sends a request and returns only the parsed body.</summary>
    public async Task<JsonNode> CallAsync(string method, string path, object? body = null, string? actor = "admin", params int[] expected)
        => (await SendAsync(method, path, body, actor, expected)).Body!;

    public Task<JsonNode> GetAsync(string path, string? actor = "admin", params int[] expected)
        => CallAsync("GET", path, null, actor, expected);

    public Task<JsonNode> PostAsync(string path, object? body, string? actor = "admin", params int[] expected)
        => CallAsync("POST", path, body, actor, expected);

    public async Task LoginAsync(string actor, string username, string password)
    {
        var result = await PostAsync("/elsa/api/identity/login", new { username, password }, null);
        var token = result["accessToken"]?.GetValue<string>();
        Assert.False(string.IsNullOrEmpty(token), $"Login failed: {actor}");
        Tokens[actor] = token!;
    }

    /// <summary>Polls <paramref name="get"/> until <paramref name="predicate"/> holds (60 x 100 ms).</summary>
    public static async Task<T> WaitForAsync<T>(Func<Task<T>> get, Func<T, bool> predicate, int attempts = 60, int delayMs = 100)
    {
        T result = default!;
        for (var i = 0; i < attempts; i++)
        {
            result = await get();
            if (predicate(result)) return result;
            await Task.Delay(delayMs, TestContext.Current.CancellationToken);
        }
        throw new Xunit.Sdk.XunitException("Condition not reached: " + Truncate(Describe(result), 1500));
    }

    public static string NewId() => Guid.NewGuid().ToString();

    public static string Hex() => Guid.NewGuid().ToString("N");

    public static JsonNode Node(object value) => JsonSerializer.SerializeToNode(value, Json)!;

    public static string Truncate(string? value, int length)
        => value is null ? "" : value.Length <= length ? value : value[..length];

    private static string Describe(object? value) => value switch
    {
        null => "null",
        JsonNode node => node.ToJsonString(Json),
        _ => JsonSerializer.Serialize(value, Json),
    };

    public void Dispose() => http.Dispose();
}

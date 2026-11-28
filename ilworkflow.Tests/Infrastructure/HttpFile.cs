using System.Text;
using System.Text.RegularExpressions;

namespace ilworkflow.Tests.Infrastructure;

/// <summary>One named request of a REST Client <c>.http</c> file, before variable expansion.</summary>
public sealed record HttpFileRequest(string Name, string Template, int[] Expected);

/// <summary>
/// Reads the MAIN SEQUENCE (everything before <c># END MAIN SEQUENCE</c>) of a <c>.http</c> file and
/// expands <c>{{variable}}</c>, <c>{{$guid}}</c> and <c>{{name.response.body.$.path[0].x}}</c> references.
/// </summary>
public sealed partial class HttpFile
{
    public HttpFile(string path)
    {
        Source = File.ReadAllText(path).Split("# END MAIN SEQUENCE")[0];
        foreach (Match match in VariableRegex().Matches(Source))
            Variables[match.Groups[1].Value] = match.Groups[2].Value.TrimEnd('\r');
        foreach (var section in SectionRegex().Split(Source))
        {
            var named = NameRegex().Match(section);
            if (!named.Success) continue;
            var expect = ExpectRegex().Match(section);
            var expected = expect.Success
                ? expect.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray()
                : [200, 201, 202];
            Requests.Add(new HttpFileRequest(named.Groups[1].Value, section.Replace("\r\n", "\n"), expected));
        }
    }

    public string Source { get; }

    public Dictionary<string, string> Variables { get; } = new();

    public List<HttpFileRequest> Requests { get; } = [];

    /// <summary>Expands a request template into method, URL, headers and optional JSON body.</summary>
    public static (string Method, string Url, Dictionary<string, string> Headers, byte[]? Body) Expand(
        HttpFileRequest request, IReadOnlyDictionary<string, string> variables, IReadOnlyDictionary<string, JsonNode?> responses)
    {
        var expanded = ReferenceRegex().Replace(request.Template, match => Substitute(match.Groups[1].Value, variables, responses));
        var line = RequestLineRegex().Match(expanded);
        Assert.True(line.Success, $"{request.Name}: no request line");
        var rest = line.Groups[3].Value;
        var split = rest.IndexOf("\n\n", StringComparison.Ordinal);
        var headerText = split < 0 ? rest : rest[..split];
        var bodyText = split < 0 ? "" : rest[(split + 2)..];
        var headers = headerText.Split('\n')
            .Where(h => h.Contains(": "))
            .Select(h => h.Split(": ", 2))
            .ToDictionary(h => h[0], h => h[1].Trim());
        byte[]? body = null;
        if (!string.IsNullOrWhiteSpace(bodyText))
        {
            JsonNode.Parse(bodyText.Trim()); // the request body must be valid JSON after substitution
            body = Encoding.UTF8.GetBytes(bodyText.Trim());
        }
        return (line.Groups[1].Value, line.Groups[2].Value, headers, body);
    }

    private static string Substitute(string expression, IReadOnlyDictionary<string, string> variables, IReadOnlyDictionary<string, JsonNode?> responses)
    {
        if (expression == "$guid") return Guid.NewGuid().ToString();
        if (variables.TryGetValue(expression, out var variable)) return variable;
        var parts = expression.Split(".response.body.$.", 2);
        Assert.True(parts.Length == 2 && responses.ContainsKey(parts[0]), $"Unresolved reference {{{{{expression}}}}}");
        var value = responses[parts[0]];
        foreach (Match segment in PathRegex().Matches(parts[1]))
            value = segment.Groups[2].Success ? value![int.Parse(segment.Groups[2].Value)] : value![segment.Groups[1].Value];
        return value switch
        {
            null => "null",
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => value.ToJsonString(),
        };
    }

    [GeneratedRegex(@"^@(\w+)\s*=\s*(.*)$", RegexOptions.Multiline)]
    private static partial Regex VariableRegex();

    [GeneratedRegex(@"^###.*$", RegexOptions.Multiline)]
    private static partial Regex SectionRegex();

    [GeneratedRegex(@"^# @name (\w+)\r?$", RegexOptions.Multiline)]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^# @expect ([\d ]+)\r?$", RegexOptions.Multiline)]
    private static partial Regex ExpectRegex();

    [GeneratedRegex(@"\{\{(.*?)\}\}")]
    private static partial Regex ReferenceRegex();

    [GeneratedRegex(@"^(GET|POST|PUT|DELETE) (\S+)\n(.*)", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex RequestLineRegex();

    [GeneratedRegex(@"(\w+)|\[(\d+)\]")]
    private static partial Regex PathRegex();
}

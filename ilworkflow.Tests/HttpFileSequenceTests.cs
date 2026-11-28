using ilworkflow.Tests.Infrastructure;
using static ilworkflow.Tests.Infrastructure.LiveApi;

namespace ilworkflow.Tests;

/// <summary>
/// Executes the actual MAIN SEQUENCE of the repository's .http files without logging tokens
/// (ports of Tests/VerifyConfiguredRequests.py and Tests/VerifyHttpSequence.py).
/// Requires accounts provisioned by Authentication.http or <see cref="ConfiguredWorkflowTests"/>.
/// </summary>
public sealed class HttpFileSequenceTests(ITestOutputHelper output) : IDisposable
{
    private static readonly string[] TaskLists = ["aTasks", "bTasks", "cTasks", "dTasks", "observedTasks", "observedDanTask"];
    private static readonly string[] FinishedNames = ["finishedInstance", "observedFinished"];

    private readonly LiveApi api = new(TimeSpan.FromSeconds(30));
    private readonly Dictionary<string, JsonNode?> responses = new();
    private readonly Dictionary<string, string> variables = new();

    public void Dispose() => api.Dispose();

    [Fact]
    public async Task ConfigurationAndTicketFiles_MainSequence()
    {
        LiveSettings.Require();
        await api.LoginAsync("admin", "admin", "password");
        var users = (await api.GetAsync("/elsa/api/identity/users"))["users"]!.AsArray();
        foreach (var name in new[] { "alice", "amy", "bob", "clara", "dan" })
            variables[name + "Id"] = users.First(u => (string)u!["name"]! == "configured-" + name)!["id"]!.ToString();
        var count = await RunAsync(["Workflow-Configuration.http", "Ticket-Workflow.http"]);
        output.WriteLine($"PASS {count} actual HTTP-file requests; named response IDs/revisions and status filters verified");
    }

    [Fact]
    public async Task ObservedTasksFile_MainSequence_Pagination()
    {
        LiveSettings.Require();
        await api.LoginAsync("admin", "admin", "password");
        var count = await RunAsync(["Observed-Tasks.http"]);
        output.WriteLine($"PASS {count} actual HTTP-file requests; named response IDs/revisions and status filters verified");

        string Actor(string actor) => actor == "admin" ? "admin" : Register(actor);
        var execute = "/elsa/api/workflow-definitions/" + variables["definitionId"] + "/execute";
        var payload = new { input = new { attributes = new { claimAmount = 6000 } } };
        await api.PostAsync(execute, payload); // a second ticket so there are two observed tasks to page through
        const string observedPath = "/elsa/api/user-tasks?scope=observed";
        var first = await api.GetAsync(observedPath + "&limit=1&includeTotalCount=true", Actor("dan"));
        var second = await api.GetAsync(observedPath + "&limit=1&cursor=" + Uri.EscapeDataString((string)first["nextCursor"]!), Actor("dan"));
        Assert.True((int)first["totalCount"]! >= 2);
        Assert.NotEqual((string)first["items"]![0]!["id"]!, (string)second["items"]![0]!["id"]!);

        output.WriteLine("PASS observed pagination");
    }

    /// <summary>
    /// Generic runner: set <c>ILWORKFLOW_HTTP_COLLECTION</c> to a .http file (absolute or repository-relative)
    /// whose main sequence drives a ticket from start to a Completed status.
    /// </summary>
    [Fact]
    public async Task HttpCollection_MainSequence()
    {
        LiveSettings.Require();
        var collection = Environment.GetEnvironmentVariable("ILWORKFLOW_HTTP_COLLECTION");
        if (string.IsNullOrWhiteSpace(collection)) Assert.Skip("Set ILWORKFLOW_HTTP_COLLECTION to a .http file to run it.");
        var path = Path.IsPathRooted(collection) ? collection : Path.Combine(LiveSettings.RepoRoot, collection);
        var file = new HttpFile(path);
        foreach (var (key, value) in file.Variables) variables[key] = value;
        variables["baseUrl"] = api.BaseUrl;
        var count = 0;
        foreach (var request in file.Requests)
        {
            var (method, url, headers, body) = HttpFile.Expand(request, variables, responses);
            JsonNode? response = null;
            var status = 0;
            var ready = false;
            for (var attempt = 0; attempt < 40 && !ready; attempt++)
            {
                (status, response) = await RawAsync(method, url, headers, body, [200, 201, 202]);
                ready = !(new[] { "aTasks", "bTasks", "cTasks", "dTasks" }.Contains(request.Name) && (response?["items"]?.AsArray().Count ?? 0) == 0)
                    && !(request.Name == "finishedInstance" && (string?)response?["status"] != "Finished");
                if (!ready) await Task.Delay(150, TestContext.Current.CancellationToken);
            }
            Assert.True(ready, $"{request.Name}: asynchronous state did not become ready");
            if (request.Name.EndsWith("Login")) Assert.False(string.IsNullOrEmpty((string?)response?["accessToken"]), $"{request.Name}: no access token");
            if (request.Name == "startClaim") Assert.Equal("Suspended", (string)response!["workflowState"]!["subStatus"]!);
            if (request.Name == "finishedInstance") Assert.Equal("Completed", (string)response!["workflowState"]!["output"]!["ticketStatus"]!);
            responses[request.Name] = response;
            count++;
            output.WriteLine($"PASS {request.Name}: HTTP {status}");
        }
        output.WriteLine($"PASS: {count} actual HTTP requests; A-B-C-D completed; tokens not logged");
    }

    private async Task<int> RunAsync(string[] files)
    {
        var count = 0;
        foreach (var filename in files)
        {
            var file = new HttpFile(Path.Combine(LiveSettings.RepoRoot, filename));
            foreach (var (key, value) in file.Variables) variables[key] = value;
            variables["baseUrl"] = api.BaseUrl;
            if (filename is "Workflow-Configuration.http" or "Observed-Tasks.http") variables["definitionId"] = "example-" + Hex();
            if (filename == "Ticket-Workflow.http") variables["definitionId"] = (string)responses["publishConfiguration"]!["data"]!["workflow_definition_id"]!;

            foreach (var request in file.Requests)
            {
                var name = request.Name;
                var (method, url, headers, body) = HttpFile.Expand(request, variables, responses);
                JsonNode? result = null;
                var status = 0;
                var ready = false;
                for (var attempt = 0; attempt < 60 && !ready; attempt++)
                {
                    (status, result) = await RawAsync(method, url, headers, body, request.Expected);
                    ready = !(TaskLists.Contains(name) && result!["items"]!.AsArray().Count == 0)
                        && !(FinishedNames.Contains(name) && (string)result!["status"]! != "Finished");
                    if (!ready) await Task.Delay(100, TestContext.Current.CancellationToken);
                }
                Assert.True(ready, name + ": workflow did not become ready");
                responses[name] = result;
                count++;

                if (FinishedNames.Contains(name)) Assert.Equal("Approved", (string)result!["workflowState"]!["output"]!["ticketOutcome"]!);
                if (name == "observedTasks")
                {
                    Assert.Equal(1, (int)result!["totalCount"]!);
                    Assert.Equal("C - Clara review", (string)result["items"]![0]!["title"]!);
                    Assert.Empty(result["items"]![0]!["allowedActions"]!.AsArray());
                }
                if (name is "observerAssigned" or "observerAvailable" or "outsiderObserved") Assert.Empty(result!["items"]!.AsArray());
                if (name == "outsiderObserved") Assert.Equal(0, (int)result!["totalCount"]!);
                if (name == "observedDetail")
                {
                    Assert.Equal(6000m, (decimal)result!["data"]!["claimAmount"]!);
                    Assert.True((bool)result["disclosure"]!["canViewHistory"]!);
                    Assert.Empty(result["allowedActions"]!.AsArray());
                }
                if (name == "observedEvents")
                    Assert.True(result!["items"]!.AsArray().Any(e => (string)e!["kind"]! == "Created"), result.ToJsonString());
                output.WriteLine($"PASS {filename}: {name} HTTP {status}");
            }
        }
        return count;
    }

    /// <summary>Registers the token from a named <c>{actor}Login</c> response so it can be used with <see cref="LiveApi"/>.</summary>
    private string Register(string actor)
    {
        api.Tokens[actor] = (string)responses[actor + "Login"]!["accessToken"]!;
        return actor;
    }

    private static readonly HttpClient Raw = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static async Task<(int Status, JsonNode? Body)> RawAsync(string method, string url, Dictionary<string, string> headers, byte[]? body, int[] expected)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (body is not null) request.Content = new ByteArrayContent(body);
        foreach (var (key, value) in headers)
            if (!request.Headers.TryAddWithoutValidation(key, value)) request.Content?.Headers.TryAddWithoutValidation(key, value);
        using var response = await Raw.SendAsync(request, TestContext.Current.CancellationToken);
        var status = (int)response.StatusCode;
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(expected.Contains(status), $"{method} {url}: HTTP {status}: {Truncate(raw, 500)}");
        // Error responses with an expected status are not parsed, matching the original runner.
        return (status, status >= 400 || string.IsNullOrWhiteSpace(raw) ? null : JsonNode.Parse(raw));
    }
}

using ilworkflow.Tests.Infrastructure;
using static ilworkflow.Tests.Infrastructure.LiveApi;

namespace ilworkflow.Tests;

/// <summary>
/// Live PostgreSQL parallel-approval checks (port of Tests/ParallelWorkflows.py).
/// Run again with <c>ILWORKFLOW_VERIFY_RESTART=1</c> after restarting the same API.
/// </summary>
public sealed class ParallelWorkflowTests(ITestOutputHelper output) : IDisposable
{
    private static readonly string StatePath = LiveSettings.StateFile("parallel-workflow-check.json");
    private readonly LiveApi api = new();
    private readonly Dictionary<string, string> ids = new();

    public void Dispose() => api.Dispose();

    [Fact]
    public async Task Restart_PreservesRejectionAndObserverAccess()
    {
        LiveSettings.Require(restart: true);
        Assert.True(File.Exists(StatePath), $"Run the main phase first; {StatePath} is missing.");
        await PrepareAsync();
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(StatePath, TestContext.Current.CancellationToken))!;
        var instance = (string)saved["instance"]!;
        var observed = await api.GetAsync($"/elsa/api/user-tasks?scope=observed&workflowInstanceId={instance}", "D");
        Assert.Equal([(string)saved["C"]!["id"]!], Ids(observed["items"]!));
        var detail = await api.GetAsync("/elsa/api/user-tasks/" + saved["C"]!["id"], "D");
        Assert.Equal(6000m, (decimal)detail["data"]!["claimAmount"]!);
        Assert.Empty(detail["allowedActions"]!.AsArray());
        await SendAsync(saved["D"]!, "D", "REJECT");
        await CheckFinishedAsync(instance, "Rejected");
        output.WriteLine("PASS restart preserves C rejection and observer access while D can resolve the group");
    }

    [Fact]
    public async Task Main_ParallelGroupValidation_Observers_Ordering_Rejections_Concurrency_Correction()
    {
        LiveSettings.Require();
        await PrepareAsync();

        var mutations = new Action<JsonObject, JsonArray>[]
        {
            (setup, _) => setup["startStage"] = "D",
            (_, stages) => stages[0]!["approveTo"] = Dest("D"),
            (_, stages) => stages[1]!["parallelWith"] = new JsonArray("UNKNOWN"),
            (_, stages) => stages[1]!["parallelWith"] = new JsonArray("C"),
            (_, stages) => stages[1]!["parallelWith"] = new JsonArray("D", "D"),
            (_, stages) => stages[2]!["parallelWith"] = new JsonArray("E"),
            (_, stages) => stages[3]!["parallelWith"] = new JsonArray("D"),
            (_, stages) => stages[2]!["approveTo"] = new JsonObject { ["kind"] = "Approved" },
            (_, stages) => stages[1]!["viewableByStages"] = new JsonArray("UNKNOWN"),
            (_, stages) => stages[1]!["viewableByStages"] = new JsonArray("C"),
            (_, stages) => stages[1]!["viewableByStages"] = new JsonArray("D", "D"),
            (_, stages) => stages[2]!["approveRules"] = new JsonArray(new JsonObject
            {
                ["conditions"] = stages[2]!["assignment"]!["rules"]![0]!["conditions"]!.DeepClone(),
                ["destination"] = new JsonObject { ["kind"] = "Approved" },
            }),
        };
        foreach (var mutate in mutations) await ConfigAsync(invalid: mutate);
        output.WriteLine("PASS invalid parallel membership, direct entry, conflicting routes and viewer stages");

        var definition = (await ConfigAsync())!;
        var (instance, c, d) = await StartAsync(definition);
        var observed = await api.GetAsync($"/elsa/api/user-tasks?scope=observed&workflowInstanceId={instance}", "D");
        Assert.Equal([(string)c["id"]!], Ids(observed["items"]!));
        Assert.Empty(observed["items"]![0]!["allowedActions"]!.AsArray());
        var own = await api.GetAsync($"/elsa/api/user-tasks?scope=assigned&workflowInstanceId={instance}", "D");
        Assert.Equal([(string)d["id"]!], Ids(own["items"]!));
        await SendAsync(c, "D", "APPROVE", expected: [403, 404]);
        await SendAsync(d, "D", "APPROVE");
        await CheckAdvanceAsync(instance, d, c);
        output.WriteLine("PASS D observes parallel C while retaining authority over its own D task");

        foreach (var actor in new[] { "C", "D" })
        {
            (instance, c, d) = await StartAsync(definition);
            var (winner, loser, other) = actor == "C" ? (c, d, "D") : (d, c, "C");
            var (_, body) = await SendAsync(winner, actor, "APPROVE");
            await SendAsync(winner, actor, "APPROVE", body);
            await SendAsync(loser, other, "APPROVE", expected: [403, 404, 409, 422]);
            await CheckAdvanceAsync(instance, winner, loser);
        }
        output.WriteLine("PASS both approval orders, cancellation, operation retry and late completion");

        (instance, c, d) = await StartAsync(definition);
        await SendAsync(c, "C", "REJECT");
        Assert.Equal((string)d["id"]!, (string)(await ActiveAsync(instance, "D"))["id"]!);
        Assert.DoesNotContain(await TasksAsync(instance), t => (string)t!["title"]! == "E");
        await SendAsync(d, "D", "APPROVE");
        await CheckAdvanceAsync(instance, d, c, "Completed");
        (instance, c, d) = await StartAsync(definition);
        await SendAsync(c, "C", "REJECT");
        await SendAsync(d, "D", "REJECT");
        await CheckFinishedAsync(instance, "Rejected");
        output.WriteLine("PASS first rejection keeps group open, later approval wins, two rejections reject");

        (instance, c, d) = await StartAsync(definition);
        int[] any = [200, 202, 403, 404, 409, 422];
        var statuses = (await Task.WhenAll(SendAsync(c, "C", "APPROVE", expected: any), SendAsync(d, "D", "APPROVE", expected: any)))
            .Select(r => r.Status).ToArray();
        Assert.True(statuses.Count(s => s is 200 or 202) == 1, string.Join(",", statuses));
        var (first, second) = statuses[0] is 200 or 202 ? (c, d) : (d, c);
        await CheckAdvanceAsync(instance, first, second);
        output.WriteLine("PASS simultaneous approvals produce exactly one continuation");

        (instance, c, d) = await StartAsync((await ConfigAsync(correction: true))!);
        await SendAsync(c, "C", "REJECT");
        await SendAsync(d, "D", "REJECT");
        var correction = await ActiveAsync(instance, "Correct and resubmit: C");
        await api.SendAsync("GET", "/elsa/api/user-tasks/" + correction["id"], null, "D", 404);
        await SendAsync(correction, "admin", "RESUBMIT", new JsonObject
        {
            ["actionKey"] = "RESUBMIT", ["expectedRevision"] = correction["revision"]!.DeepClone(), ["operationId"] = NewId(),
            ["data"] = Node(new { attributes = new { claimAmount = 4000 } }),
        });
        var newC = await ActiveAsync(instance, "C");
        var newD = await ActiveAsync(instance, "D");
        Assert.NotEqual((string)c["id"]!, (string)newC["id"]!);
        Assert.NotEqual((string)d["id"]!, (string)newD["id"]!);
        await SendAsync(newD, "D", "APPROVE");
        await CheckAdvanceAsync(instance, newD, newC);
        output.WriteLine("PASS correction creates a new parallel visit");

        (instance, c, d) = await StartAsync(definition);
        await SendAsync(c, "C", "REJECT");
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await File.WriteAllTextAsync(StatePath,
            new JsonObject { ["instance"] = instance, ["C"] = c.DeepClone(), ["D"] = d.DeepClone() }.ToJsonString(),
            TestContext.Current.CancellationToken);
        output.WriteLine("PASS saved pending D for ILWORKFLOW_VERIFY_RESTART");
    }

    private async Task PrepareAsync()
    {
        await api.LoginAsync("admin", "admin", "password");
        var roles = (await api.GetAsync("/elsa/api/identity/roles"))["roles"]!.AsArray();
        if (!roles.Any(r => (string)r!["id"]! == "Approver"))
            await api.PostAsync("/elsa/api/identity/roles", new
            {
                id = "Approver", name = "Approver",
                permissions = new[] { "user-tasks:view", "user-tasks:claim", "user-tasks:complete" },
            });
        var users = (await api.GetAsync("/elsa/api/identity/users"))["users"]!.AsArray();
        foreach (var key in new[] { "B", "C", "D", "E" })
        {
            var name = "parallel-check-" + key.ToLowerInvariant();
            var user = users.FirstOrDefault(u => (string)u!["name"]! == name)
                ?? await api.PostAsync("/elsa/api/identity/users", new { name, password = "Password123!", roles = new[] { "Approver" } });
            ids[key] = user!["id"]!.ToString();
            await api.LoginAsync(key, name, "Password123!");
        }
    }

    private static string[] Ids(JsonNode items) => items.AsArray().Select(t => (string)t!["id"]!).ToArray();

    private async Task<JsonArray> TasksAsync(string instance)
        => (await api.GetAsync($"/elsa/api/user-tasks?scope=all&workflowInstanceId={instance}"))["items"]!.AsArray();

    private async Task<JsonNode> ActiveAsync(string instance, string title)
    {
        static bool Match(JsonNode? t, string title) => (string)t!["title"]! == title && (string)t["status"]! == "Assigned";
        var found = await WaitForAsync(() => TasksAsync(instance), items => items.Any(t => Match(t, title)));
        return found.First(t => Match(t, title))!.DeepClone();
    }

    private async Task<(int Status, JsonNode Body)> SendAsync(JsonNode task, string actor, string action, JsonNode? body = null, int[]? expected = null)
    {
        body ??= new JsonObject
        {
            ["actionKey"] = action, ["expectedRevision"] = task["revision"]!.DeepClone(), ["operationId"] = NewId(),
            ["data"] = action == "REJECT" ? new JsonObject { ["reason"] = "Parallel test rejection" } : new JsonObject(),
        };
        var (status, _) = await api.SendAsync("POST", "/elsa/api/user-tasks/" + task["id"] + "/complete", body, actor, expected ?? [200, 202]);
        return (status, body);
    }

    private async Task CheckFinishedAsync(string instance, string outcome)
    {
        var state = await WaitForAsync(() => api.GetAsync("/elsa/api/workflow-instances/" + instance), r => (string)r["status"]! == "Finished");
        Assert.True((string?)state["workflowState"]!["output"]!["ticketOutcome"] == outcome, (string?)state["subStatus"]);
    }

    private async Task CheckAdvanceAsync(string instance, JsonNode winner, JsonNode loser, string loserStatus = "Cancelled")
    {
        var finalTask = await ActiveAsync(instance, "E");
        var result = await TasksAsync(instance);
        Assert.Single(result, t => (string)t!["title"]! == "E");
        var (winnerId, loserId) = ((string)winner["id"]!, (string)loser["id"]!);
        var statuses = result.Where(t => (string)t!["id"]! == winnerId || (string)t!["id"]! == loserId)
            .ToDictionary(t => (string)t!["id"]!, t => (string)t!["status"]!);
        Assert.Equal(new Dictionary<string, string> { [winnerId] = "Completed", [loserId] = loserStatus }, statuses);
        await SendAsync(finalTask, "E", "APPROVE");
        await CheckFinishedAsync(instance, "Approved");
    }

    private static JsonObject Dest(string key) => new() { ["kind"] = "Stage", ["stageKey"] = key };

    private JsonObject Stage(string key, JsonObject approve, JsonObject reject) => new()
    {
        ["key"] = key, ["title"] = key, ["approveTo"] = approve, ["rejectTo"] = reject,
        ["assignment"] = new JsonObject { ["policy"] = "Specific", ["members"] = new JsonArray(ids[key]), ["userId"] = ids[key] },
    };

    private async Task<string?> ConfigAsync(bool correction = false, Action<JsonObject, JsonArray>? invalid = null)
    {
        var definition = "parallel-" + Hex();
        JsonObject Reject() => correction ? new JsonObject { ["kind"] = "Correction", ["stageKey"] = "C" } : new JsonObject { ["kind"] = "Rejected" };
        var stages = new JsonArray(
            Stage("B", Dest("C"), new JsonObject { ["kind"] = "Rejected" }),
            Stage("C", Dest("E"), Reject()),
            Stage("D", Dest("E"), Reject()),
            Stage("E", new JsonObject { ["kind"] = "Approved" }, new JsonObject { ["kind"] = "Rejected" }));
        stages[1]!["parallelWith"] = new JsonArray("D");
        stages[1]!["viewableByStages"] = new JsonArray("D");
        stages[2]!["assignment"] = new JsonObject
        {
            ["policy"] = "Condition", ["members"] = new JsonArray(ids["D"]),
            ["rules"] = new JsonArray(new JsonObject
            {
                ["conditions"] = new JsonArray(new JsonObject { ["attribute"] = "claimAmount", ["operator"] = "GreaterThan", ["value"] = 0 }),
                ["userId"] = ids["D"],
            }),
        };
        await api.PostAsync("/api/workflow-attributes", new { definitionId = definition, attribute = new { key = "claimAmount", type = "Decimal" } });
        var setup = new JsonObject { ["name"] = "Parallel approval check", ["startStage"] = "B", ["stages"] = stages };
        invalid?.Invoke(setup, stages);
        var saved = await api.PostAsync("/api/workflow-configurations",
            new JsonObject { ["workflowDefinitionId"] = definition, ["configuration"] = setup }, "admin", invalid is null ? 200 : 400);
        if (invalid is not null) return null;
        Assert.Equal(["claimAmount"], saved["configuration"]!["attributes"]!.AsArray().Select(f => (string)f!["key"]!).ToArray());
        await api.PostAsync($"/api/workflow-configurations/{saved["workflowDefinitionId"]}/versions/{saved["workflowVersionId"]}/publish", null);
        return definition;
    }

    private async Task<(string Instance, JsonNode C, JsonNode D)> StartAsync(string definition)
    {
        var response = await api.PostAsync($"/elsa/api/workflow-definitions/{definition}/execute",
            new { input = new { attributes = new { claimAmount = 6000 } } });
        var instance = (string)response["workflowState"]!["id"]!;
        await SendAsync(await ActiveAsync(instance, "B"), "B", "APPROVE");
        return (instance, await ActiveAsync(instance, "C"), await ActiveAsync(instance, "D"));
    }
}

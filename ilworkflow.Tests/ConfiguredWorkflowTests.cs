using System.Text.Json;
using ilworkflow.Tests.Infrastructure;
using static ilworkflow.Tests.Infrastructure.LiveApi;

namespace ilworkflow.Tests;

/// <summary>
/// Live API acceptance checks (port of Tests/ConfiguredWorkflows.py). Creates research accounts/configurations.
/// Run the main phase, restart the server, then rerun with <c>ILWORKFLOW_VERIFY_RESTART=1</c>.
/// </summary>
public sealed class ConfiguredWorkflowTests(ITestOutputHelper output) : IDisposable
{
    private static readonly string StatePath = LiveSettings.StateFile("configured-workflow-check.json");
    private readonly LiveApi api = new(TimeSpan.FromSeconds(30));
    private Dictionary<string, string> ids = new();

    public void Dispose() => api.Dispose();

    [Fact]
    public async Task Preview_SwaggerLoads_AndMutationsReturn503()
    {
        LiveSettings.Require(preview: true);
        await api.LoginAsync("admin", "admin", "password");
        var document = await api.GetAsync("/swagger/v3/swagger.json", null);
        Assert.Contains(document["paths"]!.AsObject(), p => p.Key.Contains("workflow-configurations"));
        await api.SendAsync("POST", "/api/workflow-configurations/get", new { workflowDefinitionId = "anything" }, "admin", 503);
        await api.SendAsync("POST", "/elsa/api/workflow-definitions/anything/execute", new { }, "admin", 503);
        await api.SendAsync("POST", "/elsa/api/user-tasks/anything/complete", new { }, "admin", 503);
        output.WriteLine("PASS documentation preview: Swagger loads and configuration/execution/task mutations return 503");
    }

    [Fact]
    public async Task Restart_PreservesUsersConfigurationPendingTaskAndCorrections()
    {
        LiveSettings.Require(restart: true);
        Assert.True(File.Exists(StatePath), $"Run the main phase first; {StatePath} is missing.");
        await PrepareAsync();
        var saved = JsonNode.Parse(await File.ReadAllTextAsync(StatePath, TestContext.Current.CancellationToken))!;
        var savedIds = saved["userIds"]!.AsObject().ToDictionary(x => x.Key, x => (string)x.Value!);
        Assert.True(savedIds.OrderBy(x => x.Key).SequenceEqual(ids.OrderBy(x => x.Key)), "Native user IDs did not survive restart");
        var config = await api.PostAsync("/api/workflow-configurations/get", new JsonObject { ["workflowDefinitionId"] = saved["workflowDefinitionId"]!.DeepClone(), ["workflowVersion"] = saved["workflowVersion"]!.DeepClone() });
        Assert.Equal(saved["workflowVersion"]!.ToString(), config["workflowVersion"]!.ToString());
        Assert.True((bool)config["isPublished"]!);
        var pending = (string)saved["pendingInstance"]!;
        Assert.Equal((string)saved["pendingTask"]!, (string)(await TaskAsync(pending, "alice"))["id"]!);
        foreach (var user in new[] { "alice", "bob", "clara", "dan" }) await DecideAsync(pending, user);
        await FinishedAsync(pending, "Approved");
        var corrected = await FinishedAsync((string)saved["correctedInstance"]!, "Approved");
        Assert.Equal(2, (int)corrected["workflowState"]!["output"]!["attributeRevision"]!);
        output.WriteLine("PASS restart: native user IDs/login, configuration, definition, pending task, callback resumption, corrected attributes and final outcome");
    }

    [Fact]
    public async Task Main_ConfigurationVersioning_Routing_Corrections_Concurrency_AccessAndValidation()
    {
        LiveSettings.Require();
        await PrepareAsync();

        var config = await CreateAsync();
        var firstRevision = (long)config["revision"]!;
        config = await SaveAsync(config["workflowDefinitionId"]!, config["workflowVersion"], config["configuration"]!);
        Assert.Equal(firstRevision + 1, (long)config["revision"]!);
        await SaveAsync(config["workflowDefinitionId"]!, JsonValue.Create("missing-version"), config["configuration"]!, 404);
        config = await PublishAsync(config);
        await SaveAsync(config["workflowDefinitionId"]!, config["workflowVersion"], config["configuration"]!, 409);
        output.WriteLine("PASS POST create/update by version, revision appends, unknown version and immutable published configuration");

        foreach (var amount in new[] { 4000, 5000, 6000 })
        {
            var run = await StartAsync(config, amount);
            await DecideAsync(run, "alice", replay: amount == 6000);
            await DecideAsync(run, "bob");
            if (amount > 5000) await DecideAsync(run, "clara");
            await DecideAsync(run, "dan");
            await FinishedAsync(run, "Approved");
            output.WriteLine($"PASS {amount}: conditional approvals and final outcome");
        }

        var instance = await StartAsync(config);
        await DecideAsync(instance, "alice");
        await DecideAsync(instance, "bob", "Reject", new { reason = "Return to A" });
        await DecideAsync(instance, "alice");
        await DecideAsync(instance, "bob");
        await DecideAsync(instance, "clara", "Reject", new { reason = "Terminal rejection" });
        await FinishedAsync(instance, "Rejected");
        output.WriteLine("PASS reject B to A, then terminal rejection C");

        instance = await StartAsync(config);
        await DecideAsync(instance, "alice", "Reject", new { reason = "Correct the amount" });
        await DecideAsync(instance, "requester", "Resubmit", new { attributes = new { claimAmount = 4000 } });
        await DecideAsync(instance, "alice");
        await DecideAsync(instance, "bob");
        await DecideAsync(instance, "dan");
        await FinishedAsync(instance, "Approved");
        var corrected = instance;
        output.WriteLine("PASS correction A, revised attributes, same instance, C bypassed");

        instance = await StartAsync(config, 4000);
        await DecideAsync(instance, "alice");
        await DecideAsync(instance, "bob");
        await DecideAsync(instance, "dan", "Reject", new { reason = "Correct final details" });
        await DecideAsync(instance, "requester", "Resubmit", new { attributes = new { claimAmount = 6000 } });
        await DecideAsync(instance, "dan");
        await FinishedAsync(instance, "Approved");
        output.WriteLine("PASS correction D resumes configured D, without replaying B/C");

        instance = await StartAsync(config);
        var t = await TaskAsync(instance, "alice");
        async Task<bool> Claim(string who)
        {
            // Once another candidate claims, Elsa may hide the task from the loser with 404.
            var (status, _) = await api.SendAsync("POST", $"/elsa/api/user-tasks/{t["id"]}/claim",
                new JsonObject { ["expectedRevision"] = t["revision"]!.DeepClone(), ["operationId"] = NewId() }, who, 200, 202, 403, 404, 409);
            return status is 200 or 202;
        }
        var results = await Task.WhenAll(Claim("alice"), Claim("amy"));
        Assert.Equal(1, results.Count(x => x));
        var winner = results[0] ? "alice" : "amy";
        var detail = await TaskAsync(instance, winner);
        await api.PostAsync($"/elsa/api/user-tasks/{t["id"]}/complete",
            new JsonObject { ["expectedRevision"] = detail["revision"]!.DeepClone(), ["operationId"] = NewId(), ["actionKey"] = "REJECT", ["data"] = new JsonObject() }, winner, 400, 422);
        var definitionId = (string)config["workflowDefinitionId"]!;
        await api.SendAsync("GET", "/elsa/api/workflow-instances/" + instance, null, "outsider", 404);
        await api.SendAsync("GET", "/elsa/api/workflow-instances", null, "requester", 403);
        await api.SendAsync("POST", "/api/workflow-configurations", new JsonObject { ["configuration"] = Fixture() }, "requester", 403);
        await api.SendAsync("POST", $"/elsa/api/workflow-definitions/{definitionId}/dispatch", new { input = new { } }, "requester", 403);
        await api.SendAsync("POST", $"/elsa/api/workflow-definitions/{definitionId}/execute", new { input = new { requesterId = ids["outsider"], attributes = new { } } }, "requester", 403);
        await api.SendAsync("POST", $"/elsa/api/workflow-definitions/{definitionId}/execute", new { input = new { attributes = new { claimAmount = "wrong" } } }, "requester", 400);
        await api.SendAsync("POST", $"/elsa/api/workflow-definitions/{definitionId}/execute", """{"input":{},"input":{}}"""u8.ToArray(), "requester", 400);
        await api.SendAsync("GET", "/elsa/api/user-tasks", null, null, 401);
        output.WriteLine("PASS concurrent claims, required reason, authentication, configuration permission, scoped reads and execution guards");

        foreach (var mutate in new[] { "duplicate", "cycle", "operator", "member" })
        {
            var setup = Fixture();
            var stages = setup["stages"]!.AsArray();
            switch (mutate)
            {
                case "duplicate": stages.Add(stages[0]!.DeepClone()); break;
                case "cycle": stages[^1]!["approveTo"] = new JsonObject { ["kind"] = "Stage", ["stageKey"] = "A" }; break;
                case "operator": stages[1]!["approveRules"]![0]!["conditions"]![0]!["attribute"] = "firstName"; break;
                case "member": stages[0]!["assignment"]!["members"] = new JsonArray(ids["requester"]); break;
            }
            await CreateAsync(setup, 400);
        }
        output.WriteLine("PASS FluentValidation duplicate keys, cycles, type/operator mismatch and ineligible member");

        // A stage entered by rejection can still contain an invalid approval cycle. Validate the whole graph.
        {
            var setup = Fixture();
            var stages = setup["stages"]!;
            stages[0]!["approveTo"] = new JsonObject { ["kind"] = "Approved" };
            stages[0]!["rejectTo"] = new JsonObject { ["kind"] = "Stage", ["stageKey"] = "B" };
            stages[1]!["approveRules"] = new JsonArray();
            stages[1]!["approveTo"] = new JsonObject { ["kind"] = "Stage", ["stageKey"] = "C" };
            stages[2]!["approveTo"] = new JsonObject { ["kind"] = "Stage", ["stageKey"] = "B" };
            await CreateAsync(setup, 400);
            output.WriteLine("PASS approval cycles rejected even when reached only through a rejection route");
        }

        // These cases previously diverged between C# assignment and JavaScript routing.
        // Int64 values must remain exact; absent attributes must not inherit Object.constructor.
        {
            var setup = Fixture();
            setup["attributes"]![2]!["type"] = "Integer";
            setup["stages"]![1]!["approveRules"]![0]!["conditions"]![0]!["value"] = 9007199254740992L;
            var precise = await PublishAsync(await CreateAsync(setup));
            instance = await StartAsync(precise, 9007199254740993L);
            foreach (var user in new[] { "alice", "bob", "clara", "dan" }) await DecideAsync(instance, user);
            await FinishedAsync(instance, "Approved");

            setup = Fixture();
            setup["attributes"]!.AsArray().Add(new JsonObject { ["key"] = "constructor", ["type"] = "String" });
            setup["stages"]![1]!["approveRules"]![0]!["conditions"] = new JsonArray(new JsonObject { ["attribute"] = "constructor", ["operator"] = "NotEquals", ["value"] = "blocked" });
            var missing = await PublishAsync(await CreateAsync(setup));
            await api.SendAsync("POST", $"/elsa/api/workflow-definitions/{missing["workflowDefinitionId"]}/execute",
                new { input = new { attributes = new { claimAmount = 6000 } } }, "requester", 400);
            output.WriteLine("PASS exact Int64 condition routing and required prototype-named attributes");
        }

        // No eligible candidate must suspend; a supervisor repairs assignment through the native API.
        {
            var blockedSetup = Fixture();
            blockedSetup["stages"]![0]!["assignment"]!["members"] = new JsonArray();
            var blockedConfig = await PublishAsync(await CreateAsync(blockedSetup));
            var blockedInstance = await StartAsync(blockedConfig);
            var blocked = (await api.GetAsync($"/elsa/api/user-tasks?scope=all&workflowInstanceId={blockedInstance}", "supervisor"))["items"]![0]!;
            Assert.Equal("Unassigned", (string)blocked["status"]!);
            await api.PostAsync($"/elsa/api/user-tasks/{blocked["id"]}/assign", new JsonObject
            {
                ["expectedRevision"] = blocked["revision"]!.DeepClone(), ["operationId"] = NewId(),
                ["assignee"] = new JsonObject { ["kind"] = "user", ["provider"] = "default", ["id"] = ids["alice"] },
            }, "supervisor");
            await DecideAsync(blockedInstance, "alice");
            Assert.StartsWith("B", (string)(await TaskAsync(blockedInstance, "bob"))["title"]!);
            output.WriteLine("PASS missing assignment never bypasses approval; supervisor repairs through native assign");
        }

        var pending = await StartAsync(config);
        var pendingTask = (string)(await TaskAsync(pending, "alice"))["id"]!;
        // No version: the same definition gets its next version.
        var nextConfig = await SaveAsync(config["workflowDefinitionId"]!, null, config["configuration"]!);
        Assert.Equal(config["workflowDefinitionId"]!.ToString(), nextConfig["workflowDefinitionId"]!.ToString());
                Assert.Equal((int)config["workflowVersion"]! + 1, (int)nextConfig["workflowVersion"]!);
        nextConfig["configuration"]!["name"] = "Expense Claim v2";
        nextConfig = await SaveAsync(nextConfig["workflowDefinitionId"]!, nextConfig["workflowVersion"], nextConfig["configuration"]!);
        await PublishAsync(nextConfig);
        Assert.Equal((int)config["workflowVersion"]!, (int)(await api.GetAsync("/elsa/api/workflow-instances/" + pending))["version"]!);
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        var state = new JsonObject
        {
            ["workflowDefinitionId"] = config["workflowDefinitionId"]!.DeepClone(), ["workflowVersion"] = config["workflowVersion"]!.DeepClone(),
            ["pendingInstance"] = pending, ["pendingTask"] = pendingTask, ["correctedInstance"] = corrected, ["userIds"] = Node(ids),
        };
        await File.WriteAllTextAsync(StatePath, state.ToJsonString(), TestContext.Current.CancellationToken);
        output.WriteLine("PASS next version published while pending instance remains pinned; restart fixture saved");
    }

    private async Task PrepareAsync()
    {
        await api.LoginAsync("admin", "admin", "password");
        var roles = (await api.GetAsync("/elsa/api/identity/roles"))["roles"]!.AsArray();
        var grants = new Dictionary<string, string[]>
        {
            ["Approver"] = ["user-tasks:view", "user-tasks:claim", "user-tasks:complete", "workflows/instances:view"],
            ["Requester"] = ["user-tasks:view", "user-tasks:complete", "workflows/definitions:view", "workflows/definitions:execute", "workflows/instances:view"],
            ["Configurator"] = ["workflows/definitions:*", "workflows/instances:view"],
            ["Supervisor"] = ["user-tasks:view", "user-tasks:assign", "user-tasks:supervise", "workflows/instances:view"],
        };
        foreach (var (role, permissions) in grants)
        {
            var exists = roles.Any(x => (string)x!["id"]! == role);
            await api.CallAsync(exists ? "PUT" : "POST", "/elsa/api/identity/roles" + (exists ? "/" + role : ""), new { id = role, name = role, permissions });
        }
        var existing = (await api.GetAsync("/elsa/api/identity/users"))["users"]!.AsArray();
        ids = new Dictionary<string, string>();
        foreach (var (name, role) in new[]
        {
            ("requester", "Requester"), ("alice", "Approver"), ("amy", "Approver"), ("bob", "Approver"), ("clara", "Approver"),
            ("dan", "Approver"), ("outsider", "Requester"), ("configurator", "Configurator"), ("supervisor", "Supervisor"),
        })
        {
            var user = existing.FirstOrDefault(x => (string)x!["name"]! == "configured-" + name)
                ?? await api.PostAsync("/elsa/api/identity/users", new { name = "configured-" + name, password = "Password123!", roles = new[] { role } });
            ids[name] = user!["id"]!.ToString();
            await api.LoginAsync(name, "configured-" + name, "Password123!");
        }
    }

    private JsonObject Fixture()
    {
        JsonObject Dest(string kind, string? key = null)
        {
            var d = new JsonObject { ["kind"] = kind };
            if (key is not null) d["stageKey"] = key;
            return d;
        }
        JsonObject Assign(string policy, string[] members, string? user = null, JsonArray? rules = null)
        {
            var a = new JsonObject { ["policy"] = policy, ["members"] = new JsonArray(members.Select(m => (JsonNode)ids[m]).ToArray()) };
            if (user is not null) a["userId"] = ids[user];
            if (rules is not null) a["rules"] = rules;
            return a;
        }
        JsonObject Condition() => new() { ["attribute"] = "claimAmount", ["operator"] = "GreaterThan", ["value"] = 5000 };
        JsonObject Stage(string key, string title, JsonObject assignment, JsonObject approveTo, JsonObject rejectTo)
            => new() { ["key"] = key, ["title"] = title, ["assignment"] = assignment, ["approveTo"] = approveTo, ["rejectTo"] = rejectTo };

        var b = Stage("B", "B Claims review", Assign("Bucket", ["bob"]), Dest("Stage", "D"), Dest("Stage", "A"));
        b["approveRules"] = new JsonArray(new JsonObject { ["conditions"] = new JsonArray(Condition()), ["destination"] = Dest("Stage", "C") });
        return new JsonObject
        {
            ["name"] = "Configured Expense Claim",
            ["attributes"] = Node(new object[]
            {
                new { key = "firstName", type = "String", required = true, maxLength = 100 },
                new { key = "lastName", type = "String", required = true },
                new { key = "claimAmount", type = "Decimal", required = true, minimum = 0 },
            }),
            ["startStage"] = "A",
            ["stages"] = new JsonArray(
                Stage("A", "A Initial review", Assign("Bucket", ["alice", "amy"]), Dest("Stage", "B"), Dest("Correction", "A")),
                b,
                Stage("C", "C High-value approval",
                    Assign("Condition", ["clara"], rules: new JsonArray(new JsonObject { ["conditions"] = new JsonArray(Condition()), ["userId"] = ids["clara"] })),
                    Dest("Stage", "D"), Dest("Rejected")),
                Stage("D", "D Final approval", Assign("Specific", ["dan"], "dan"), Dest("Approved"), Dest("Correction", "D"))),
        };
    }

    private async Task<JsonNode> CreateAsync(JsonObject? setup = null, int expected = 200)
    {
        setup = (JsonObject)(setup ?? Fixture()).DeepClone();
        var definition = "configured-" + Hex();
        var attributes = setup["attributes"]!.AsArray();
        setup.Remove("attributes");
        foreach (var field in attributes)
            await api.PostAsync("/api/workflow-attributes", new JsonObject { ["definitionId"] = definition, ["attribute"] = field!.DeepClone() }, "configurator");
        return (await api.SendAsync("POST", "/api/workflow-configurations",
            new JsonObject { ["workflowDefinitionId"] = definition, ["configuration"] = setup }, "configurator", expected)).Body!;
    }

    /// <summary>Definition ID + version number update that draft version. Without a version the definition gets its next version.</summary>
    private Task<JsonNode> SaveAsync(JsonNode definitionId, JsonNode? versionId, JsonNode configuration, params int[] expected)
    {
        var body = new JsonObject { ["workflowDefinitionId"] = definitionId.DeepClone(), ["configuration"] = configuration.DeepClone() };
        if (versionId is not null) body["workflowVersion"] = versionId.DeepClone();
        return api.PostAsync("/api/workflow-configurations", body, "configurator", expected);
    }

    private Task<JsonNode> PublishAsync(JsonNode config)
        => api.PostAsync($"/api/workflow-configurations/{config["workflowDefinitionId"]}/versions/{config["workflowVersion"]}/publish", null, "configurator");

    private async Task<string> StartAsync(JsonNode config, long amount = 6000)
    {
        var result = await api.PostAsync($"/elsa/api/workflow-definitions/{config["workflowDefinitionId"]}/execute",
            new { correlationId = "CHECK-" + NewId(), input = new { attributes = new { claimAmount = amount } } }, "requester");
        var state = result["workflowState"]!;
        Assert.True((string)state["subStatus"]! == "Suspended", Truncate(state["incidents"]?.ToJsonString(), 2000));
        return (string)state["id"]!;
    }

    private async Task<JsonNode> TaskAsync(string instance, string user)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            foreach (var scope in new[] { "assigned", "available" })
            {
                var result = await api.GetAsync($"/elsa/api/user-tasks?workflowInstanceId={instance}&scope={scope}&includeTotalCount=true", user);
                var item = result["items"]!.AsArray().FirstOrDefault(x => (string)x!["status"]! is "Available" or "Assigned" or "Unassigned");
                if (item is not null) return await api.GetAsync("/elsa/api/user-tasks/" + item["id"], user);
            }
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        var state = await api.GetAsync("/elsa/api/workflow-instances/" + instance);
        throw new Xunit.Sdk.XunitException("No task: " + Truncate(state.ToJsonString(), 1500));
    }

    private async Task DecideAsync(string instance, string user, string action = "Approve", object? data = null, bool replay = false)
    {
        var t = await TaskAsync(instance, user);
        if ((string)t["status"]! is "Available" or "Unassigned")
        {
            await api.PostAsync($"/elsa/api/user-tasks/{t["id"]}/claim", new JsonObject { ["expectedRevision"] = t["revision"]!.DeepClone(), ["operationId"] = NewId() }, user);
            t = await api.GetAsync("/elsa/api/user-tasks/" + t["id"], user);
        }
        var keys = t["actions"]!.AsArray().Select(x => (string)x!["key"]!).ToHashSet();
        var actionKey = keys.First(k => string.Equals(k, action, StringComparison.OrdinalIgnoreCase));
        if (!LiveSettings.VerifyRestart)
        {
            string[] expectedKeys = action == "Resubmit" ? ["RESUBMIT"] : ["APPROVE", "REJECT"];
            Assert.True(keys.SetEquals(expectedKeys), string.Join(",", keys));
        }
        var payload = new JsonObject
        {
            ["expectedRevision"] = t["revision"]!.DeepClone(), ["operationId"] = NewId(), ["actionKey"] = actionKey,
            ["data"] = Node(data ?? new { comment = "Reviewed" }),
        };
        await api.PostAsync($"/elsa/api/user-tasks/{t["id"]}/complete", payload, user);
        if (replay) await api.PostAsync($"/elsa/api/user-tasks/{t["id"]}/complete", payload, user);
    }

    private async Task<JsonNode> FinishedAsync(string instance, string outcome)
    {
        JsonNode result = null!;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            result = await api.GetAsync("/elsa/api/workflow-instances/" + instance);
            if ((string)result["status"]! == "Finished") break;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.True((string)result["subStatus"]! == "Finished", Truncate(result.ToJsonString(), 1000));
        Assert.Equal(outcome, (string)result["workflowState"]!["output"]!["ticketOutcome"]!);
        return result;
    }
}

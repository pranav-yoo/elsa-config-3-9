using ilworkflow.Tests.Infrastructure;
using static ilworkflow.Tests.Infrastructure.LiveApi;

namespace ilworkflow.Tests;

/// <summary>Live named-definition/field-master checks (port of Tests/AttributeCatalog.py). Creates research records.</summary>
public sealed class AttributeCatalogTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RequiredConditionalFields_InactiveMissingForeignRejection_ImmutableVersions_NativeApproval()
    {
        LiveSettings.Require();
        using var api = new LiveApi(TimeSpan.FromSeconds(30));
        await api.LoginAsync("admin", "admin", "password");
        // configured-alice is a local Approver created by Authentication.http or ConfiguredWorkflowTests.
        var users = (await api.GetAsync("/elsa/api/identity/users"))["users"]!.AsArray();
        var aliceId = users.First(u => (string)u!["name"]! == "configured-alice")!["id"]!.GetValue<string>();
        await api.LoginAsync("alice", "configured-alice", "Password123!");

        var definition = "catalog-" + Hex();
        Task Field(string key, string kind = "String", string status = "Active", string? family = null)
            => api.PostAsync("/api/workflow-attributes", new { definitionId = family ?? definition, attribute = new { key, type = kind }, status });
        await Field("firstName");
        await Field("claimAmount", "Decimal");
        await Field("department");
        await Field("obsolete", "Boolean", "Inactive");
        await Field("foreignField", family: definition + "-other");

        var setup = Node(new
        {
            name = "Catalog claim",
            startStage = "A",
            stages = new[]
            {
                new
                {
                    key = "A", title = "Alice approval",
                    assignment = new { policy = "Specific", members = new[] { aliceId }, userId = aliceId },
                    approveTo = new { kind = "Rejected" }, rejectTo = new { kind = "Rejected" },
                    approveRules = new[] { new { conditions = new[] { new { attribute = "claimAmount", @operator = "GreaterThan", value = 5000 } }, destination = new { kind = "Approved" } } },
                },
            },
        });
        var body = new JsonObject { ["workflowDefinitionId"] = definition, ["configuration"] = setup };
        var stage = setup["stages"]![0]!;
        var condition = stage["approveRules"]![0]!["conditions"]![0]!;
        foreach (var invalid in new[] { "obsolete", "foreignField", "unknown" })
        {
            condition["attribute"] = invalid;
            var failure = await api.PostAsync("/api/workflow-configurations", body, "admin", 400);
            Assert.Contains(failure["errors"]!.AsArray(), e => ((string)e!["message"]!).Contains(invalid));
        }
        condition["attribute"] = "claimAmount";

        var assignment = stage["assignment"]!;
        assignment["policy"] = "Condition";
        assignment["rules"] = Node(new[] { new { conditions = new[] { new { attribute = "department", @operator = "Equals", value = "Finance" } }, userId = aliceId } });
        var config = await api.PostAsync("/api/workflow-configurations", body);
        Assert.Equal(definition, (string)config["workflowDefinitionId"]!);
        Assert.Equal(
            new Dictionary<string, bool> { ["claimAmount"] = true, ["department"] = true },
            config["configuration"]!["attributes"]!.AsArray().ToDictionary(x => (string)x!["key"]!, x => (bool)x!["required"]!));
        await api.PostAsync("/api/workflow-configurations", body, "admin", 409);

        // Inline attributes are ignored; the catalog is authoritative.
        setup["attributes"] = Node(new[] { new { key = "injected", type = "String", required = true } });
        var updated = await api.PostAsync("/api/workflow-configurations",
            new JsonObject { ["workflowDefinitionId"] = definition, ["workflowVersion"] = config["workflowVersion"]!.DeepClone(), ["configuration"] = setup.DeepClone() });
        Assert.Equal(["claimAmount", "department"], Keys(updated));
        var versionId = (int)config["workflowVersion"]!;
        var published = await api.PostAsync($"/api/workflow-configurations/{definition}/versions/{versionId}/publish", null);

        foreach (var attributes in new object[] { new { claimAmount = 6000 }, new { firstName = "John", claimAmount = 6000, department = "Finance" } })
            await api.PostAsync($"/elsa/api/workflow-definitions/{definition}/execute", new { input = new { attributes } }, "admin", 400);
        var instance = (string)(await api.PostAsync($"/elsa/api/workflow-definitions/{definition}/execute",
            new { input = new { attributes = new { claimAmount = 6000, department = "Finance" } } }))["workflowState"]!["id"]!;
        Assert.Equal(definition, (string)(await api.GetAsync("/elsa/api/workflow-instances/" + instance))["definitionId"]!);

        // No version: the published definition gets its next version.
        var nextDraft = await api.PostAsync("/api/workflow-configurations", new JsonObject { ["workflowDefinitionId"] = definition, ["configuration"] = setup.DeepClone() });
        Assert.NotEqual(versionId, (int)nextDraft["workflowVersion"]!);
        Assert.Equal((int)published["workflowVersion"]! + 1, (int)nextDraft["workflowVersion"]!);
        Assert.Equal(["claimAmount", "department"], Keys(nextDraft));
        stage["approveRules"] = new JsonArray();
        stage["assignment"] = new JsonObject { ["policy"] = "Specific", ["members"] = assignment["members"]!.DeepClone(), ["userId"] = assignment["userId"]!.DeepClone() };
        var empty = await api.PostAsync("/api/workflow-configurations",
            new JsonObject { ["workflowDefinitionId"] = definition, ["workflowVersion"] = nextDraft["workflowVersion"]!.DeepClone(), ["configuration"] = setup.DeepClone() });
        Assert.Empty(empty["configuration"]!["attributes"]!.AsArray());
        Assert.Equal(2, (await api.GetAsync($"/api/workflow-configurations/{definition}/versions/{versionId}"))["configuration"]!["attributes"]!.AsArray().Count);

        await api.PostAsync("/api/workflow-attributes", new { definitionId = definition, attribute = new { key = "bad", type = "String" } }, "alice", 403);
        var task = (await api.GetAsync($"/elsa/api/user-tasks?scope=assigned&status=Assigned&workflowInstanceId={instance}", "alice"))["items"]![0]!;
        await api.PostAsync($"/elsa/api/user-tasks/{task["id"]}/complete",
            new JsonObject { ["expectedRevision"] = task["revision"]!.DeepClone(), ["operationId"] = NewId(), ["actionKey"] = "APPROVE", ["data"] = new JsonObject() }, "alice");
        var result = await WaitForAsync(() => api.GetAsync("/elsa/api/workflow-instances/" + instance), r => (string)r["status"]! == "Finished");
        Assert.Equal("Approved", (string)result["workflowState"]!["output"]!["ticketOutcome"]!);

        await api.PostAsync("/api/workflow-attributes", new { definitionId = (string?)null, attribute = new { key = "bad", type = "String" } }, "admin", 400);
        output.WriteLine("PASS required conditional fields, inactive/missing/foreign rejection, ignored inline schema, no-condition draft, immutable versions and native approval");
    }

    private static string[] Keys(JsonNode config)
        => config["configuration"]!["attributes"]!.AsArray().Select(x => (string)x!["key"]!).Order().ToArray();
}

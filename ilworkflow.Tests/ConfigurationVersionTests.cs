using ilworkflow.Tests.Infrastructure;
using static ilworkflow.Tests.Infrastructure.LiveApi;

namespace ilworkflow.Tests;

/// <summary>
/// Live checks for the configuration contract: ONE save endpoint keyed by workflow_definition_id / workflow_version_id.
/// <list type="bullet">
/// <item>No IDs: new definition with a generated ID and its first version.</item>
/// <item>workflow_definition_id only: that definition, or the next version when it already exists.</item>
/// <item>Both IDs: update that draft version.</item>
/// <item>Revision is internal: returned, grows on every save and publish, never sent by clients.</item>
/// </list>
/// Tests write camelCase; <see cref="LiveApi"/> converts to the snake_case wire names (workflowDefinitionId = workflow_definition_id).
/// </summary>
public sealed class ConfigurationVersionTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OneSaveEndpoint_CreatesUpdatesAndVersions_ByDefinitionAndVersionId()
    {
        LiveSettings.Require();
        using var api = new LiveApi(TimeSpan.FromSeconds(60));
        await api.LoginAsync("admin", "admin", "password");
        var aliceId = await EnsureApproverAsync(api);

        JsonNode Setup(string name) => Node(new
        {
            name, startStage = "A",
            stages = new[]
            {
                new
                {
                    key = "A", title = "A Review",
                    assignment = new { policy = "Bucket", members = new[] { aliceId } },
                    actions = new[]
                    {
                        new { key = "APPROVE", label = "Approve", destination = new { kind = "Approved" } },
                        new { key = "REJECT", label = "Reject", destination = new { kind = "Rejected" } },
                    },
                },
            },
        });
        Task<JsonNode> Save(object? definitionId, object? versionId, string name, string? actor = "admin", params int[] expected)
        {
            var body = new JsonObject { ["configuration"] = Setup(name) };
            if (definitionId is not null) body["workflowDefinitionId"] = definitionId.ToString();
            if (versionId is not null) body["workflowVersionId"] = versionId.ToString();
            return api.PostAsync("/api/workflow-configurations", body, actor, expected);
        }
        static string Path(JsonNode config, string suffix = "")
            => $"/api/workflow-configurations/{config["workflowDefinitionId"]}/versions/{config["workflowVersionId"]}{suffix}";

        // 1. No IDs: generated definition, first version, revision starts at 1. Retired fields are not exposed.
        var generated = await Save(null, null, "Generated");
        Assert.False(string.IsNullOrEmpty((string?)generated["workflowDefinitionId"]));
        Assert.False(string.IsNullOrEmpty((string?)generated["workflowVersionId"]));
        Assert.Equal(1, (int)generated["versionNumber"]!);
        Assert.Equal(1L, (long)generated["revision"]!);
        Assert.False((bool)generated["isPublished"]!);
        foreach (var retired in new[] { "id", "definitionId", "definitionVersionId", "definitionVersion" })
            Assert.False(generated.AsObject().ContainsKey(retired), $"retired field '{retired}' leaked into the response");

        // 2. Definition ID only, new definition: the caller's ID is honoured.
        var definitionId = "versions-" + Hex()[..12];
        var v1 = await Save(definitionId, null, "First");
        Assert.Equal(definitionId, (string)v1["workflowDefinitionId"]!);
        Assert.Equal(1, (int)v1["versionNumber"]!);
        Assert.Equal(1L, (long)v1["revision"]!);

        // 3. Both IDs update the same version: the version ID stays, revision appends.
        var v1b = await Save(definitionId, v1["workflowVersionId"], "Renamed");
        Assert.Equal((string)v1["workflowVersionId"]!, (string)v1b["workflowVersionId"]!);
        Assert.Equal(2L, (long)v1b["revision"]!);
        Assert.Equal("Renamed", (string)v1b["configuration"]!["name"]!);
        var v1c = await Save(definitionId, v1["workflowVersionId"], "Renamed again");
        Assert.Equal(3L, (long)v1c["revision"]!);
        Assert.Equal(1, (int)v1c["versionNumber"]!);
        Assert.Equal(v1c.ToJsonString(), (await api.GetAsync(Path(v1c))).ToJsonString());

        // 4. Definition ID only while a draft is open: refused (one open draft per definition).
        await Save(definitionId, null, "Second draft", "admin", 409);

        // 5. Bad requests.
        await Save(null, v1["workflowVersionId"], "x", "admin", 400);                // version ID without a definition ID
        await Save(definitionId, "nope", "x", "admin", 404);                         // unknown version
        await Save(generated["workflowDefinitionId"], v1["workflowVersionId"], "x", "admin", 404); // another definition's version
        foreach (var retired in new[] { "definitionId", "id", "expectedRevision" })     // retired names must not silently create a stray definition
        {
            var body = new JsonObject { ["workflowDefinitionId"] = definitionId, ["workflowVersionId"] = (string)v1["workflowVersionId"]!, ["configuration"] = Setup("x") };
            body[retired] = retired == "definitionId" ? "other" : 0;
            await api.PostAsync("/api/workflow-configurations", body, "admin", 400);
        }
        await api.PostAsync("/api/workflow-configurations", new JsonObject { ["workflowDefinitionId"] = definitionId }, "admin", 400); // configuration required

        // 6. Validate, then publish. Publish is idempotent; revision appends once per transition.
        Assert.True((bool)(await api.PostAsync(Path(v1c, "/validate"), null))["valid"]!);
        var published = await api.PostAsync(Path(v1c, "/publish"), null);
        Assert.True((bool)published["isPublished"]!);
        Assert.Equal(4L, (long)published["revision"]!);
        Assert.Equal(4L, (long)(await api.PostAsync(Path(v1c, "/publish"), null))["revision"]!);

        // 7. A published version is immutable.
        await Save(definitionId, v1["workflowVersionId"], "x", "admin", 409);

        // 8. Definition ID only after publishing: next version of the same definition, new version ID, revision restarts.
        var v2 = await Save(definitionId, null, "Second version");
        Assert.Equal(definitionId, (string)v2["workflowDefinitionId"]!);
        Assert.NotEqual((string)v1["workflowVersionId"]!, (string)v2["workflowVersionId"]!);
        Assert.Equal(2, (int)v2["versionNumber"]!);
        Assert.Equal(1L, (long)v2["revision"]!);
        Assert.False((bool)v2["isPublished"]!);

        // 9. Updating v2 never touches v1.
        var v2b = await Save(definitionId, v2["workflowVersionId"], "Second edited");
        Assert.Equal(2L, (long)v2b["revision"]!);
        Assert.Equal((string)v2["workflowVersionId"]!, (string)v2b["workflowVersionId"]!);
        var v1Again = await api.GetAsync(Path(v1c));
        Assert.True((bool)v1Again["isPublished"]!);
        Assert.Equal("Renamed again", (string)v1Again["configuration"]!["name"]!);

        // 10. One open draft at a time; publishing v2 opens the way for v3.
        await Save(definitionId, null, "x", "admin", 409);
        Assert.True((bool)(await api.PostAsync(Path(v2b, "/publish"), null))["isPublished"]!);
        var v3 = await Save(definitionId, null, "Third");
        Assert.Equal(3, (int)v3["versionNumber"]!);
        Assert.Equal(1L, (long)v3["revision"]!);

        // 11. Permissions and unknown targets.
        await api.LoginAsync("approver", "config-version-approver", "Password123!");
        await Save(null, null, "x", "approver", 403);
        await api.SendAsync("GET", Path(v1c), null, "approver", 403);
        await api.SendAsync("GET", $"/api/workflow-configurations/{definitionId}/versions/missing", null, "admin", 404);
        await api.SendAsync("POST", $"/api/workflow-configurations/{definitionId}/versions/missing/publish", null, "admin", 404);
        await api.SendAsync("POST", "/api/workflow-configurations", new JsonObject { ["configuration"] = Setup("x") }, null, 401);

        output.WriteLine("PASS configuration versions: generated and named definitions, update by version, new version by omission, revision appends, publish rules");
    }

    /// <summary>A local Approver without configuration rights: eligible as a member and used for the 403 checks.</summary>
    private static async Task<string> EnsureApproverAsync(LiveApi api)
    {
        var roles = (await api.GetAsync("/elsa/api/identity/roles"))["roles"]!.AsArray();
        if (!roles.Any(r => (string)r!["id"]! == "Approver"))
            await api.PostAsync("/elsa/api/identity/roles", new
            {
                id = "Approver", name = "Approver",
                permissions = new[] { "user-tasks:view", "user-tasks:claim", "user-tasks:complete" },
            });
        const string name = "config-version-approver";
        var users = (await api.GetAsync("/elsa/api/identity/users"))["users"]!.AsArray();
        var user = users.FirstOrDefault(u => (string)u!["name"]! == name)
            ?? await api.PostAsync("/elsa/api/identity/users", new { name, password = "Password123!", roles = new[] { "Approver" } });
        return user["id"]!.ToString();
    }
}

namespace IlWorkflow.UnitTests;

/// <summary>Small helpers that build workflow setups for the tests, so each test shows only what it is about.</summary>
public static class SetupFactory
{
    public static StageAction Action(string key, DestinationKind kind, string? stage = null, bool reason = false) =>
        new(key, null, new Destination(kind, stage), reason);

    public static StageDefinition Stage(string key, string[] members, StageAction[] actions, AssignmentPolicy policy = AssignmentPolicy.Bucket,
        string[]? parallelWith = null, string[]? viewers = null, string? userId = null) =>
        new(key, key + " review", new AssignmentDefinition(policy, members, userId), parallelWith, viewers, actions);

    /// <summary>A -> B -> Approved. A can also reject to a correction.</summary>
    public static WorkflowSetup TwoStages() => new("Expense", "A",
    [
        Stage("A", ["alice"], [Action("APPROVE", DestinationKind.Stage, "B"), Action("REJECT", DestinationKind.Correction, "A", reason: true)]),
        Stage("B", ["bob"], [Action("APPROVE", DestinationKind.Approved), Action("REJECT", DestinationKind.Rejected, reason: true)])
    ]);
}

/// <summary>Stands in for Elsa Identity: only the listed users have the Approver role.</summary>
public sealed class FakeDirectory(params string[] approvers) : IApproverDirectory
{
    public Task<bool> HasRoleAsync(string id, string role, CancellationToken ct) =>
        Task.FromResult(role == WorkflowConfigurationValidator.ApproverRole ? approvers.Contains(id) : role == WorkflowSetup.SupervisorRole && id == "sam");
}

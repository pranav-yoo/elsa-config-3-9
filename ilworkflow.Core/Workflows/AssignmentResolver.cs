namespace IlWorkflow.Core.Workflows;

/// <param name="Candidates">Users who may see and claim the task.</param>
/// <param name="Assignee">The owner, when one is known up front.</param>
/// <param name="Blocked">No eligible user: the task waits for a supervisor to assign it.</param>
public sealed record ResolvedAssignment(string[] Candidates, string? Assignee, bool Blocked);

/// <summary>
/// Decides who receives a stage's task. Elsa then owns everything after that:
/// claiming, ownership, reassignment and completion.
/// </summary>
public sealed class AssignmentResolver(IApproverDirectory users)
{
    public async Task<ResolvedAssignment> ResolveAsync(AssignmentDefinition assignment, CancellationToken ct)
    {
        // Only members who still have the Approver role are eligible.
        var eligible = new List<string>();
        foreach (var member in assignment.Members)
            if (await users.HasRoleAsync(member, IlWorkflow.Core.Validation.WorkflowConfigurationValidator.ApproverRole, ct))
                eligible.Add(member);

        if (eligible.Count == 0)
            return Blocked();

        if (assignment.Policy == AssignmentPolicy.Specific)
        {
            var owner = assignment.SpecificOwner;
            return owner is not null && eligible.Contains(owner) ? new([.. eligible], owner, false) : Blocked();
        }

        // Bucket: everyone eligible may claim it. With a single member there is nothing to claim, so assign directly.
        var assignee = eligible.Count == 1 ? eligible[0] : null;
        return new([.. eligible], assignee, false);
    }

    // Never treat "nobody can approve" as an approval: the task stays open until a supervisor assigns it.
    private static ResolvedAssignment Blocked() => new([], null, true);
}

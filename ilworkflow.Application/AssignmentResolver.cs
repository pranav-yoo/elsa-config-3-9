using System.Text.Json;



namespace IlWorkflow.Application.Workflows;

public sealed record ResolvedAssignment(string[] Candidates, string? Assignee, bool Blocked);

/// <summary>Selects participants only. Elsa remains responsible for claiming, ownership and completion.</summary>
public sealed class AssignmentResolver(IApproverDirectory users)
{
    public async Task<ResolvedAssignment> ResolveAsync(IlWorkflow.Domain.Configuration.AssignmentDefinition assignment, Dictionary<string, JsonElement> attributes, CancellationToken ct)
    {
        var eligible = new List<string>();
        foreach (var member in assignment.Members)
            if (await users.HasRoleAsync(member, "Approver", ct)) eligible.Add(member);
        var selected = assignment.Policy switch
        {
            AssignmentPolicy.Specific => assignment.UserId,
            AssignmentPolicy.Condition => (assignment.Rules ?? []).FirstOrDefault(x => AttributeRules.Matches(x.When, attributes))?.UserId,
            _ => null
        };
        var blocked = eligible.Count == 0 || assignment.Policy != AssignmentPolicy.Bucket && (selected is null || !eligible.Contains(selected));
        // An unresolved assignment never turns into implicit approval. Supervisors may assign the blocked task.
        return new(blocked ? [] : eligible.ToArray(), blocked ? null : selected, blocked);
    }
}

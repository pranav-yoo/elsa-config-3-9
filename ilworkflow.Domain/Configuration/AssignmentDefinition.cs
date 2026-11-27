using System.Text.Json.Serialization;

namespace IlWorkflow.Domain.Configuration;

/// <summary>Who receives a stage's task.</summary>
/// <param name="Policy">Bucket: every member sees it and one claims it. Specific: one member owns it.</param>
/// <param name="Members">Candidate user IDs. Each needs the Approver role.</param>
/// <param name="UserId">Owner for the Specific policy. Optional when there is exactly one member.</param>
public sealed record AssignmentDefinition(AssignmentPolicy Policy, string[] Members, string? UserId = null)
{
    /// <summary>The owner of a Specific assignment: UserId, or the only member.</summary>
    [JsonIgnore]
    public string? SpecificOwner => UserId ?? (Members is { Length: 1 } ? Members[0] : null);
}

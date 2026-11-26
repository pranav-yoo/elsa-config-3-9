using System.Text.Json.Serialization;
using IlWorkflow.Domain.Enums;

namespace IlWorkflow.Domain.Configuration;

public sealed record AssignmentDefinition(AssignmentPolicy Policy, string[] Members, string? UserId = null, AssignmentRule[]? Rules = null)
{
    /// <summary>Specific owner: UserId, or the only member when UserId is omitted. Not serialized.</summary>
    [JsonIgnore]
    public string? SpecificOwner => UserId ?? (Members is { Length: 1 } ? Members[0] : null);
}

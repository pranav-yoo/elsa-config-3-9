using System.Text.Json.Serialization;

namespace IlWorkflow.Domain.Configuration;

public sealed record AssignmentRule(AttributeCondition[]? Conditions, string UserId)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AttributeCondition? When { get => null; init { if (value is not null) Conditions = [.. Conditions ?? [], value]; } }
}

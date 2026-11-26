using System.Text.Json.Serialization;

namespace IlWorkflow.Domain.Configuration;

// Rules hold a Conditions list. Older snapshots stored one "when" object: it is still read
// (folded into Conditions) so published versions and running instances keep routing, but never written.
public sealed record RouteRule(AttributeCondition[]? Conditions, Destination Destination)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AttributeCondition? When { get => null; init { if (value is not null) Conditions = [.. Conditions ?? [], value]; } }
}

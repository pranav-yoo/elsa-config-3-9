using System.Text.Json.Serialization;
using IlWorkflow.Domain.Rules;

namespace IlWorkflow.Domain.Configuration;

public sealed record StageDefinition(string Key, string Title, AssignmentDefinition Assignment,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? ParallelWith = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? ViewableByStages = null)
{
    private StageAction[]? actions;
    private Destination? legacyApproveTo, legacyRejectTo;
    private RouteRule[]? legacyApproveRules;

    /// <summary>All task actions. Older snapshots without "actions" are converted from approveTo/rejectTo/approveRules.</summary>
    public StageAction[]? Actions
    {
        get => actions ?? (legacyApproveTo is null && legacyRejectTo is null ? null :
        [
            new(TaskActionKeys.Approve, "Approve", legacyApproveTo!, Rules: legacyApproveRules),
            new(TaskActionKeys.Reject, "Reject", legacyRejectTo!)
        ]);
        init => actions = value;
    }

    // Legacy input only: still read so published versions and running instances keep routing, never written.
    [JsonPropertyName("approveTo"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Destination? LegacyApproveTo { get => null; init => legacyApproveTo = value; }
    [JsonPropertyName("rejectTo"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Destination? LegacyRejectTo { get => null; init => legacyRejectTo = value; }
    [JsonPropertyName("approveRules"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RouteRule[]? LegacyApproveRules { get => null; init => legacyApproveRules = value; }

    [JsonIgnore] public StageAction? Approve => Actions?.FirstOrDefault(x => x?.Key == TaskActionKeys.Approve);
    [JsonIgnore] public StageAction? Reject => Actions?.FirstOrDefault(x => x?.Key == TaskActionKeys.Reject);
    [JsonIgnore] public Destination? ApproveTo => Approve?.Destination;
    [JsonIgnore] public Destination? RejectTo => Reject?.Destination;
    [JsonIgnore] public RouteRule[]? ApproveRules => Approve?.Rules;
    [JsonIgnore] public StageAction[] CustomActions => (Actions ?? []).Where(x => x is not null && !x.IsBuiltIn).ToArray();
}

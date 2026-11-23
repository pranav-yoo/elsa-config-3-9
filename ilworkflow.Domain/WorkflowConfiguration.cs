using System.Text.Json;
using System.Text.Json.Serialization;

namespace IlWorkflow.Domain.Configuration;

public enum AttributeType { String, Decimal, Integer, Boolean, Date, Choice }
public enum AssignmentPolicy { Bucket, Specific, Condition }
public enum ConditionOperator { Equals, NotEquals, GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual }
// Custom = a configured stage action (e.g. REFER_UW); the graph routes it by the recorded action key.
public enum TaskDecision { Approve, Reject, Resubmit, Custom }

public static class TaskActionKeys
{
    public const string Approve = "APPROVE";
    public const string Reject = "REJECT";
    public const string Resubmit = "RESUBMIT";

    // Pending Elsa tasks retain the key casing recorded when they were created.
    public static TaskDecision? Parse(string? key) => key?.ToUpperInvariant() switch
    {
        Approve => TaskDecision.Approve,
        Reject => TaskDecision.Reject,
        Resubmit => TaskDecision.Resubmit,
        _ => null
    };
}

public enum DestinationKind { Stage, Correction, Approved, Rejected }
public enum TicketOutcome { InReview, Returned, Approved, Rejected }

/// <summary>One application configuration is bound to exactly one native Elsa definition version.</summary>
public sealed class WorkflowConfiguration
{
    public long Id { get; set; }
    public string TenantId { get; set; } = "";
    public string DefinitionId { get; set; } = "";
    public string DefinitionVersionId { get; set; } = "";
    public int DefinitionVersion { get; set; }
    public long Revision { get; set; } = 1;
    public bool UsesAttributeCatalog { get; set; }
    // Frozen survives native retraction: a previously published snapshot can never be edited.
    public bool Frozen { get; set; }
    public bool Ready { get; set; }
    public string DefinitionHash { get; set; } = "";
    public string ConfigurationJson { get; set; } = "{}";
}

/// <param name="Supervisors">
/// User IDs that can see every task of this workflow (all stages, incl. blocked and correction tasks)
/// and reassign them. They need the workflow_supervisor role (user-tasks:view, user-tasks:assign, no supervise),
/// so they see only the workflows that list them.
/// </param>
public sealed record WorkflowSetup(string Name, AttributeDefinition[] Attributes, string StartStage, StageDefinition[] Stages,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Supervisors = null)
{
    public const string SupervisorRole = "workflow_supervisor";
}
public sealed record AttributeDefinition(string Key, AttributeType Type, bool Required = false, string[]? Choices = null,
    int? MaxLength = null, decimal? Minimum = null, decimal? Maximum = null);
/// <summary>One comparison. In a rule's Conditions list, entries sharing an OrGroup are OR-ed; everything else is AND-ed.</summary>
public sealed record AttributeCondition(string Attribute, ConditionOperator Operator, JsonElement Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? OrGroup = null);
public sealed record Destination(DestinationKind Kind, string? StageKey = null);
// Rules hold a Conditions list. Older snapshots stored one "when" object: it is still read
// (folded into Conditions) so published versions and running instances keep routing, but never written.
public sealed record RouteRule(AttributeCondition[]? Conditions, Destination Destination)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AttributeCondition? When { get => null; init { if (value is not null) Conditions = [.. Conditions ?? [], value]; } }
}
public sealed record AssignmentRule(AttributeCondition[]? Conditions, string UserId)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AttributeCondition? When { get => null; init { if (value is not null) Conditions = [.. Conditions ?? [], value]; } }
}
public sealed record AssignmentDefinition(AssignmentPolicy Policy, string[] Members, string? UserId = null, AssignmentRule[]? Rules = null)
{
    /// <summary>Specific owner: UserId, or the only member when UserId is omitted. Not serialized.</summary>
    [JsonIgnore]
    public string? SpecificOwner => UserId ?? (Members is { Length: 1 } ? Members[0] : null);
}
/// <summary>
/// One task button. APPROVE and REJECT are required, reserved keys; other keys are custom actions such as REFER_UW.
/// Rules (optional) pick a conditional destination, first match wins; otherwise Destination.
/// </summary>
public sealed record StageAction(string Key, string? Label, Destination Destination,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool RequireReason = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RouteRule[]? Rules = null)
{
    [JsonIgnore] public bool IsBuiltIn => Key is TaskActionKeys.Approve or TaskActionKeys.Reject;
    [JsonIgnore] public string DisplayLabel => !string.IsNullOrWhiteSpace(Label) ? Label
        : Key == TaskActionKeys.Approve ? "Approve" : Key == TaskActionKeys.Reject ? "Reject" : Key;
    /// <summary>Key of a precomputed rule result. APPROVE keeps the original "stage_index" form read by older graphs.</summary>
    public static string RouteKey(string stageKey, string actionKey, int index) =>
        actionKey == TaskActionKeys.Approve ? $"{stageKey}_{index}" : $"{stageKey}_{actionKey}_{index}";
}

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

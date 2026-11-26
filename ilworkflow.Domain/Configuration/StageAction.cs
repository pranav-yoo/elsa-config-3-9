using System.Text.Json.Serialization;
using IlWorkflow.Domain.Rules;

namespace IlWorkflow.Domain.Configuration;

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

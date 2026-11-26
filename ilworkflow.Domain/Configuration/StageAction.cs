using System.Text.Json.Serialization;

namespace IlWorkflow.Domain.Configuration;

/// <summary>
/// One task button of a stage (for example APPROVE, REJECT or REFER_UW). Rules (optional) pick a conditional
/// destination, first match wins; otherwise Destination applies. RequireReason asks the user for a reason.
/// </summary>
public sealed record StageAction(string Key, string? Label, Destination Destination,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool RequireReason = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RouteRule[]? Rules = null)
{
    [JsonIgnore] public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Key : Label;

    /// <summary>Key of a precomputed rule result, shared by the generated graph and the runtime snapshot.</summary>
    public static string RouteKey(string stageKey, string actionKey, int index) => $"{stageKey}_{actionKey}_{index}";
}

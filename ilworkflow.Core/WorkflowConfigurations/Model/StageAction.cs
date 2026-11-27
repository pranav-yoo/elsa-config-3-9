using System.Text.Json.Serialization;

namespace IlWorkflow.Core.WorkflowConfigurations.Model;

/// <summary>One button on a task, for example APPROVE, REJECT or REFER_UW.</summary>
/// <param name="Key">UPPER_CASE key sent by the client when the button is pressed.</param>
/// <param name="Label">Button text. Defaults to the key.</param>
/// <param name="Destination">Where the ticket goes when this button is pressed.</param>
/// <param name="RequireReason">When true the user must send a non-empty "reason".</param>
public sealed record StageAction(
    string Key,
    string? Label,
    Destination Destination,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool RequireReason = false)
{
    [JsonIgnore] public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Key : Label;
}

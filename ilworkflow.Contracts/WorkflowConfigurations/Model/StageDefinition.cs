using System.Text.Json.Serialization;

namespace IlWorkflow.Contracts.WorkflowConfigurations.Model;

/// <summary>One approval step.</summary>
/// <param name="Key">Short unique ID, for example "A".</param>
/// <param name="Title">Shown as the task title.</param>
/// <param name="Assignment">Who receives the task.</param>
/// <param name="Actions">The buttons on the task. Each button says where the ticket goes next.</param>
/// <param name="ParallelWith">Other stages that open at the same time as this one (a parallel group).</param>
public sealed record StageDefinition(
    string Key,
    string Title,
    AssignmentDefinition Assignment,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? ParallelWith = null,
    StageAction[]? Actions = null)
{
    [JsonIgnore] public bool IsParallelGroup => ParallelWith is not null;
}

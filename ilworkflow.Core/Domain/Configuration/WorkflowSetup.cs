using System.Text.Json.Serialization;

namespace IlWorkflow.Core.Domain.Configuration;

/// <summary>
/// A complete approval workflow: its name, the first stage and every stage.
/// This object is stored as JSON and also embedded in the generated Elsa definition,
/// so a running ticket always uses the version it was started with.
/// </summary>
/// <param name="Supervisors">
/// User IDs (role <see cref="SupervisorRole"/>) that can see and reassign every task of this workflow.
/// </param>
public sealed record WorkflowSetup(
    string Name,
    string StartStage,
    StageDefinition[] Stages,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Supervisors = null)
{
    public const string SupervisorRole = "workflow_supervisor";

    public StageDefinition? FindStage(string key) => Stages.FirstOrDefault(stage => stage.Key == key);
}

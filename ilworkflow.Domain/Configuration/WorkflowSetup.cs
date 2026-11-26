using System.Text.Json.Serialization;

namespace IlWorkflow.Domain.Configuration;

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

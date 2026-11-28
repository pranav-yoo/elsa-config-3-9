namespace IlWorkflow.Contracts.WorkflowConfigurations.Requests;

/// <summary>The "configuration" object of a save request: name, first stage, stages and optional supervisors.</summary>
public sealed record WorkflowSetupRequest(string Name, string StartStage, StageDefinition[] Stages, string[]? Supervisors = null)
{
    public WorkflowSetup ToSetup() => new(Name, StartStage, Stages, Supervisors);
}

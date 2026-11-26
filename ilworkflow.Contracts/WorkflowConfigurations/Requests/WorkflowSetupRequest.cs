namespace IlWorkflow.Contracts.WorkflowConfigurations.Requests;

/// <summary>The workflow itself: name, start stage and stages. Attributes are not configurable for now, so none are sent.</summary>
public sealed record WorkflowSetupRequest(string Name, string StartStage, StageDefinition[] Stages, string[]? Supervisors = null);

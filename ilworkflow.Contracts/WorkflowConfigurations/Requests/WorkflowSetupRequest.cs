namespace IlWorkflow.Contracts.WorkflowConfigurations.Requests;

/// <summary>The workflow itself: name, start stage and stages. Attribute fields are resolved from the catalog, never sent.</summary>
public sealed record WorkflowSetupRequest(string Name, string StartStage, StageDefinition[] Stages, string[]? Supervisors = null);

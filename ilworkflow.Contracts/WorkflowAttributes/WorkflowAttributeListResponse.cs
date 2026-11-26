namespace IlWorkflow.Contracts.WorkflowAttributes;

/// <summary>The catalog fields of one definition.</summary>
public sealed record WorkflowAttributeListResponse(IReadOnlyList<WorkflowAttributeResponse> Items);

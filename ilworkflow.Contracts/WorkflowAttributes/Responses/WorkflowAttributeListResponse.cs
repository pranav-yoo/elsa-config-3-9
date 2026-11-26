namespace IlWorkflow.Contracts.WorkflowAttributes.Responses;

/// <summary>The catalog fields of one definition.</summary>
public sealed record WorkflowAttributeListResponse(IReadOnlyList<WorkflowAttributeResponse> Items);

namespace IlWorkflow.Contracts.Tasks.Requests;

/// <summary>
/// Body of claim and release. ExpectedRevision and OperationId are optional: omitted values use the task's current
/// revision and a new operation ID. Send the original OperationId to retry the same command safely.
/// </summary>
public sealed record TaskCommandRequest(int? ExpectedRevision = null, string? OperationId = null);

namespace IlWorkflow.Core.Contracts.Tasks.Requests;

/// <summary>Reassign a task to another user (owner or supervisor).</summary>
public sealed record AssignTaskRequest(string UserId, string? Reason = null, int? ExpectedRevision = null, string? OperationId = null);

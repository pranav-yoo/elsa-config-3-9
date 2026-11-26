namespace IlWorkflow.Contracts.Tasks;

/// <summary>Reassign a task to another user (owner or supervisor).</summary>
public sealed record AssignTaskRequest(string UserId, string? Reason = null, int? ExpectedRevision = null, string? OperationId = null);

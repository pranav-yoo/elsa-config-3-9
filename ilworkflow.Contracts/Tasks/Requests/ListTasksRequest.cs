namespace IlWorkflow.Contracts.Tasks.Requests;

/// <summary>Filters of a task list. Scope: assigned, available, history, all.</summary>
public sealed record ListTasksRequest(string Scope = "assigned", string? Status = null, string? WorkflowInstanceId = null,
    string? Cursor = null, int Limit = 50, bool IncludeTotalCount = false);

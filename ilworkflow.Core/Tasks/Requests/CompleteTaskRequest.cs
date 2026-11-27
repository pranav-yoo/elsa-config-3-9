using System.Text.Json;

namespace IlWorkflow.Core.Tasks.Requests;

/// <summary>Press a task button. <c>ActionKey</c> is APPROVE, REJECT, RESUBMIT or a configured custom key; <c>Data</c> carries the reason or comment.</summary>
public sealed record CompleteTaskRequest(string ActionKey, JsonElement? Data = null, int? ExpectedRevision = null, string? OperationId = null);

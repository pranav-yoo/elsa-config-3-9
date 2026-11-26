namespace IlWorkflow.Infrastructure.Tasks;

/// <summary>Result of a task command: Accepted (HTTP 202) with the updated task, or a conflict (HTTP 409).</summary>
public sealed record TaskCommandResult(bool Accepted, object Body);

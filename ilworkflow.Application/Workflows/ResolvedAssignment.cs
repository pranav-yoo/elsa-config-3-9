namespace IlWorkflow.Application.Workflows;

public sealed record ResolvedAssignment(string[] Candidates, string? Assignee, bool Blocked);

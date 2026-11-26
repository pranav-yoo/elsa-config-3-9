namespace IlWorkflow.Domain.Configuration;

public sealed record AssignmentRule(AttributeCondition[]? Conditions, string UserId);

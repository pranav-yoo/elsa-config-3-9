namespace IlWorkflow.Domain.Configuration;

public sealed record RouteRule(AttributeCondition[]? Conditions, Destination Destination);

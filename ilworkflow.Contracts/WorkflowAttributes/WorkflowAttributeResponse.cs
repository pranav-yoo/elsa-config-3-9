namespace IlWorkflow.Contracts.WorkflowAttributes;

/// <summary>One catalog field as returned by the API. Mirrors the stored field without exposing the persistence entity.</summary>
public sealed record WorkflowAttributeResponse(
    long Id, string DefinitionId, string Key, AttributeType Type, bool Required, string[]? Choices,
    int? MaxLength, decimal? Minimum, decimal? Maximum, AttributeStatus Status);

using IlWorkflow.Domain.Enums;

namespace IlWorkflow.Domain.Configuration;

public sealed record AttributeDefinition(string Key, AttributeType Type, bool Required = false, string[]? Choices = null,
    int? MaxLength = null, decimal? Minimum = null, decimal? Maximum = null);

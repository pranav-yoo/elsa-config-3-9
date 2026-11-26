using IlWorkflow.Domain.Enums;

namespace IlWorkflow.Domain.Configuration;

public sealed record Destination(DestinationKind Kind, string? StageKey = null);

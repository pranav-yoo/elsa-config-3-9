using System.Text.Json;

namespace IlWorkflow.Application.Validation;

public sealed record DecisionData(string Action, JsonElement Data);

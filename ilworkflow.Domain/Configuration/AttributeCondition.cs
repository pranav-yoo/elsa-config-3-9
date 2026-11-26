using System.Text.Json;
using System.Text.Json.Serialization;
using IlWorkflow.Domain.Enums;

namespace IlWorkflow.Domain.Configuration;

/// <summary>One comparison. In a rule's Conditions list, entries sharing an OrGroup are OR-ed; everything else is AND-ed.</summary>
public sealed record AttributeCondition(string Attribute, ConditionOperator Operator, JsonElement Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? OrGroup = null);

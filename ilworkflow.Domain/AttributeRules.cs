using System.Text.Json;
namespace IlWorkflow.Domain.Configuration;

/// <summary>One typed comparison implementation is shared by validation, assignment and generated conditions.</summary>
public static class AttributeRules
{
    public static bool IsValid(AttributeDefinition field, JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return !field.Required;
        return field.Type switch
        {
            AttributeType.String => value.ValueKind == JsonValueKind.String && (!field.Required || !string.IsNullOrWhiteSpace(value.GetString()))
                && (field.MaxLength is null || value.GetString()!.Length <= field.MaxLength),
            AttributeType.Choice => value.ValueKind == JsonValueKind.String && (field.Choices ?? []).Contains(value.GetString()),
            AttributeType.Decimal => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && InRange(field, number),
            AttributeType.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer) && InRange(field, integer),
            AttributeType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            AttributeType.Date => value.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", out _),
            _ => false
        };
    }
    private static bool InRange(AttributeDefinition field, decimal value) => (field.Minimum is null || value >= field.Minimum) && (field.Maximum is null || value <= field.Maximum);
    public static bool Matches(AttributeCondition condition, IReadOnlyDictionary<string, JsonElement> attributes)
    {
        if (!attributes.TryGetValue(condition.Attribute, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return false;
        var comparison = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal().CompareTo(condition.Value.GetDecimal()),
            JsonValueKind.String => string.CompareOrdinal(value.GetString(), condition.Value.GetString()),
            JsonValueKind.True or JsonValueKind.False => value.GetBoolean().CompareTo(condition.Value.GetBoolean()),
            _ => throw new ArgumentException("Unsupported comparison.")
        };
        return condition.Operator switch
        {
            ConditionOperator.Equals => comparison == 0, ConditionOperator.NotEquals => comparison != 0,
            ConditionOperator.GreaterThan => comparison > 0, ConditionOperator.GreaterThanOrEqual => comparison >= 0,
            ConditionOperator.LessThan => comparison < 0, ConditionOperator.LessThanOrEqual => comparison <= 0,
            _ => false
        };
    }
}


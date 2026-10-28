using System.Text.Json;
using Elsa.Identity.Contracts;
using Elsa.Identity.Models;

namespace IlWorkflow.Api.Tickets;

public sealed record TicketConfiguration(string Id, int Version, TicketDefinition Definition);
public sealed record TicketDefinition(string Name, FieldDefinition[] Attributes, string StartStage, StageDefinition[] Stages);
public sealed record FieldDefinition(string Key, string Type, bool Required = false, string[]? Choices = null);
public sealed record Condition(string Attribute, string Operator, JsonElement Value);
public sealed record RouteRule(Condition When, string Destination);
public sealed record AssignmentRule(Condition When, string UserId);
public sealed record AssignmentDefinition(string Policy, string[] Members, string? UserId = null, AssignmentRule[]? Rules = null);
public sealed record StageDefinition(string Key, string Name, AssignmentDefinition Assignment, string ApproveTo, string RejectTo, RouteRule[]? ApproveRules = null);
public sealed record StageTask(string Key, string Title, string[] Candidates, string? Assignee, bool Correction, bool Blocked);

public static class TicketJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new ArgumentException("Missing configuration.");

    public static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }

    public static void ValidateAttributes(TicketDefinition definition, Dictionary<string, JsonElement> attributes)
    {
        Require(attributes.Keys.All(key => definition.Attributes.Any(f => f.Key == key)), "Unknown attribute.");
        foreach (var field in definition.Attributes)
        {
            if (!attributes.TryGetValue(field.Key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                Require(!field.Required, $"{field.Key} is required.");
                continue;
            }
            var valid = field.Type switch
            {
                "string" => value.ValueKind == JsonValueKind.String && (!field.Required || !string.IsNullOrWhiteSpace(value.GetString())),
                "decimal" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "date" => value.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", out _),
                "choice" => value.ValueKind == JsonValueKind.String && (field.Choices ?? []).Contains(value.GetString()),
                _ => false
            };
            Require(valid, $"Invalid {field.Key} ({field.Type}).");
        }
    }

    public static bool Matches(Condition condition, IReadOnlyDictionary<string, JsonElement> attributes)
    {
        if (!attributes.TryGetValue(condition.Attribute, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return false;
        var comparison = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal().CompareTo(condition.Value.GetDecimal()),
            JsonValueKind.String => string.CompareOrdinal(value.GetString(), condition.Value.GetString()),
            JsonValueKind.True or JsonValueKind.False => value.GetBoolean().CompareTo(condition.Value.GetBoolean()),
            _ => throw new ArgumentException("Unsupported condition value.")
        };
        return condition.Operator switch
        {
            "equals" => comparison == 0, "notEquals" => comparison != 0,
            "greaterThan" => comparison > 0, "greaterThanOrEqual" => comparison >= 0,
            "lessThan" => comparison < 0, "lessThanOrEqual" => comparison <= 0,
            _ => throw new ArgumentException("Unknown condition operator.")
        };
    }

    public static async Task ValidateConfigurationAsync(TicketConfiguration configuration, TicketIdentity users, CancellationToken cancellationToken = default)
    {
        var definition = configuration.Definition;
        Require(configuration.Version > 0 && definition.Stages.Length > 0, "A version and stages are required.");
        Require(definition.Stages.Select(s => s.Key).Distinct().Count() == definition.Stages.Length, "Duplicate stage keys.");
        Require(definition.Attributes.Select(f => f.Key).Distinct().Count() == definition.Attributes.Length, "Duplicate attribute keys.");
        Require(definition.Stages.Any(s => s.Key == definition.StartStage), "Unknown start stage.");
        bool Destination(string key) => key is "$completed" or "$rejected" or "$requester" || definition.Stages.Any(s => s.Key == key);
        void ValidateCondition(Condition condition)
        {
            var field = definition.Attributes.SingleOrDefault(f => f.Key == condition.Attribute);
            Require(field is not null, "Unknown condition attribute.");
            ValidateAttributes(new("condition", [field! with { Required = true }], "", []), new() { [condition.Attribute] = condition.Value });
            Require(new[] { "equals", "notEquals", "greaterThan", "greaterThanOrEqual", "lessThan", "lessThanOrEqual" }.Contains(condition.Operator), "Unknown condition operator.");
            Require(condition.Operator is "equals" or "notEquals" || field!.Type is "decimal" or "integer" or "date", "Ordered comparisons need a number or date.");
        }
        foreach (var stage in definition.Stages)
        {
            Require(Destination(stage.ApproveTo) && Destination(stage.RejectTo), "Unknown stage destination.");
            Require(stage.ApproveTo != "$requester", "Use rejectTo to return for correction.");
            Require(new[] { "bucket", "specific", "condition" }.Contains(stage.Assignment.Policy), "Supported policies: bucket, specific, condition.");
            foreach (var member in stage.Assignment.Members)
            {
                Require(await users.HasRoleAsync(member, "Approver", cancellationToken), $"Unknown approver {member}.");
            }
            if (stage.Assignment.Policy == "specific") Require(stage.Assignment.Members.Contains(stage.Assignment.UserId), "Specific assignee must be a candidate.");
            foreach (var rule in stage.Assignment.Rules ?? [])
            {
                ValidateCondition(rule.When);
                Require(stage.Assignment.Members.Contains(rule.UserId), "Rule assignee must be a candidate.");
            }
            foreach (var rule in stage.ApproveRules ?? [])
            {
                ValidateCondition(rule.When);
                Require(Destination(rule.Destination) && rule.Destination != "$requester", "Invalid approval route.");
            }
        }
        // Sequential approval paths must terminate; explicit rejection returns are allowed.
        void Visit(string key, HashSet<string> path)
        {
            if (key is "$completed" or "$rejected") return;
            Require(path.Add(key), "Approval cycles are unsupported.");
            var stage = definition.Stages.Single(s => s.Key == key);
            foreach (var destination in (stage.ApproveRules ?? []).Select(r => r.Destination).Append(stage.ApproveTo)) Visit(destination, new(path));
        }
        Visit(definition.StartStage, []);
    }
}

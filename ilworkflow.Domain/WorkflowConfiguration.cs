using System.Text.Json;
using System.Text.Json.Serialization;

namespace IlWorkflow.Domain.Configuration;

public enum AttributeType { String, Decimal, Integer, Boolean, Date, Choice }
public enum AssignmentPolicy { Bucket, Specific, Condition }
public enum ConditionOperator { Equals, NotEquals, GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual }
public enum TaskDecision { Approve, Reject, Resubmit }
public enum DestinationKind { Stage, Correction, Approved, Rejected }
public enum TicketOutcome { InReview, Returned, Approved, Rejected }

/// <summary>One application configuration is bound to exactly one native Elsa definition version.</summary>
public sealed class WorkflowConfiguration
{
    public long Id { get; set; }
    public string TenantId { get; set; } = "";
    public string DefinitionId { get; set; } = "";
    public string DefinitionVersionId { get; set; } = "";
    public int DefinitionVersion { get; set; }
    public long Revision { get; set; } = 1;
    // Frozen survives native retraction: a previously published snapshot can never be edited.
    public bool Frozen { get; set; }
    public bool Ready { get; set; }
    public string DefinitionHash { get; set; } = "";
    public string ConfigurationJson { get; set; } = "{}";
}

public sealed record WorkflowSetup(string Name, AttributeDefinition[] Attributes, string StartStage, StageDefinition[] Stages);
public sealed record AttributeDefinition(string Key, AttributeType Type, bool Required = false, string[]? Choices = null,
    int? MaxLength = null, decimal? Minimum = null, decimal? Maximum = null);
public sealed record AttributeCondition(string Attribute, ConditionOperator Operator, JsonElement Value);
public sealed record Destination(DestinationKind Kind, string? StageKey = null);
public sealed record RouteRule(AttributeCondition When, Destination Destination);
public sealed record AssignmentRule(AttributeCondition When, string UserId);
public sealed record AssignmentDefinition(AssignmentPolicy Policy, string[] Members, string? UserId = null, AssignmentRule[]? Rules = null);
public sealed record StageDefinition(string Key, string Title, AssignmentDefinition Assignment, Destination ApproveTo,
    Destination RejectTo, RouteRule[]? ApproveRules = null);

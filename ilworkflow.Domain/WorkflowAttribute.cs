namespace IlWorkflow.Domain.Configuration;

public enum AttributeStatus { Inactive, Active }

/// <summary>Reusable field master, configured before creating the Elsa definition.</summary>
public sealed class WorkflowAttribute
{
    public long Id { get; set; }
    public string DefinitionId { get; set; } = "";
    public string Key { get; set; } = "";
    public AttributeType Type { get; set; }
    public bool Required { get; set; }
    public string[]? Choices { get; set; }
    public int? MaxLength { get; set; }
    public decimal? Minimum { get; set; }
    public decimal? Maximum { get; set; }
    public AttributeStatus Status { get; set; } = AttributeStatus.Active;
    public AttributeDefinition ToDefinition() => new(Key, Type, Required, Choices, MaxLength, Minimum, Maximum);
}

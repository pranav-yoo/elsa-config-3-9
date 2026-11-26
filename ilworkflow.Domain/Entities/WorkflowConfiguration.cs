namespace IlWorkflow.Domain.Entities;

/// <summary>One application configuration is bound to exactly one native Elsa definition version.</summary>
public sealed class WorkflowConfiguration
{
    public long Id { get; set; }
    public string TenantId { get; set; } = "";
    public string DefinitionId { get; set; } = "";
    public string DefinitionVersionId { get; set; } = "";
    public int DefinitionVersion { get; set; }
    public long Revision { get; set; } = 1;
    public bool UsesAttributeCatalog { get; set; }
    // Frozen survives native retraction: a previously published snapshot can never be edited.
    public bool Frozen { get; set; }
    public bool Ready { get; set; }
    public string DefinitionHash { get; set; } = "";
    public string ConfigurationJson { get; set; } = "{}";
}

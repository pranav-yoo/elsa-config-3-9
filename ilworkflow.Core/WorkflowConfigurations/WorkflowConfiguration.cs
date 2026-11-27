namespace IlWorkflow.Core.WorkflowConfigurations;

/// <summary>
/// Database row (Application.WorkflowConfigurations) for ONE version of an Elsa workflow definition.
/// Elsa stores the generated flowchart; this row stores the readable setup it was generated from.
/// </summary>
public sealed class WorkflowConfiguration
{
    public long Id { get; set; }
    public string TenantId { get; set; } = "";
    /// <summary>Elsa definition ID, shared by all versions.</summary>
    public string DefinitionId { get; set; } = "";
    /// <summary>Elsa's ID of this exact version.</summary>
    public string DefinitionVersionId { get; set; } = "";
    public int DefinitionVersion { get; set; }
    /// <summary>Grows on every save/publish. Used by EF Core as a concurrency token.</summary>
    public long Revision { get; set; } = 1;
    /// <summary>Set once on publish and never cleared: a published setup can never be edited again.</summary>
    public bool Frozen { get; set; }
    /// <summary>True once Elsa has successfully published this version, so tickets may start.</summary>
    public bool Ready { get; set; }
    /// <summary>SHA-256 of the generated Elsa JSON, to detect edits made outside this API.</summary>
    public string DefinitionHash { get; set; } = "";
    /// <summary>The <see cref="Configuration.WorkflowSetup"/> as JSON.</summary>
    public string ConfigurationJson { get; set; } = "{}";
}

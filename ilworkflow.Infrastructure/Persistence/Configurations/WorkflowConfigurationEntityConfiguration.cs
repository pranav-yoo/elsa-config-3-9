using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IlWorkflow.Infrastructure.Persistence.Configurations;

/// <summary>How the <see cref="WorkflowConfiguration"/> entity maps to the "Application"."WorkflowConfigurations" table.</summary>
public sealed class WorkflowConfigurationEntityConfiguration : IEntityTypeConfiguration<WorkflowConfiguration>
{
    public void Configure(EntityTypeBuilder<WorkflowConfiguration> row)
    {
        row.ToTable("WorkflowConfigurations", "Application");
        row.HasKey(x => x.Id);
        row.Property(x => x.ConfigurationJson).HasColumnType("jsonb");
        // EF Core adds "WHERE Revision = <old value>" to updates, so two concurrent saves cannot both win.
        row.Property(x => x.Revision).IsConcurrencyToken();
        row.HasIndex(x => new { x.TenantId, x.DefinitionVersionId }).IsUnique();
    }
}

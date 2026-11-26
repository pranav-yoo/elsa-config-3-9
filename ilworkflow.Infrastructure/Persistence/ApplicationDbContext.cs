using Microsoft.EntityFrameworkCore;

namespace IlWorkflow.Infrastructure.Persistence;

/// <summary>Application-owned configuration. Elsa owns its own contexts and migrations.</summary>
public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<WorkflowConfiguration> WorkflowConfigurations => Set<WorkflowConfiguration>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var row = modelBuilder.Entity<WorkflowConfiguration>();
        row.ToTable("WorkflowConfigurations", "Application");
        row.HasKey(x => x.Id);
        row.Property(x => x.ConfigurationJson).HasColumnType("jsonb");
        row.Property(x => x.Revision).IsConcurrencyToken();
        row.HasIndex(x => new { x.TenantId, x.DefinitionVersionId }).IsUnique();
    }
}

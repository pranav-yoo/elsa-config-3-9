using Microsoft.EntityFrameworkCore;

namespace IlWorkflow.Infrastructure.Persistence;

/// <summary>Application-owned tables. Elsa owns its own contexts and migrations.</summary>
public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<WorkflowConfiguration> WorkflowConfigurations => Set<WorkflowConfiguration>();

    // Picks up every IEntityTypeConfiguration<T> in the Persistence/Configurations folder.
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);
}

using System.Text.Json;
using Elsa.Persistence.EFCore;
using Elsa.UserTasks.Persistence.EFCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace IlWorkflow.Infrastructure.Persistence.UserTasks;

/// <summary>Elsa 3.9 migration uses text TagsJson; Npgsql otherwise infers a PostgreSQL array.</summary>
public sealed class UserTaskPostgreSqlModel : IEntityModelCreatingHandler
{
    private static List<string> ReadTags(string value)
    {
        // Earlier unpatched writes cast an empty PostgreSQL array to the text value "{}".
        // Treat that legacy empty value as an empty list without deleting any task/history records.
        if (value == "{}") return [];
        return JsonSerializer.Deserialize<List<string>>(value) ?? [];
    }

    public void Handle(ElsaDbContextBase dbContext, ModelBuilder modelBuilder, IMutableEntityType entityType)
    {
        if (dbContext is not UserTasksElsaDbContext || entityType.ClrType != typeof(UserTaskRecord)) return;
        var entity = modelBuilder.Entity<UserTaskRecord>();
        entity.Ignore(x => x.Tags);
        var tags = entity.Property(x => x.Tags).HasColumnName("TagsJson").HasColumnType("text")
            .HasConversion(value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                value => ReadTags(value));
        tags.Metadata.SetValueComparer(new ValueComparer<List<string>>(
            (left, right) => left!.SequenceEqual(right!), value => value.Aggregate(0, (hash, tag) => HashCode.Combine(hash, tag)), value => value.ToList()));
    }
}

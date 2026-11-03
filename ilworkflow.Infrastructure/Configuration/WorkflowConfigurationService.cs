using System.Security.Cryptography;
using System.Text;
using Elsa.Common.Models;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Entities;
using Elsa.Workflows.Management.Filters;
using FluentValidation;
using IlWorkflow.Infrastructure.Persistence;
using IlWorkflow.Api.Workflows;
using Microsoft.EntityFrameworkCore;

namespace IlWorkflow.Infrastructure.Configuration;

/// <summary>A small facade over Elsa's draft/publish lifecycle; not another ticket-type lifecycle.</summary>
public sealed class WorkflowConfigurationService(ApplicationDbContext db, IWorkflowDefinitionPublisher publisher,
    IWorkflowDefinitionStore definitions, WorkflowDefinitionBuilder builder, WorkflowConfigurationValidator validator) : IWorkflowConfigurationService
{
    public static string Hash(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    public async Task<WorkflowConfiguration> GetAsync(long id, CancellationToken ct) =>
        await db.WorkflowConfigurations.SingleOrDefaultAsync(x => x.Id == id && x.TenantId == "", ct) ?? throw new ApiException(404, "Configuration not found.");

    /// <summary>POST without an ID creates; positive IDs update an editable draft using ExpectedRevision.</summary>
    public async Task<WorkflowConfiguration> SaveAsync(SaveConfigurationRequest request, CancellationToken ct)
    {
        if (request.Id is < 0) throw new ApiException(400, "ID cannot be negative.");
        if (request.Configuration is null) throw new ApiException(400, "Configuration is required.");
        if (request.DefinitionId is not null) await new DefinitionIdValidator().ValidateAndThrowAsync(request.DefinitionId, ct);
        // Named definitions use the preconfigured active field master, never client-supplied attributes.
        // Old callers without definition_id retain their existing inline-schema contract.
        var setup = request.Configuration;
        if (request.DefinitionId is not null)
            setup = setup with { Attributes = await ActiveAttributesAsync(request.DefinitionId, ct) };
        if (request.Id is > 0 && request.DefinitionId is null)
        {
            var current = await GetAsync(request.Id.Value, ct);
            if (current.UsesAttributeCatalog) setup = setup with { Attributes = await ActiveAttributesAsync(current.DefinitionId, ct) };
        }
        await validator.ValidateAndThrowAsync(setup, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        WorkflowConfiguration row;
        WorkflowDefinition definition;
        if (request.Id is null or 0)
        {
            if (request.DefinitionId is not null)
            {
                await LockAsync(request.DefinitionId, ct);
                if (await definitions.FindAsync(new WorkflowDefinitionFilter { DefinitionId = request.DefinitionId, VersionOptions = VersionOptions.Latest }, ct) is not null)
                    throw new ApiException(409, "Definition ID already exists. Update its draft or create its next version.");
                definition = await publisher.NewAsync(cancellationToken: ct);
                definition.DefinitionId = request.DefinitionId;
            }
            else definition = await publisher.NewAsync(cancellationToken: ct);
            row = new() { DefinitionId = definition.DefinitionId, DefinitionVersionId = definition.Id, DefinitionVersion = definition.Version, UsesAttributeCatalog = request.DefinitionId is not null };
            db.WorkflowConfigurations.Add(row);
        }
        else
        {
            row = await GetAsync(request.Id.Value, ct);
            if (request.DefinitionId is not null && request.DefinitionId != row.DefinitionId)
                throw new ApiException(400, "Definition ID cannot be changed on an existing configuration.");
            await LockAsync(row.DefinitionId, ct);
            await db.Entry(row).ReloadAsync(ct);
            CheckRevision(row, request.ExpectedRevision);
            if (row.Frozen) throw new ApiException(409, "Published snapshots cannot be edited. Create the next draft.");
            definition = await definitions.FindAsync(new WorkflowDefinitionFilter { Id = row.DefinitionVersionId }, ct) ?? throw new ApiException(409, "Elsa draft is missing.");
            if (definition.IsPublished) throw new ApiException(409, "Elsa version is already published.");
            row.Revision++;
        }
        row.ConfigurationJson = ConfigurationJson.Write(setup);
        row.Ready = false;
        ApplySetup(definition, setup);
        await publisher.SaveDraftAsync(definition, ct);
        row.DefinitionVersion = definition.Version;
        var storedDefinition = await definitions.FindAsync(new WorkflowDefinitionFilter { Id = definition.Id }, ct);
        row.DefinitionHash = Hash(storedDefinition!.StringData ?? "");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return row;
    }
    private async Task<AttributeDefinition[]> ActiveAttributesAsync(string definitionId, CancellationToken ct) =>
        (await db.WorkflowAttributes.AsNoTracking().Where(x => x.DefinitionId == definitionId && x.Status == AttributeStatus.Active)
            .OrderBy(x => x.Id).ToArrayAsync(ct)).Select(x => x.ToDefinition()).ToArray();

    public async Task ValidateAsync(long id, CancellationToken ct) => await validator.ValidateAndThrowAsync(ConfigurationJson.Read<WorkflowSetup>((await GetAsync(id, ct)).ConfigurationJson), ct);

    /// <summary>Ready is committed only after native publication succeeds. Failed partial publication cannot execute.</summary>
    public async Task<WorkflowConfiguration> PublishAsync(long id, long expectedRevision, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = await GetAsync(id, ct);
        await LockAsync(row.DefinitionId, ct);
        await db.Entry(row).ReloadAsync(ct);
        CheckRevision(row, expectedRevision);
        if (row.Ready) return row;
        await validator.ValidateAndThrowAsync(ConfigurationJson.Read<WorkflowSetup>(row.ConfigurationJson), ct);
        var definition = await definitions.FindAsync(new WorkflowDefinitionFilter { Id = row.DefinitionVersionId }, ct) ?? throw new ApiException(409, "Elsa draft is missing.");
        if (Hash(definition.StringData ?? "") != row.DefinitionHash) throw new ApiException(409, "The generated Elsa draft changed outside configuration APIs. Save the draft again.");
        var result = await publisher.PublishAsync(definition, ct);
        if (!result.Succeeded) throw new ApiException(400, "Elsa rejected the generated definition: " + string.Join("; ", result.ValidationErrors.Select(x => x.Message)));
        var published = await definitions.FindAsync(new WorkflowDefinitionFilter { Id = definition.Id }, ct);
        row.DefinitionHash = Hash(published!.StringData ?? "");
        // Never reset Frozen after Elsa retracts this version when a newer version is published.
        // The snapshot may still be referenced by a long-running instance.
        row.Frozen = true;
        row.Ready = true;
        row.Revision++;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return row;
    }
    public async Task<WorkflowConfiguration> CreateNextDraftAsync(long id, long expectedRevision, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var source = await GetAsync(id, ct);
        await LockAsync(source.DefinitionId, ct);
        await db.Entry(source).ReloadAsync(ct);
        CheckRevision(source, expectedRevision);
        if (!source.Ready) throw new ApiException(409, "Publish the source version first.");
        var existing = await db.WorkflowConfigurations.FirstOrDefaultAsync(x => x.DefinitionId == source.DefinitionId && !x.Frozen, ct);
        if (existing is not null) return existing;
        var draft = await publisher.GetDraftAsync(source.DefinitionId, VersionOptions.Published, ct) ?? throw new ApiException(409, "Published definition is missing.");
        // Clone the active published native version, even when the requested source snapshot is historical.
        // Rebuild from that source's configuration so its schema and generated graph remain aligned.
        var setup = ConfigurationJson.Read<WorkflowSetup>(source.ConfigurationJson);
        if (source.UsesAttributeCatalog) setup = setup with { Attributes = await ActiveAttributesAsync(source.DefinitionId, ct) };
        await validator.ValidateAndThrowAsync(setup, ct);
        ApplySetup(draft, setup);
        await publisher.SaveDraftAsync(draft, ct);
        var storedDraft = await definitions.FindAsync(new WorkflowDefinitionFilter { Id = draft.Id }, ct);
        var row = new WorkflowConfiguration { DefinitionId = draft.DefinitionId, DefinitionVersionId = draft.Id,
            DefinitionVersion = draft.Version, ConfigurationJson = ConfigurationJson.Write(setup), UsesAttributeCatalog = source.UsesAttributeCatalog, DefinitionHash = Hash(storedDraft!.StringData ?? "") };
        db.WorkflowConfigurations.Add(row);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return row;
    }
    /// <summary>Keep graph, input metadata and outputs identical when saving or cloning a configuration.</summary>
    private void ApplySetup(WorkflowDefinition definition, WorkflowSetup setup)
    {
        definition.Name = setup.Name;
        definition.StringData = builder.Build(setup);
        definition.MaterializerName = "Json";
        // Native execute accepts the complete object; conditional fields are also described as inputs.
        var conditionalKeys = setup.Stages.SelectMany(stage =>
            (stage.ApproveRules ?? []).Select(rule => rule.When.Attribute)
                .Concat((stage.Assignment.Rules ?? []).Select(rule => rule.When.Attribute))).ToHashSet();
        definition.Inputs = new List<Elsa.Workflows.Models.InputDefinition>
        {
            new() { Name = "attributes", Type = typeof(object) }, new() { Name = "requesterId", Type = typeof(string) }
        };
        foreach (var field in setup.Attributes.Where(field => conditionalKeys.Contains(field.Key)))
            definition.Inputs.Add(new() { Name = field.Key, Type = field.Type switch
            {
                AttributeType.Decimal => typeof(decimal), AttributeType.Integer => typeof(long), AttributeType.Boolean => typeof(bool), _ => typeof(string)
            } });
        definition.Outputs = new List<Elsa.Workflows.Models.OutputDefinition>
        {
            new() { Name = "ticketOutcome", Type = typeof(string) }, new() { Name = "requesterId", Type = typeof(string) },
            new() { Name = "attributes", Type = typeof(object) }, new() { Name = "attributeRevision", Type = typeof(int) },
            new() { Name = "attributeHistory", Type = typeof(object) }, new() { Name = "currentStage", Type = typeof(string) }
        };
    }

    // The transaction owns this per-definition lock across application instances, not just threads in one process.
    // Native Elsa contexts commit independently; Ready + hash checks are the admission gate, not a distributed transaction.
    private Task<int> LockAsync(string definitionId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({definitionId}, 0))", ct);
    private static void CheckRevision(WorkflowConfiguration row, long? revision)
    {
        if (revision != row.Revision) throw new ApiException(409, "ExpectedRevision must match the current configuration revision.");
    }
}

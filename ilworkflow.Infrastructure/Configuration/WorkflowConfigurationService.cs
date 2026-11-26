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
    public async Task<WorkflowConfiguration> GetAsync(string workflowDefinitionId, int workflowVersion, CancellationToken ct) =>
        await db.WorkflowConfigurations.SingleOrDefaultAsync(x => x.TenantId == "" && x.DefinitionId == workflowDefinitionId
            && x.DefinitionVersion == workflowVersion, ct) ?? throw new ApiException(404, "Configuration version not found.");

    /// <summary>
    /// One write path. A version number updates that draft; otherwise a new version is created. Without a definition ID the
    /// definition is new too. Revision is internal: it grows on every successful save and publish and is never sent by clients.
    /// </summary>
    public async Task<WorkflowConfiguration> SaveAsync(SaveConfigurationRequest request, CancellationToken ct)
    {
        if (request.Configuration is null) throw new ApiException(400, "Configuration is required.");
        var definitionId = Blank(request.WorkflowDefinitionId);
        var version = request.WorkflowVersion;
        if (version is not null && definitionId is null)
            throw new ApiException(400, "workflow_definition_id is required when workflow_version is supplied.");
        if (definitionId is not null) await new DefinitionIdValidator().ValidateAndThrowAsync(definitionId, ct);
        var input = request.Configuration;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var (row, definition, setup) = version is not null
            ? await OpenDraftAsync(definitionId!, version.Value, input, ct)
            : await CreateVersionAsync(definitionId, input, ct);
        row.UsesAttributeCatalog = true;
        row.ConfigurationJson = ConfigurationJson.Write(setup);
        row.Ready = false;
        ApplySetup(definition, setup);
        await publisher.SaveDraftAsync(definition, ct);
        // Elsa owns these identifiers; copy them only after the draft is stored so a new version carries its real ID.
        row.DefinitionId = definition.DefinitionId;
        row.DefinitionVersionId = definition.Id;
        row.DefinitionVersion = definition.Version;
        var storedDefinition = await definitions.FindAsync(new WorkflowDefinitionFilter { Id = definition.Id }, ct);
        row.DefinitionHash = Hash(storedDefinition!.StringData ?? "");
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return row;
    }

    /// <summary>Update an existing draft version. Published or frozen versions are immutable.</summary>
    private async Task<(WorkflowConfiguration Row, WorkflowDefinition Definition, WorkflowSetup Setup)> OpenDraftAsync(
        string definitionId, int version, WorkflowSetupRequest input, CancellationToken ct)
    {
        var row = await GetAsync(definitionId, version, ct);
        await LockAsync(row.DefinitionId, ct);
        await db.Entry(row).ReloadAsync(ct);
        if (row.Frozen) throw new ApiException(409, "Published versions cannot be edited. Omit workflow_version to create the next version.");
        var definition = await definitions.FindAsync(new WorkflowDefinitionFilter { Id = row.DefinitionVersionId }, ct) ?? throw new ApiException(409, "Elsa draft is missing.");
        if (definition.IsPublished) throw new ApiException(409, "Elsa version is already published.");
        row.Revision++;
        return (row, definition, await BuildSetupAsync(row.DefinitionId, input, ct));
    }

    /// <summary>Create a definition (generated or caller-chosen ID) or the next version of an existing one.</summary>
    private async Task<(WorkflowConfiguration Row, WorkflowDefinition Definition, WorkflowSetup Setup)> CreateVersionAsync(
        string? definitionId, WorkflowSetupRequest input, CancellationToken ct)
    {
        WorkflowDefinition definition;
        if (definitionId is not null)
        {
            await LockAsync(definitionId, ct);
            var latest = await definitions.FindAsync(new WorkflowDefinitionFilter { DefinitionId = definitionId, VersionOptions = VersionOptions.Latest }, ct);
            if (latest is not null)
            {
                var open = await db.WorkflowConfigurations.FirstOrDefaultAsync(x => x.TenantId == "" && x.DefinitionId == definitionId && !x.Frozen, ct);
                if (open is not null)
                    throw new ApiException(409, $"Draft version {open.DefinitionVersion} is still open. Send its workflow_version to update it, or publish it first.");
                if (!await db.WorkflowConfigurations.AnyAsync(x => x.TenantId == "" && x.DefinitionId == definitionId, ct))
                    throw new ApiException(409, "This definition exists in Elsa but was not created through workflow configurations.");
                // Validate before Elsa creates the draft so a rejected request cannot leave an orphan version behind.
                var nextSetup = await BuildSetupAsync(definitionId, input, ct);
                definition = await publisher.GetDraftAsync(definitionId, VersionOptions.Published, ct) ?? throw new ApiException(409, "Published definition is missing.");
                return (NewRow(definition), definition, nextSetup);
            }
        }
        definition = await publisher.NewAsync(cancellationToken: ct);
        if (definitionId is not null) definition.DefinitionId = definitionId;
        return (NewRow(definition), definition, await BuildSetupAsync(definition.DefinitionId, input, ct));
    }

    private WorkflowConfiguration NewRow(WorkflowDefinition definition)
    {
        var row = new WorkflowConfiguration { DefinitionId = definition.DefinitionId, DefinitionVersionId = definition.Id, DefinitionVersion = definition.Version };
        db.WorkflowConfigurations.Add(row);
        return row;
    }

    private async Task<WorkflowSetup> BuildSetupAsync(string definitionId, WorkflowSetupRequest input, CancellationToken ct)
    {
        var setup = await MapConditionalAttributesAsync(definitionId, new WorkflowSetup(input.Name, [], input.StartStage, input.Stages, input.Supervisors), ct);
        await validator.ValidateAndThrowAsync(setup, ct);
        return setup;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private async Task<WorkflowSetup> MapConditionalAttributesAsync(string definitionId, WorkflowSetup setup, CancellationToken ct)
    {
        // Collect keys from both kinds of conditions. Leave malformed rules to FluentValidation.
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stage in setup.Stages ?? [])
        {
            foreach (var action in stage?.Actions ?? [])
                foreach (var rule in action?.Rules ?? [])
                    foreach (var condition in rule?.Conditions ?? [])
                        if (condition?.Attribute is { } key) keys.Add(key);
            foreach (var rule in stage?.Assignment?.Rules ?? [])
                foreach (var condition in rule?.Conditions ?? [])
                    if (condition?.Attribute is { } key) keys.Add(key);
        }

        if (keys.Count == 0) return setup with { Attributes = [] };

        var fields = await db.WorkflowAttributes.AsNoTracking()
            .Where(x => x.DefinitionId == definitionId && x.Status == AttributeStatus.Active && keys.Contains(x.Key))
            .OrderBy(x => x.Id).ToArrayAsync(ct);
        return setup with { Attributes = fields.Select(x => x.ToDefinition() with { Required = true }).ToArray() };
    }

    public async Task ValidateAsync(string workflowDefinitionId, int workflowVersion, CancellationToken ct) =>
        await validator.ValidateAndThrowAsync(ConfigurationJson.Read<WorkflowSetup>((await GetAsync(workflowDefinitionId, workflowVersion, ct)).ConfigurationJson), ct);

    /// <summary>Ready is committed only after native publication succeeds. Failed partial publication cannot execute.</summary>
    public async Task<WorkflowConfiguration> PublishAsync(string workflowDefinitionId, int workflowVersion, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = await GetAsync(workflowDefinitionId, workflowVersion, ct);
        await LockAsync(row.DefinitionId, ct);
        await db.Entry(row).ReloadAsync(ct);
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
    /// <summary>Keep graph, input metadata and outputs identical when saving or cloning a configuration.</summary>
    private void ApplySetup(WorkflowDefinition definition, WorkflowSetup setup)
    {
        definition.Name = setup.Name;
        definition.StringData = builder.Build(setup);
        definition.MaterializerName = "Json";
        // The snapshot contains only mapped condition fields. Native execute also accepts them in one object.
        definition.Inputs = new List<Elsa.Workflows.Models.InputDefinition>
        {
            new() { Name = "attributes", Type = typeof(object) }, new() { Name = "requesterId", Type = typeof(string) }
        };
        foreach (var field in setup.Attributes)
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
}

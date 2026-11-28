using System.Security.Cryptography;
using System.Text;
using Elsa.Common.Models;
using Elsa.Workflows.Management;
using Elsa.Workflows.Management.Entities;
using Elsa.Workflows.Management.Filters;
using Elsa.Workflows.Models;
using FluentValidation;
using IlWorkflow.Infrastructure.Persistence;
using IlWorkflow.Infrastructure.Workflows;
using Microsoft.EntityFrameworkCore;

namespace IlWorkflow.Infrastructure.Configuration;

/// <summary>
/// Saves, validates and publishes workflow configurations. Elsa owns drafts, versions and publishing;
/// this service keeps one <see cref="WorkflowConfiguration"/> row next to each Elsa version.
///
/// Save request rules:
/// <list type="bullet">
/// <item>No workflow_definition_id: create a new definition (Elsa generates the ID) at version 1.</item>
/// <item>workflow_definition_id only: create that definition (409 if it exists).</item>
/// <item>workflow_definition_id + workflow_version: update that draft (published versions are read-only).</item>
/// </list>
/// </summary>
public sealed class WorkflowConfigurationService(
    ApplicationDbContext db,
    IWorkflowDefinitionPublisher publisher,
    IWorkflowDefinitionStore definitions,
    WorkflowDefinitionBuilder builder,
    WorkflowConfigurationValidator validator) : IWorkflowConfigurationService
{
    public async Task<WorkflowConfiguration> GetAsync(string workflowDefinitionId, int? workflowVersion, CancellationToken ct)
    {
        var versions = db.WorkflowConfigurations.Where(x => x.TenantId == "" && x.DefinitionId == workflowDefinitionId);
        var row = workflowVersion is { } version
            ? await versions.SingleOrDefaultAsync(x => x.DefinitionVersion == version, ct)
            : await versions.OrderByDescending(x => x.DefinitionVersion).FirstOrDefaultAsync(ct);
        return row ?? throw new ApiException(404, "Configuration version not found.");
    }

    public async Task<WorkflowConfiguration> SaveAsync(SaveConfigurationRequest request, CancellationToken ct)
    {
        await new SaveConfigurationRequestValidator().ValidateAndThrowAsync(request, ct);
        var definitionId = string.IsNullOrWhiteSpace(request.WorkflowDefinitionId) ? null : request.WorkflowDefinitionId;
        var setup = request.Configuration.ToSetup();
        // Validate before touching Elsa, so a bad request never leaves an orphan draft behind.
        await validator.ValidateAndThrowAsync(setup, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var (row, definition) = request.WorkflowVersion is { } version
            ? await OpenDraftAsync(definitionId!, version, ct)
            : await CreateDefinitionAsync(definitionId, ct);

        definition.Name = setup.Name;
        definition.MaterializerName = "Json";
        definition.StringData = builder.Build(setup);
        definition.Inputs = [new() { Name = TicketState.Attributes, Type = typeof(object) }, new() { Name = TicketState.Requester, Type = typeof(string) }];
        definition.Outputs = TicketOutputs();
        await publisher.SaveDraftAsync(definition, ct);

        // Elsa assigns these when the draft is stored, so copy them afterwards.
        row.DefinitionId = definition.DefinitionId;
        row.DefinitionVersionId = definition.Id;
        row.DefinitionVersion = definition.Version;
        row.ConfigurationJson = ConfigurationJson.Write(setup);
        row.DefinitionHash = await StoredHashAsync(definition.Id, ct);
        row.Ready = false;

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return row;
    }

    public async Task ValidateAsync(string workflowDefinitionId, int workflowVersion, CancellationToken ct)
    {
        var row = await GetAsync(workflowDefinitionId, workflowVersion, ct);
        await validator.ValidateAndThrowAsync(ConfigurationJson.Read<WorkflowSetup>(row.ConfigurationJson), ct);
    }

    /// <summary>Publishes through Elsa. The row becomes Ready (tickets may start) only after Elsa succeeds.</summary>
    public async Task<WorkflowConfiguration> PublishAsync(string workflowDefinitionId, int workflowVersion, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = await GetAsync(workflowDefinitionId, workflowVersion, ct);
        await LockDefinitionAsync(row.DefinitionId, ct);
        await db.Entry(row).ReloadAsync(ct);
        if (row.Ready) return row; // already published: publishing twice is harmless

        // Users or roles may have changed since the draft was saved.
        await validator.ValidateAndThrowAsync(ConfigurationJson.Read<WorkflowSetup>(row.ConfigurationJson), ct);

        var definition = await FindVersionAsync(row.DefinitionVersionId, ct);
        if (Hash(definition.StringData) != row.DefinitionHash)
            throw new ApiException(409, "The Elsa draft was changed outside this API. Save the configuration again.");

        var result = await publisher.PublishAsync(definition, ct);
        if (!result.Succeeded)
            throw new ApiException(400, "Elsa rejected the definition: " + string.Join("; ", result.ValidationErrors.Select(error => error.Message)));

        row.DefinitionHash = await StoredHashAsync(definition.Id, ct);
        row.Frozen = true; // never reset: running tickets may still use this version
        row.Ready = true;
        row.Revision++;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return row;
    }

    /// <summary>SHA-256 of the generated Elsa JSON. Used to detect edits made outside this API.</summary>
    public static string Hash(string? elsaJson) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(elsaJson ?? "")));

    private async Task<(WorkflowConfiguration Row, WorkflowDefinition Definition)> OpenDraftAsync(string definitionId, int version, CancellationToken ct)
    {
        var row = await GetAsync(definitionId, version, ct);
        await LockDefinitionAsync(row.DefinitionId, ct);
        await db.Entry(row).ReloadAsync(ct); // re-read inside the lock

        if (row.Frozen)
            throw new ApiException(409, "Published versions cannot be edited.");
        var definition = await FindVersionAsync(row.DefinitionVersionId, ct);
        if (definition.IsPublished)
            throw new ApiException(409, "This Elsa version is already published.");

        row.Revision++;
        return (row, definition);
    }

    private async Task<(WorkflowConfiguration Row, WorkflowDefinition Definition)> CreateDefinitionAsync(string? definitionId, CancellationToken ct)
    {
        if (definitionId is not null)
        {
            await LockDefinitionAsync(definitionId, ct);
            if (await DefinitionExistsAsync(definitionId, ct))
                throw new ApiException(409, "Definition already exists. Send workflow_version to update a draft.");
        }

        var definition = await publisher.NewAsync(cancellationToken: ct);
        if (definitionId is not null) definition.DefinitionId = definitionId;
        return (AddRow(definition), definition);
    }

    private WorkflowConfiguration AddRow(WorkflowDefinition definition)
    {
        var row = new WorkflowConfiguration { DefinitionId = definition.DefinitionId, DefinitionVersionId = definition.Id, DefinitionVersion = definition.Version };
        db.WorkflowConfigurations.Add(row);
        return row;
    }

    private async Task<bool> DefinitionExistsAsync(string definitionId, CancellationToken ct) =>
        await definitions.FindAsync(new WorkflowDefinitionFilter { DefinitionId = definitionId, VersionOptions = VersionOptions.Latest }, ct) is not null;

    private async Task<WorkflowDefinition> FindVersionAsync(string versionId, CancellationToken ct) =>
        await definitions.FindAsync(new WorkflowDefinitionFilter { Id = versionId }, ct) ?? throw new ApiException(409, "The Elsa draft is missing.");

    private async Task<string> StoredHashAsync(string versionId, CancellationToken ct) => Hash((await FindVersionAsync(versionId, ct)).StringData);

    /// <summary>Workflow outputs a client can read from the instance (see <see cref="TicketState"/>).</summary>
    private static List<OutputDefinition> TicketOutputs() =>
    [
        new() { Name = TicketState.Outcome, Type = typeof(string) },
        new() { Name = TicketState.Requester, Type = typeof(string) },
        new() { Name = TicketState.CurrentStage, Type = typeof(string) },
        new() { Name = TicketState.Attributes, Type = typeof(object) },
        new() { Name = TicketState.AttributeRevision, Type = typeof(int) },
        new() { Name = TicketState.AttributeHistory, Type = typeof(object) }
    ];

    /// <summary>
    /// PostgreSQL advisory lock held until the transaction ends. Two saves of the same definition (even on
    /// different servers) run one after the other instead of racing.
    /// </summary>
    private Task<int> LockDefinitionAsync(string definitionId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({definitionId}, 0))", ct);
}

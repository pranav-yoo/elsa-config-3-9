using FluentValidation;
using IlWorkflow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
namespace IlWorkflow.Infrastructure.Configuration;

public sealed class WorkflowAttributeService(ApplicationDbContext db) : IWorkflowAttributeService
{
    public async Task<IReadOnlyList<WorkflowAttribute>> ListAsync(string definitionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(definitionId)) throw new ApiException(400, "Definition ID is required.");
        await new DefinitionIdValidator().ValidateAndThrowAsync(definitionId, ct);
        return await db.WorkflowAttributes.AsNoTracking().Where(x => x.DefinitionId == definitionId).OrderBy(x => x.Id).ToArrayAsync(ct);
    }
    public async Task<WorkflowAttribute> SaveAsync(SaveWorkflowAttributeRequest request, CancellationToken ct)
    {
        await new SaveWorkflowAttributeValidator().ValidateAndThrowAsync(request, ct);
        var row = request.Id is null or 0 ? new WorkflowAttribute() :
            await db.WorkflowAttributes.SingleOrDefaultAsync(x => x.Id == request.Id, ct) ?? throw new ApiException(404, "Attribute not found.");
        if (row.Id != 0 && row.DefinitionId != request.DefinitionId) throw new ApiException(400, "An attribute cannot move to another definition.");
        if (await db.WorkflowAttributes.AnyAsync(x => x.DefinitionId == request.DefinitionId && x.Key == request.Attribute.Key && x.Id != row.Id, ct)) throw new ApiException(409, "An attribute with that key already exists for this definition.");
        row.DefinitionId = request.DefinitionId; row.Key = request.Attribute.Key; row.Type = request.Attribute.Type;
        row.Required = request.Attribute.Required; row.Choices = request.Attribute.Choices;
        row.MaxLength = request.Attribute.MaxLength; row.Minimum = request.Attribute.Minimum; row.Maximum = request.Attribute.Maximum;
        row.Status = request.Status;
        if (row.Id == 0) db.WorkflowAttributes.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }
}

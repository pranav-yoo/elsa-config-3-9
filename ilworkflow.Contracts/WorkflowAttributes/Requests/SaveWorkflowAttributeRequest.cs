namespace IlWorkflow.Contracts.WorkflowAttributes.Requests;

/// <summary>Create (missing/zero <c>Id</c>) or update (positive <c>Id</c>) one field of a definition's attribute catalog.</summary>
public sealed record SaveWorkflowAttributeRequest(long? Id, string DefinitionId, AttributeDefinition Attribute, AttributeStatus Status = AttributeStatus.Active);

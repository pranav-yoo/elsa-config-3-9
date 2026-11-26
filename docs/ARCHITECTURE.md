# Four-project architecture

HTTP contracts, workflow behavior and database schemas are preserved.

```text
API -> Application -> Domain
API -> Infrastructure -> Application / Domain
```

- **Domain:** WorkflowConfiguration.cs contains the entity, enums and value records. AttributeRules.cs contains exact comparisons and field constraints. No external package dependencies.
- **Contracts:** every HTTP request and response model, grouped by module (`WorkflowConfigurations`, `WorkflowAttributes`, `Tasks`). It references Domain for the shared value types (stages, attribute definitions) and has no Elsa, EF Core or ASP.NET dependency. Elsa's own native response types stay with Elsa.
- **Application:** Abstractions.cs contains JSON helpers, errors and the IWorkflowConfigurationService/IWorkflowAttributeService/IApproverDirectory ports. Validators enforce configuration/input/decision rules. AssignmentResolver selects eligible participants through the directory port. References Domain and FluentValidation only.
- **Infrastructure:** WorkflowConfigurationService implements the application port using EF and native Elsa publishing. Persistence contains the existing migration and provider fixes. Workflows contains graph generation, review activities and the form adapter. Tickets/TicketIdentity resolves approver roles from Elsa Identity. Authorization contains native participation checks.
- **API:** Controllers bind requests; middleware guards native execution. Services/WorkflowServiceRegistration.cs connects ports/adapters and registers Elsa/custom services. Program.cs owns startup, diagnostics, database initialization and the request pipeline.

No generic repository wrappers, mediator pipeline or replacement task tables were added. EF/Elsa-dependent orchestration stays behind the application service port.

Run `dotnet run --project ilworkflow.API --launch-profile http`. API owns settings, Samples and launch profiles. Root setup/token scripts now target that project. The user-secret ID remains ilworkflow-elsa-local.

Infrastructure deliberately keeps assembly name IlWorkflow.Api and the IlWorkflow.Api.Workflows/Tickets activity namespaces, because persisted Elsa state can refer to those CLR identities. The API host uses ilworkflow.API.Host. These compatibility names do not reverse project dependencies. Migration identifiers/schemas stay unchanged; moving files does not recreate data or rewrite published definitions.

README documents the live workflow, identity, request-file, preview and restart checks.

## Preconfigured attribute master

Domain `WorkflowAttribute` represents a field linked to a stable definition ID with an Active/Inactive status. Application `IWorkflowAttributeService` and shared FluentValidation validators define its contract. Infrastructure `WorkflowAttributeService` stores it in `Application.WorkflowAttributes`; API `WorkflowAttributesController` exposes configurator-only POST create/update and GET listing.

`SaveConfigurationRequest.workflow_definition_id` supplies the native Elsa family ID; `workflow_version_id` selects the draft to update, and omitting it creates a new version. The configuration adapter collects attribute keys from approval and assignment conditions. It snapshots the matching active fields for that definition and makes them mandatory. Draft saves and next-version creation refresh the snapshot. Published versions and their instances remain pinned.

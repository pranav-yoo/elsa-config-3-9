# Four-project architecture

HTTP contracts, workflow behavior and database schemas are preserved.

```text
API -> Application -> Domain
API -> Infrastructure -> Application / Domain
```

- **Domain:** WorkflowConfiguration.cs contains the entity, enums and value records. AttributeRules.cs contains exact comparisons and field constraints. No external package dependencies.
- **Contracts:** every HTTP request and response model, grouped by module (`WorkflowConfigurations`, `Tasks`), each with `Requests/` and `Responses/` folders and matching namespaces. It references Domain for the shared value types (stages, attribute definitions) and has no Elsa, EF Core or ASP.NET dependency. Elsa's own native response types stay with Elsa.
- **Application:** Abstractions.cs contains JSON helpers, errors and the IWorkflowConfigurationService/IApproverDirectory ports. Validators enforce configuration/input/decision rules. AssignmentResolver selects eligible participants through the directory port. References Domain and FluentValidation only.
- **Infrastructure:** WorkflowConfigurationService implements the application port using EF and native Elsa publishing. Folders: Authentication (user-token scheme), Authorization (permission checks, observer/supervisor access policy, instance read access), Configuration (the service), Identity (ApproverDirectory resolves approver roles from Elsa Identity), Persistence (DbContext, Migrations/, UserTasks/ provider fixes), Tasks (TaskService, observed/supervised query) and Workflows (graph builder, form adapter, parallel-task locking, Activities/).
- **API:** Controllers bind requests; middleware guards native execution. Services/WorkflowServiceRegistration.cs connects ports/adapters and registers Elsa/custom services. Program.cs owns startup, diagnostics, database initialization and the request pipeline.

No generic repository wrappers, mediator pipeline or replacement task tables were added. EF/Elsa-dependent orchestration stays behind the application service port.

Run `dotnet run --project ilworkflow.API --launch-profile http`. API owns settings, Samples and launch profiles. Root setup/token scripts now target that project. The user-secret ID remains ilworkflow-elsa-local.

Infrastructure deliberately keeps assembly name IlWorkflow.Api and the IlWorkflow.Api.Workflows namespace (every type under Workflows/, including Activities/), because persisted Elsa state can refer to those CLR identities. The API host uses ilworkflow.API.Host. These compatibility names do not reverse project dependencies. Migration identifiers/schemas stay unchanged; moving files does not recreate data or rewrite published definitions.

README documents the live workflow, identity, request-file, preview and restart checks.

## Configuration versions

`SaveConfigurationRequest.workflow_definition_id` supplies the native Elsa family ID; `workflow_version` selects the draft to update, and omitting it creates a new version. Attributes are not configurable for now: every snapshot stores an empty list. Published versions and their instances remain pinned.

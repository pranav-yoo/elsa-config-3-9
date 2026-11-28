# Four-project architecture

```text
API -> Infrastructure -> Core -> Contracts
API -> Core, Contracts
```

- **Contracts:** the public shape of the API, with no dependencies. One folder per feature: `WorkflowConfigurations/` (`Model/` with WorkflowSetup, StageDefinition, StageAction, Destination, AssignmentDefinition, TaskActionKeys and enums; `Requests/`; `Responses/`), `Tasks/Requests/` and `Common/` (`Result<T>`). The workflow model lives here because it appears inside requests and responses, and Core's validators use those requests: keeping it in Core would make the two projects reference each other. Elsa's own native response types stay with Elsa.
- **Core:** the business rules, with no Elsa, EF Core or ASP.NET dependency (Contracts, FluentValidation and DI abstractions only):
  - `WorkflowConfigurations/`: `Validators/`, the `WorkflowConfiguration` database row and `IWorkflowConfigurationService`.
  - `Tasks/`: `TaskDecisionValidator`, `AssignmentResolver`, `IApproverDirectory`.
  - `Tickets/`: `TicketOutcome`, `ExecuteRequestValidator`.
  - `Common/`: `ApiException`, `ConfigurationJson`.
  - `DependencyInjection.cs`: `AddCore()`.
- **Infrastructure:** WorkflowConfigurationService implements the Core interface using EF and native Elsa publishing. `DependencyInjection.cs` (`AddInfrastructure()`) registers Elsa, the database and the task services. Folders: Authentication (user-token scheme), Authorization (permission checks, observer/supervisor access policy, instance read access), Configuration (the service), Identity (ApproverDirectory resolves approver roles from Elsa Identity), Persistence (DbContext, Configurations/ table mappings, Migrations/, UserTasks/ provider fixes), Tasks (TaskService, observed/supervised query) and Workflows (graph builder, form adapter, parallel-task locking, Activities/).
- **API:** Controllers, Common (ApiResultHelper builds every Result<T> response), Extensions (`AddApi()` for controllers/JSON/policies, startup helpers), Middleware (ExceptionHandlingMiddleware, WorkflowRequestGuard). Program.cs calls `AddCore()`, `AddInfrastructure()` and `AddApi()`, then builds the request pipeline.

Why Contracts and Core, but no separate Domain: the model is a configuration shape without behavior of its own, so it lives with the contracts. The boundary that matters, Core and Contracts never referencing Elsa or EF Core, is enforced by the compiler. Split the model into its own project if it gains real business rules.

No generic repository wrappers, mediator pipeline or replacement task tables were added. EF/Elsa-dependent orchestration stays behind the application service port.

Run `dotnet run --project ilworkflow.API --launch-profile http`. API owns settings, Samples and launch profiles. Root setup/token scripts now target that project. The user-secret ID remains ilworkflow-elsa-local.

Infrastructure uses namespace IlWorkflow.Infrastructure.* (assembly ilworkflow.Infrastructure). Elsa identifies the custom activities by their `[Activity]` type names (`Tickets.TicketReview`, ...), not by CLR namespace. Migration identifiers/schemas stay unchanged.

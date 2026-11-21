# Configurable ticket approvals with Elsa 3.9

Open `ilworkflow.slnx` in Visual Studio. Set **ilworkflow.API** as the startup project.

| Project | Responsibility |
|---|---|
| ilworkflow.API | Program.cs, service registration, controllers, middleware, Swagger and settings |
| ilworkflow.Application | Application contracts, FluentValidation, task validation and assignment policies |
| ilworkflow.Domain | Configuration entity, enums, value records and exact attribute comparisons |
| ilworkflow.Infrastructure | PostgreSQL/EF, Elsa workflows/stores and identity adapters |

Application depends on Domain. Infrastructure depends on Application/Domain. API composes them. Domain and Application have no Elsa or EF Core dependencies. See [architecture notes](docs/ARCHITECTURE.md) and [developer guide](docs/DEVELOPER-GUIDE.md).

## Start

```powershell
dotnet restore ilworkflow.slnx
dotnet build ilworkflow.slnx
# First setup only:
pwsh -NoProfile -ExecutionPolicy Bypass -File ./Setup.ps1
dotnet run --project ilworkflow.API --launch-profile http
```

Open http://localhost:5000/swagger. API owns appsettings, launch profiles and Samples. Custom composition is in `ilworkflow.API/Services/WorkflowServiceRegistration.cs`.

Development uses PostgreSQL localhost:5432, database ilworkflow, account postgres. Credentials/signing keys belong in API user secrets. The existing UserSecretsId `ilworkflow-elsa-local` is unchanged. Restart does not reset persisted passwords. Packages remain pinned to Elsa 3.9.0.

For documentation without PostgreSQL: `dotnet run --project ilworkflow.API --launch-profile http -- --SwaggerPreview true`. Preview skips migrations and returns 503 for business operations.

## Ordered requests

Run requests one at a time; use named responses within a file and copy marked IDs between files.

1. [Authentication.http](Authentication.http): native local roles/users and login. Sample accounts use the configured- prefix and Password123! password. Inspect existing users/roles before creating records.
2. [Workflow-Configuration.http](Workflow-Configuration.http): create/update draft, validate, publish and create next version.
3. [Ticket-Workflow.http](Ticket-Workflow.http): internal submission, A/B/C/D approvals, rejection/correction, history and reassignment.
4. [Attribute-Catalog.http](Attribute-Catalog.http): configure fields first, then create a workflow with your own `definition_id`.

### Definition IDs and preconfigured attributes

`POST /api/workflow-attributes` creates a master field (missing/zero `id`) or updates one (positive `id`). Each row belongs to a `definitionId` and has `status: Active` or `Inactive`. `GET /api/workflow-attributes?definitionId=expense-claim-v1` lists that family's master records.

Pass `definition_id` in `POST /api/workflow-configurations` to create Elsa's workflow with that exact ID. Supply the attribute key in `when.attribute` for an approval route or conditional assignment. The server maps those keys to active rows from `Application.WorkflowAttributes` under the same definition ID. Missing, inactive or other-definition references return 400. Each mapped field is mandatory. An existing definition ID returns 409. Definition IDs cannot change on updates.

The configuration request contains the name, start stage and stages. The response includes the resolved attribute snapshot. Draft saves and next-draft creation resolve the current conditional fields. Publishing freezes that snapshot for running instances. Workflows without conditions have an empty attribute snapshot. Send the mapped values in `input.attributes` at execution and in `data.attributes` when resubmitting corrections. Your application can store its section and subsection data separately.

The attribute table and catalog-source flag are installed by migration `202611020001_WorkflowAttributes` on persistent startup. Run `python Tests/AttributeCatalog.py --base-url http://localhost:5000` to verify the named-definition flow and native approval.

POST /api/workflow-configurations creates for absent/null/zero id; positive IDs update drafts with expectedRevision. Published snapshots are immutable. Stale revisions return 409; unknown positive IDs return 404. Execution, tasks, instances and history use native Elsa APIs. No custom PUT or /api/tickets endpoint exists.

Above 5000, approval passes through C; 5000 and below bypass C. Rejection: A returns to correction/A; B returns to A; C ends Rejected; D returns to correction/D. Resubmission uses the same instance and preserves attribute revisions. Read workflowState.output.ticketOutcome for Approved/Rejected; technical Finished is not a business outcome. Poll after 202 completion. Reuse the original operationId/body/revision when retrying one command.

New review tasks expose `APPROVE` and `REJECT`. Correction tasks expose `RESUBMIT`. Use the exact `actions[].key` returned by task detail when sending `actionKey`, including for older pending tasks.

## Parallel approvals

Add `"parallelWith": ["D"]` to stage C. Routes into C open both C and D as separate Elsa tasks. The first approval follows C's approval route and cancels unanswered tasks. If every branch rejects, the group follows C's rejection route. Define C and D as regular stages with their own assignments and matching default destinations. Put shared approval rules on C and route to C to enter the group.

Run the [parallel approval requests](Parallel-Approvals.http) for each decision order. Use `python Tests/ParallelWorkflows.py` for the live PostgreSQL checks, then restart the API and repeat with `--verify-restart`.

## Read-only stage access

Add `"viewableByStages": ["D"]` to stage C. When C's task opens, the eligible members of D can see it immediately. Use `GET /elsa/api/user-tasks?scope=observed` to list tasks and the native detail and events endpoints to read them. The viewer grant follows that task into history. Use `allowedActions` to decide which controls to show. Observing a task grants no claim, completion, or management permission.

The observed list supports `workflowInstanceId`, `status`, `limit` (1–50), `cursor`, and `includeTotalCount`. Run [Observed-Tasks.http](Observed-Tasks.http) to test sequential read-only access. For an existing definition, publish a new version with the setting to apply it to new task snapshots.

## Users

All users are local Elsa Identity accounts and sign in with `POST /elsa/api/identity/login`. Stage `members`/`userId` hold the Elsa user ID returned when the user is created, not the username. Specific tasks use scope=assigned; buckets use scope=available and must be claimed.

## Persistence and verification

Elsa Identity EF stores accounts/roles. Management/runtime stores definitions/instances/bookmarks. User Tasks EF stores tasks, candidates, revisions and events. Application EF stores version-linked configuration and the preconfigured field master in its existing Application schema. The instance ID is the ticket reference; submitted attribute values/history/outcomes remain in Elsa.

Migration IDs, schemas and persisted activity identities are preserved. Existing immutable definitions require a new version to receive changed graph generation. Keep the native 3.9 PostgreSQL compatibility fixes; keyword task search returns explanatory 400, while scope/status/instance/reference filters remain available.

Use a research database; tests create records but do not save tokens.

```powershell
python Tests/ConfiguredWorkflows.py --base-url http://localhost:5000
python Tests/VerifyConfiguredRequests.py --base-url http://localhost:5000
python Tests/VerifyConfiguredRequests.py --base-url http://localhost:5000 --observed
# Restart API, then:
python Tests/ConfiguredWorkflows.py --base-url http://localhost:5000 --verify-restart
# In documentation preview instead:
python Tests/ConfiguredWorkflows.py --base-url http://localhost:5000 --verify-preview
```

Checks cover conditional routing, correction, exact comparisons, cycles, concurrency, permissions, version pinning and restart recovery.

All-approver and quorum policies, round robin, submission outbox, frontend, attachments, notifications, SLA escalation and external identity providers remain deferred.

# Configurable ticket approvals with Elsa 3.9

Open `ilworkflow.slnx` in Visual Studio. Set **ilworkflow.API** as the startup project.

| Project | Responsibility |
|---|---|
| ilworkflow.API | Program.cs, service registration, controllers, middleware, Swagger and settings |
| ilworkflow.Application | Ports (service interfaces), FluentValidation, task validation and assignment policies |
| ilworkflow.Contracts | HTTP request and response models, one folder per module (`WorkflowConfigurations`, `Tasks`), each split into `Requests/` and `Responses/` |
| ilworkflow.Domain | Configuration entity, enums, value records and exact attribute comparisons |
| ilworkflow.Infrastructure | PostgreSQL/EF, Elsa workflows/stores and identity adapters |

Contracts depends only on Domain value types. Application depends on Domain/Contracts. Infrastructure depends on Application/Contracts/Domain. API composes them. Add a new endpoint's request/response records to the matching Contracts module folder, not to a controller. Domain and Application have no Elsa or EF Core dependencies. See [architecture notes](docs/ARCHITECTURE.md) and [developer guide](docs/DEVELOPER-GUIDE.md).

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

### Definition IDs and attributes

Pass `workflow_definition_id` in `POST /api/workflow-configurations` to create Elsa's workflow with that exact ID. Sending an existing definition ID without `workflow_version` creates its next version, and returns 409 while another draft of that definition is still open.

Attributes are not configurable for now. The attribute catalog (`/api/workflow-attributes`) was removed, and every configuration is stored and returned with `"attributes": []`. Because no attribute exists, rule conditions are rejected with 400 and execution input must be `{ "attributes": {} }`. Migration `202611260001_DropWorkflowAttributes` drops the old table.

`POST /api/workflow-configurations` is the only write endpoint. Configurations are addressed by `workflow_definition_id` and `workflow_version` (Elsa's integer version number, 1, 2, 3, ...):

| Body | Result |
|---|---|
| `configuration` only | New definition with a generated `workflow_definition_id` and its first version. |
| `workflow_definition_id` + `configuration` | That definition if it does not exist yet, otherwise its **next version**. Returns 409 while a draft of it is still open. |
| `workflow_definition_id` + `workflow_version` + `configuration` | Updates that draft version. Unknown version returns 404; a published version returns 409. |

`workflow_version` without `workflow_definition_id` returns 400. The retired request fields `definition_id`, `id` and `expected_revision` also return 400 instead of being ignored. Read with `POST /api/workflow-configurations/get` and body `{ "workflow_definition_id": "...", "workflow_version": 2 }`; `workflow_version` is optional and, when omitted, returns the latest version. Validate and publish use `/api/workflow-configurations/{workflow_definition_id}/versions/{workflow_version}` (`POST .../validate`, `POST .../publish`). There is no `next-draft` route.

Responses return `workflow_definition_id`, `workflow_version`, `revision`, `is_published` and `configuration`. `revision` is an internal counter: it starts at 1 for each version, grows on every save and publish, and is never sent by clients. Published snapshots are immutable. Execution, tasks, instances and history use native Elsa APIs. No custom PUT or /api/tickets endpoint exists.

Above 5000, approval passes through C; 5000 and below bypass C. Rejection: A returns to correction/A; B returns to A; C ends Rejected; D returns to correction/D. Resubmission uses the same instance and preserves attribute revisions. Read workflowState.output.ticketOutcome for Approved/Rejected; technical Finished is not a business outcome. Poll after 202 completion. Reuse the original operationId/body/revision when retrying one command.

Review tasks expose the stage's `actions` (`APPROVE`, `REJECT` and any custom keys). Correction tasks expose `RESUBMIT`. Use the exact `actions[].key` returned by task detail when sending `actionKey`, including for older pending tasks.

### Assignment policies

- `Bucket`: every `members` entry sees the task and one of them claims it. With a single eligible member the task is assigned to that member directly (no claim).
- `Specific`: assigned directly to `userId`. With a single member, omit `userId`; that member is the owner. With several members, `userId` is required and must be one of them.
- `Condition`: the first matching rule's `userId` (one of `members`) is the owner; no match blocks the task for supervisor assignment.

### Response format (/api/*)

All application endpoints (`/api/tasks`, `/api/workflow-configurations`) return
`{ "trace_id": "...", "status_code": 200, "messages": ["..."], "data": { ... } }` (`Result<T>` in `ilworkflow.Contracts/SharedContracts`, built by `ApiResultHelper` in `ilworkflow.API/Common`), for success and errors (errors put `code` and, for validation, `errors[]` in `data`).
Elsa's native `/elsa/api/*` endpoints keep Elsa's format.
All `/api/*` property and query names are snake_case (`definition_id`, `start_stage`, `stage_key`, `or_group`, `require_reason`, `expected_revision`, `workflow_instance_id`, …). Ticket attribute names (e.g. `claimAmount`) and enum values (`Stage`, `Bucket`, `APPROVE`) are unchanged.

### Workflow supervisors

Add `"supervisors": ["<userId>", ...]` to the workflow (next to `name`, `start_stage`). Listed users see every task of that workflow (all stages, correction and blocked tasks) and can reassign them; they cannot claim or complete. They need the `workflow_supervisor` role (`user-tasks:view`, `user-tasks:assign`, `workflows/instances:view`, deliberately no `user-tasks:supervise`), so they see only the workflows that list them; see Authentication.http. The global `Supervisor` role still sees everything.

- List: `GET /elsa/api/user-tasks?scope=supervised` (filters: workflowInstanceId, status, cursor, limit, includeTotalCount).
- Detail/history: `GET /elsa/api/user-tasks/{id}`, `/events`, and `GET /elsa/api/workflow-instances/{instanceId}`.
- Reassign: `POST /elsa/api/user-tasks/{id}/assign` (native Elsa body).

The list is part of the published snapshot: tasks are tagged when created, so changing supervisors needs a new version and applies to new tasks.

### Stage actions (task buttons)

Each stage lists its task buttons in `actions`. `APPROVE` and `REJECT` are required; any other key is a custom action (UPPER_CASE, e.g. `REFER_UW`) with a `label`. Each action has a `destination` (`Stage`, `Correction`, `Approved`, `Rejected`; APPROVE cannot go to `Correction`) and optional `rules` that pick a conditional destination. REJECT always requires `data.reason`; set `"require_reason": true` to require it for a custom action. Custom actions are not allowed on parallel stages.

```json
"actions": [
  { "key": "APPROVE", "label": "Approve", "destination": { "kind": "Stage", "stage_key": "D" },
    "rules": [ { "conditions": [ { "attribute": "claimAmount", "operator": "GreaterThan", "value": 5000 } ],
                 "destination": { "kind": "Stage", "stage_key": "C" } } ] },
  { "key": "REJECT", "label": "Reject", "destination": { "kind": "Stage", "stage_key": "A" } },
  { "key": "REFER_UW", "label": "Refer to UW", "destination": { "kind": "Stage", "stage_key": "UW" }, "require_reason": true }
]
```

The task's `actions` (key + label) in `GET /elsa/api/user-tasks` and task detail are exactly these buttons; complete with `actionKey` set to one of them. Instance output `lastDecision` holds the chosen key. Older configurations with `approveTo` / `rejectTo` / `approveRules` are still read (converted to APPROVE/REJECT actions).

### Rule conditions (AND / OR)

`actions[].rules[]` and `assignment.rules[]` hold a `conditions` list. Conditions without `or_group` must all match; within each `or_group` number at least one must match. Rules are checked in order and the first match wins. Maximum 20 conditions per rule.

```json
{
  "conditions": [
    { "attribute": "claimAmount", "operator": "GreaterThan", "value": 5000 },
    { "attribute": "department", "operator": "Equals", "value": "Finance", "or_group": 1 },
    { "attribute": "department", "operator": "Equals", "value": "HR", "or_group": 1 }
  ],
  "destination": { "kind": "Stage", "stage_key": "C" }
}
```

This means claimAmount > 5000 AND (department = Finance OR HR). Older configurations stored a single `when` object; it is still read (as a one-item list) so published versions keep routing, but new requests should use `conditions`.

## Parallel approvals

Add `"parallelWith": ["D"]` to stage C. Routes into C open both C and D as separate Elsa tasks. The first approval follows C's approval route and cancels unanswered tasks. If every branch rejects, the group follows C's rejection route. Define C and D as regular stages with their own assignments and matching default destinations. Put shared approval rules on C and route to C to enter the group.

Run the [parallel approval requests](Parallel-Approvals.http) for each decision order. Use `dotnet test ilworkflow.Tests --filter ParallelWorkflowTests` for the live PostgreSQL checks, then restart the API and repeat with `ILWORKFLOW_VERIFY_RESTART=1`.

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
# Live tests (ilworkflow.Tests) skip when the API is unreachable.
$env:ILWORKFLOW_BASE_URL = "http://localhost:5000"
dotnet test ilworkflow.Tests
# Restart API, then:
$env:ILWORKFLOW_VERIFY_RESTART = "1"; dotnet test ilworkflow.Tests; Remove-Item Env:ILWORKFLOW_VERIFY_RESTART
# In documentation preview instead:
$env:ILWORKFLOW_VERIFY_PREVIEW = "1"; dotnet test ilworkflow.Tests --filter ConfiguredWorkflowTests; Remove-Item Env:ILWORKFLOW_VERIFY_PREVIEW
```

Checks cover conditional routing, correction, exact comparisons, cycles, concurrency, permissions, version pinning and restart recovery.

All-approver and quorum policies, round robin, submission outbox, frontend, attachments, notifications, SLA escalation and external identity providers remain deferred.

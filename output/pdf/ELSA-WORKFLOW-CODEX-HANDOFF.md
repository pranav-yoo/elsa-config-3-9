# Elsa 3.9 ticket workflows: complete handoff for another Codex

Date: 25 October 2026 (Asia/Calcutta).

## 1. Read this first

This is the final **restored and tested application**, not the abandoned native-only experiment. Implement the same behavior in the receiving application. This handoff is model-independent; the receiving Codex does not need a particular model name.

- ASP.NET Core, .NET 10 in this research app; Elsa packages pinned to **3.9.0**.
- Business workflow definition ID: **expense-claim-v1**.
- Native Elsa APIs for definition publication, workflow execution, task listing, claiming, decisions and history. No custom ticket REST controllers.
- One small custom **Tickets.TicketFlow** activity interprets a **configurationJson literal string** and schedules native Elsa User Tasks.
- One custom **TicketFormProvider** validates Approve, Reject and Resubmit data. This provider is necessary for the tested payloads.
- Attributes and stage/member/routing configuration are JSON. Do not build a second task engine or add custom ticket/task tables merely to reproduce this research sample.
- Development users/roles are native Elsa configuration providers backed by JSON. Actual workflow/task state is managed by Elsa, not by these JSON files.
- Optional API-managed identity mode: start with `--Identity:UseConfigurationProviders=false`. Native store providers authenticate users created through Elsa's APIs; the default JSON mode remains available. See Identity-And-ABCD.http for role/user creation followed by the full workflow.
- All approval/rejection/correction checks in this handoff passed in **in-memory preview mode**. PostgreSQL restart persistence of this final version is **not verified**.

**Do not tell the receiving application that configurationJson is a built-in Elsa interpreter. It is our custom input and activity.** Merely posting this JSON to a plain Elsa server without registering the activity/form provider will not reproduce the behavior.

The API-only PDF is [Elsa-3.9-Ticket-Workflow-API-Guide.pdf](Elsa-3.9-Ticket-Workflow-API-Guide.pdf). This Markdown additionally contains the configuration, source code, curl examples and receiving-agent implementation instructions.

## 2. Exact business behavior

```text
A: Alice/Amy bucket --Approve--> B: Bella/Bob bucket
  B approval, claimAmount > 5000  --> C: Clara --> D: Dan
  B approval, claimAmount <= 5000 ----------------> D: Dan
D --Approve--> Completed

A --Reject--> requester correction --Resubmit--> new A visit
B --Reject--> new A visit
C --Reject--> terminal Rejected
D --Reject--> requester correction --Resubmit--> new D visit
```

| Stage | Assignment | Approve | Reject |
|---|---|---|---|
| A | Alice or Amy claims one bucket task | B | Requester correction, then A |
| B | Bella or Bob claims one bucket task | C above 5000, otherwise D | A |
| C | Clara, conditional assignee | D | Terminal Rejected |
| D | Dan, specific assignee | Completed | Requester correction, then D |

Important details:

1. **5000 does not require C**; the comparison is strictly greaterThan 5000.
2. B is a **bucket**, not round robin. The user explicitly accepted this replacement.
3. One owner and one decision per stage visit. Candidates do not each vote.
4. Each stage visit creates a different native Elsa task. Completed A plus active B in one list is correct.
5. Review attributes are frozen. Review data may contain only string `reason` and/or `comment` fields.
6. Reject requires a non-empty `reason`. Correction uses `actionKey: Resubmit` and a complete validated `data.attributes` object.
7. Correction continues the **same workflow instance** and resumes the rejected stage.
8. **D correction does not replay B/C.** If 4000 becomes 6000 after D rejects, resubmission still resumes D. Preserve this tested behavior unless the human explicitly changes the policy. Flag its implications for a real approval system.
9. Both terminal business outcomes have Elsa `status: Finished`; distinguish them with `workflowState.output.ticketStatus` (`Completed` or `Rejected`).
10. Native operation IDs/revisions protect task commands; correlation IDs do **not** make execute/submission idempotent.

## 3. Files and responsibilities

| File | Purpose |
|---|---|
| Program.cs | Elsa host, native authentication/configuration providers, Swagger, persistence registrations, sanitized logging |
| Tickets/TicketFlow.cs | Custom configuration interpreter; schedules native UserTask and chooses next stage |
| Tickets/TicketConfiguration.cs | Configuration records, attribute/type/condition/configuration validation |
| Tickets/TicketFormProvider.cs | Native IUserTaskFormProvider implementation for review/correction submissions |
| Samples/identity.json | Development-only sample users and native roles/permissions |
| Samples/expense-claim.configuration.json | Readable business configuration |
| Samples/Definitions/expense-claim.definition.json | Complete POST body with escaped literal configurationJson |
| Tickets.http | Reusable native API requests |
| Identity-And-ABCD.http | Native role/user creation, then ordered A-B-C-D with generated IDs |
| Tests/ConfigurationApi.ps1 | Verified approval/rejection/correction/access test suite |
| docs/api-captures.json | 148 captured HTTP exchanges, authentication secrets redacted |

The native-only experiment `expense-claim-native-v1` and `Tests/TicketApi.ps1` are separate, Complete-only examples. **They are not the application to port.** Do not substitute them for the implementation in this handoff.

## 4. Run and authenticate

In the source application:

```powershell
dotnet restore
dotnet build
dotnet run --launch-profile http
```

Swagger: `http://localhost:5000/swagger`. OpenAPI: `http://localhost:5000/swagger/v3/swagger.json`.

Development defaults to `SwaggerPreview=true`. It skips PostgreSQL connections/migrations and uses in-memory stores. Restart clears definitions, instances and tasks. Re-publish the definition after restart.

| Username / stable user ID | Development password | Purpose |
|---|---|---|
| admin | password | Native admin wildcard permissions |
| configurator | Password123! | Publish definitions |
| requester | Password123! | Start workflow and resubmit corrections |
| alice, amy | Password123! | A candidates |
| bella, bob | Password123! | B candidates |
| clara | Password123! | C owner |
| dan | Password123! | D owner |
| supervisor | Password123! | Native supervision/assignment permissions |

These are research credentials only. Sample passwords are represented by hashes in identity.json. In Development, changing Identity:Admin:Password in user secrets does not replace the configured admin hash. Do not overwrite an existing application's authentication with these fixtures.

## 5. The readable configuration JSON

Supported attribute types: string, decimal, integer, boolean, date (`yyyy-MM-dd`) and choice (`choices` array). Assignment policies: bucket, specific, condition. Conditions: equals, notEquals, greaterThan, greaterThanOrEqual, lessThan, lessThanOrEqual. First matching rule wins; default approveTo applies when no approval rule matches.

`members` contains user IDs, not actual directory groups. The sample validates candidates through native IUserProvider with the Approver role. The selected provider can be configuration-backed (default) or store-backed (API-managed accounts). External group membership and disabled-account enforcement still need receiving-app integration.

```json
{
  "id": "expense-claim",
  "version": 1,
  "definition": {
    "name": "Expense Claim",
    "attributes": [
      { "key": "firstName", "type": "string", "required": true },
      { "key": "lastName", "type": "string", "required": true },
      { "key": "claimAmount", "type": "decimal", "required": true }
    ],
    "startStage": "A",
    "stages": [
      {
        "key": "A", "name": "A: Initial review (Alice/Amy bucket)",
        "assignment": { "policy": "bucket", "members": ["alice", "amy"] },
        "approveTo": "B", "rejectTo": "$requester"
      },
      {
        "key": "B", "name": "B: Claims review (Bella/Bob bucket)",
        "assignment": { "policy": "bucket", "members": ["bella", "bob"] },
        "approveTo": "D", "rejectTo": "A",
        "approveRules": [
          { "when": { "attribute": "claimAmount", "operator": "greaterThan", "value": 5000 }, "destination": "C" }
        ]
      },
      {
        "key": "C", "name": "C: High value approval (Clara)",
        "assignment": {
          "policy": "condition", "members": ["clara"],
          "rules": [ { "when": { "attribute": "claimAmount", "operator": "greaterThan", "value": 5000 }, "userId": "clara" } ]
        },
        "approveTo": "D", "rejectTo": "$rejected"
      },
      {
        "key": "D", "name": "D: Final approval (Dan)",
        "assignment": { "policy": "specific", "members": ["dan"], "userId": "dan" },
        "approveTo": "$completed", "rejectTo": "$requester"
      }
    ]
  }
}
```

## 6. Exact definition publication JSON

Save this as `expense-claim.definition.json`, or use the provided companion file in workflow-requests.

Keep this exact input pattern: `configurationJson.expression.type` is `Literal`, `configurationJson.typeName` is `String`, and `configurationJson.expression.value` is an **escaped JSON string**, not an object. Root type is `Tickets.TicketFlow`.

```json
{
  "publish": true,
  "model": {
    "definitionId": "expense-claim-v1",
    "name": "Expense Claim",
    "description": "configurationJson interpreter backed by native Elsa User Tasks",
    "root": {
      "id": "expense-claim",
      "name": "ExpenseClaim",
      "configurationJson": {
        "expression": {
          "value": "{\"id\":\"expense-claim\",\"version\":1,\"definition\":{\"name\":\"Expense Claim\",\"attributes\":[{\"key\":\"firstName\",\"type\":\"string\",\"required\":true},{\"key\":\"lastName\",\"type\":\"string\",\"required\":true},{\"key\":\"claimAmount\",\"type\":\"decimal\",\"required\":true}],\"startStage\":\"A\",\"stages\":[{\"key\":\"A\",\"name\":\"A: Initial review (Alice/Amy bucket)\",\"assignment\":{\"policy\":\"bucket\",\"members\":[\"alice\",\"amy\"]},\"approveTo\":\"B\",\"rejectTo\":\"$requester\"},{\"key\":\"B\",\"name\":\"B: Claims review (Bella/Bob bucket)\",\"assignment\":{\"policy\":\"bucket\",\"members\":[\"bella\",\"bob\"]},\"approveTo\":\"D\",\"rejectTo\":\"A\",\"approveRules\":[{\"when\":{\"attribute\":\"claimAmount\",\"operator\":\"greaterThan\",\"value\":5000},\"destination\":\"C\"}]},{\"key\":\"C\",\"name\":\"C: High value approval (Clara)\",\"assignment\":{\"policy\":\"condition\",\"members\":[\"clara\"],\"rules\":[{\"when\":{\"attribute\":\"claimAmount\",\"operator\":\"greaterThan\",\"value\":5000},\"userId\":\"clara\"}]},\"approveTo\":\"D\",\"rejectTo\":\"$rejected\"},{\"key\":\"D\",\"name\":\"D: Final approval (Dan)\",\"assignment\":{\"policy\":\"specific\",\"members\":[\"dan\"],\"userId\":\"dan\"},\"approveTo\":\"$completed\",\"rejectTo\":\"$requester\"}]}}",
          "type": "Literal"
        },
        "typeName": "String"
      },
      "type": "Tickets.TicketFlow",
      "version": 1
    },
    "inputs": [
      {
        "name": "requesterId",
        "type": "String"
      },
      {
        "name": "firstName",
        "type": "String"
      },
      {
        "name": "lastName",
        "type": "String"
      },
      {
        "name": "claimAmount",
        "type": "Decimal"
      }
    ]
  }
}
```

To change configuration safely, serialize the readable JSON into the literal string. Never concatenate or hand-escape it. Also update `model.inputs` when adding/removing attribute definitions; the current sample does not automatically generate input declarations.

```powershell
$definition = Get-Content ./expense-claim.definition.json -Raw | ConvertFrom-Json
$config = Get-Content ./expense-claim.configuration.json -Raw | ConvertFrom-Json
$definition.model.root.configurationJson.expression.value = $config | ConvertTo-Json -Depth 60 -Compress
$definition | ConvertTo-Json -Depth 70 | Set-Content -Encoding utf8NoBOM ./expense-claim.definition.json
```

Configuration validation happens **during execution**, not at native definition publication. It checks the known destinations, stage/attribute duplicates, candidate references, supported policies, condition types, start stage and approval-path cycles. Rejection return paths are allowed. Do not describe this as a complete immutable ticket-type publishing subsystem.

## 7. JSON request bodies and curl commands

The examples below use **PowerShell 7 + curl.exe**, so Windows PowerShell's old curl alias does not interfere. Curl commands use JSON files to avoid quoting bugs. Companion files are supplied in workflow-requests; run commands from that directory. Replace captured/example IDs with those returned by your own server. Response examples are excerpts; omitted fields are not absent from the actual API.

### 7.1 Login and keep a separate token per actor

Sample `login-alice.json`:

```json
{
  "username": "alice",
  "password": "Password123!"
}
```

```powershell
$base = 'http://localhost:5000'
$aliceLogin = curl.exe -sS -X POST "$base/elsa/api/identity/login" -H "Content-Type: application/json" --data-binary "@login-alice.json" | ConvertFrom-Json
$aliceToken = $aliceLogin.accessToken
if (-not $aliceToken) { throw 'Alice login failed' }

# Obtain each actor's token; do not keep using Alice's token for Bob/Clara/Dan.
$tokens = @{}
foreach ($user in @('admin','configurator','requester','alice','amy','bella','bob','clara','dan','supervisor')) {
    $login = curl.exe -sS -X POST "$base/elsa/api/identity/login" -H "Content-Type: application/json" --data-binary "@login-$user.json" | ConvertFrom-Json
    if (-not $login.accessToken) { throw "Login failed: $user" }
    $tokens[$user] = $login.accessToken
}
```

Captured login response (tokens redacted):

```json
{
  "isAuthenticated": true,
  "accessToken": "[REDACTED]",
  "refreshToken": "[REDACTED]"
}
```

In Swagger, paste the accessToken into the JWT bearer Authorize field. The UI supplies the Bearer prefix. In curl, explicitly send `Authorization: Bearer ...`.

### 7.2 Publish as configurator

```powershell
curl.exe -sS -X POST "$base/elsa/api/workflow-definitions" -H "Authorization: Bearer $($tokens['configurator'])" -H "Content-Type: application/json" --data-binary "@expense-claim.definition.json"
```

Captured HTTP 200 response excerpt:

```json
{
  "workflowDefinition": {
    "id": "4947fa042723c007",
    "definitionId": "expense-claim-v1",
    "version": 1,
    "isPublished": true
  }
}
```

The returned id is the definition-version ID. `definitionId` is the stable workflow template identifier. Neither is a task ID.

### 7.3 Execute / submit as requester

Save as `submit.json` (provided). Use a new reference for a new claim. Repeating execute with the same reference can start another instance.

```json
{
  "correlationId": "CLAIM-001",
  "input": {
    "requesterId": "requester",
    "firstName": "Pranav",
    "lastName": "Shah",
    "claimAmount": 6000
  }
}
```

```powershell
$run = curl.exe -sS -X POST "$base/elsa/api/workflow-definitions/expense-claim-v1/execute" -H "Authorization: Bearer $($tokens['requester'])" -H "Content-Type: application/json" --data-binary "@submit.json" | ConvertFrom-Json
$workflowId = $run.workflowState.id
$run.workflowState | Select-Object id, status, subStatus
# Expect Suspended while waiting at A; HTTP 200 alone does not prove success.
```

Captured HTTP 200 excerpt from a 6000 submission; IDs/reference differ from the template:

```json
{
  "workflowState": {
    "id": "7eaecfcc864b7711",
    "definitionId": "expense-claim-v1",
    "correlationId": "DOC-approved-6000-8021f9",
    "status": "Running",
    "subStatus": "Suspended"
  }
}
```

### 7.4 Find available A work as Alice

```powershell
$available = curl.exe -sS "$base/elsa/api/user-tasks?scope=available&workflowInstanceId=$workflowId&includeTotalCount=true" -H "Authorization: Bearer $($tokens['alice'])" | ConvertFrom-Json
$available.items | Select-Object id, title, status, revision
$taskId = $available.items[0].id
$revision = $available.items[0].revision
```

Observed initial A task shape (excerpt from the captured admin overview):

```json
{
  "items": [
    {
      "id": "2c67725fd185baf8",
      "title": "A: Initial review (Alice/Amy bucket)",
      "status": "Available",
      "workflowInstanceId": "aa7aaaa3c939dc4f",
      "revision": 1
    }
  ],
  "totalCount": 1
}
```

Native scopes: `available` for eligible claimable work; `assigned` for owned work; `all` for elevated overview. A valid token from an unrelated user may see no tasks. Do not use the requester token to claim A.

### 7.5 Claim with the latest revision

Generate `claim.json` for the task just read:

```powershell
@{ expectedRevision = $revision; operationId = [guid]::NewGuid().ToString() } | ConvertTo-Json | Set-Content -Encoding utf8NoBOM claim.json
$claim = curl.exe -sS -X POST "$base/elsa/api/user-tasks/$taskId/claim" -H "Authorization: Bearer $($tokens['alice'])" -H "Content-Type: application/json" --data-binary "@claim.json" | ConvertFrom-Json
$revision = $claim.task.revision
```

Example body:

```json
{
  "expectedRevision": 1,
  "operationId": "11111111-1111-4111-8111-111111111111"
}
```

Captured HTTP 200 claim response excerpt:

```json
{
  "operationId": "8793f14b-d80d-4aaf-bbac-9d6bffebefac",
  "status": "completed",
  "revision": 2,
  "task": {
    "id": "2c67725fd185baf8",
    "status": "Assigned",
    "revision": 2,
    "assignee": {
      "kind": "user",
      "provider": "default",
      "id": "alice",
      "displayName": "alice"
    }
  }
}
```

`expectedRevision` is the task concurrency revision. It is not a definition version. Read the task again after changes. `operationId` is a UUID for one logical command: use a new one for a new command; retry uncertain requests with the **same ID, revision and payload**.

### 7.6 Read current details and approve as owner

```powershell
$task = curl.exe -sS "$base/elsa/api/user-tasks/$taskId" -H "Authorization: Bearer $($tokens['alice'])" | ConvertFrom-Json
@{ expectedRevision = $task.revision; operationId = [guid]::NewGuid().ToString(); actionKey = 'Approve'; data = @{ comment = 'Initial checks passed' } } | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8NoBOM approve.json
curl.exe -sS -X POST "$base/elsa/api/user-tasks/$taskId/complete" -H "Authorization: Bearer $($tokens['alice'])" -H "Content-Type: application/json" --data-binary "@approve.json"
```

```json
{
  "expectedRevision": 2,
  "operationId": "22222222-2222-4222-8222-222222222222",
  "actionKey": "Approve",
  "data": {
    "comment": "Initial checks passed"
  }
}
```

Captured HTTP 202 approval response excerpt:

```json
{
  "operationId": "db9f4399-c50e-4afa-9b80-8d2a3a3c3c39",
  "status": "accepted",
  "revision": 3,
  "task": {
    "id": "2c67725fd185baf8",
    "status": "Completing",
    "revision": 3,
    "assignee": {
      "kind": "user",
      "provider": "default",
      "id": "alice",
      "displayName": "alice"
    }
  }
}
```

HTTP 202/status accepted/task Completing means Elsa accepted the command and is resuming the workflow. Poll for completion and the next stage; do not assume the next task is visible immediately.

### 7.7 Continue B, conditional C, then D

```powershell
# B: Bella or Bob lists available work and claims the new task.
curl.exe -sS "$base/elsa/api/user-tasks?scope=available&workflowInstanceId=$workflowId" -H "Authorization: Bearer $($tokens['bob'])"
# Repeat claim -> GET detail -> complete Approve using B's ID/revision and Bob's token.

# C exists only if B approved with amount > 5000. It is already assigned to Clara.
curl.exe -sS "$base/elsa/api/user-tasks?scope=assigned&workflowInstanceId=$workflowId" -H "Authorization: Bearer $($tokens['clara'])"
# GET C detail -> complete Approve with Clara's token. No bucket claim is needed.

# D is already assigned to Dan; it follows B directly or follows C.
curl.exe -sS "$base/elsa/api/user-tasks?scope=assigned&workflowInstanceId=$workflowId" -H "Authorization: Bearer $($tokens['dan'])"
# GET D detail -> complete Approve with Dan's token.
```

Always replace taskId, expectedRevision and actor token for each task. Do not reuse A's ID to complete B.

### 7.8 Reject a review task

This is the alternative to approval on an active owned task. After a task has completed, start a separate test claim to try rejection.

```powershell
$owner = 'clara' # Example: rejecting C; use alice/bob/dan for their owned task.
$task = curl.exe -sS "$base/elsa/api/user-tasks/$taskId" -H "Authorization: Bearer $($tokens[$owner])" | ConvertFrom-Json
@{ expectedRevision = $task.revision; operationId = [guid]::NewGuid().ToString(); actionKey = 'Reject'; data = @{ reason = 'High-value claim declined' } } | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8NoBOM reject.json
curl.exe -sS -X POST "$base/elsa/api/user-tasks/$taskId/complete" -H "Authorization: Bearer $($tokens[$owner])" -H "Content-Type: application/json" --data-binary "@reject.json"
```

```json
{
  "expectedRevision": 1,
  "operationId": "33333333-3333-4333-8333-333333333333",
  "actionKey": "Reject",
  "data": {
    "reason": "High-value claim declined"
  }
}
```

Captured HTTP 202 C rejection response excerpt:

```json
{
  "operationId": "97898eb4-6cad-4e84-859c-df3a0ee60a79",
  "status": "accepted",
  "revision": 2,
  "task": {
    "id": "1925851f2a597547",
    "status": "Completing",
    "revision": 2,
    "assignee": {
      "kind": "user",
      "provider": "default",
      "id": "clara"
    }
  }
}
```

The client never supplies the next destination. `rejectTo` determines it: A -> requester, B -> A, C -> Rejected, D -> requester. Rejection without a reason is HTTP 422, not a successful rejection.

### 7.9 Correct and resubmit as requester

After A or D returns a claim, retrieve the **new correction task ID** as requester. Do not complete the rejected review task again.

```powershell
$corrections = curl.exe -sS "$base/elsa/api/user-tasks?scope=assigned&workflowInstanceId=$workflowId" -H "Authorization: Bearer $($tokens['requester'])" | ConvertFrom-Json
$correctionId = $corrections.items[0].id
$correction = curl.exe -sS "$base/elsa/api/user-tasks/$correctionId" -H "Authorization: Bearer $($tokens['requester'])" | ConvertFrom-Json
@{ expectedRevision = $correction.revision; operationId = [guid]::NewGuid().ToString(); actionKey = 'Resubmit'; data = @{ attributes = @{ firstName = 'Pranav'; lastName = 'Shah'; claimAmount = 4000 } } } | ConvertTo-Json -Depth 10 | Set-Content -Encoding utf8NoBOM resubmit.json
curl.exe -sS -X POST "$base/elsa/api/user-tasks/$correctionId/complete" -H "Authorization: Bearer $($tokens['requester'])" -H "Content-Type: application/json" --data-binary "@resubmit.json"
```

```json
{
  "expectedRevision": 1,
  "operationId": "44444444-4444-4444-8444-444444444444",
  "actionKey": "Resubmit",
  "data": {
    "attributes": {
      "firstName": "Pranav",
      "lastName": "Shah",
      "claimAmount": 4000
    }
  }
}
```

Captured HTTP 202 resubmission response excerpt:

```json
{
  "operationId": "9f33ab33-2345-442e-a221-325aa0eb4854",
  "status": "accepted",
  "revision": 2,
  "task": {
    "id": "fe1f5ac134421f45",
    "status": "Completing",
    "revision": 2,
    "assignee": {
      "kind": "user",
      "provider": "default",
      "id": "requester"
    }
  }
}
```

### 7.10 Final status and complete journey

```powershell
curl.exe -sS "$base/elsa/api/workflow-instances/$workflowId" -H "Authorization: Bearer $($tokens['admin'])"
curl.exe -sS "$base/elsa/api/user-tasks?scope=all&workflowInstanceId=$workflowId&includeTotalCount=true" -H "Authorization: Bearer $($tokens['admin'])"
curl.exe -sS "$base/elsa/api/user-tasks/$taskId/events" -H "Authorization: Bearer $($tokens['admin'])"
curl.exe -sS "$base/elsa/api/workflow-instances/$workflowId/journal" -H "Authorization: Bearer $($tokens['admin'])"
curl.exe -sS "$base/elsa/api/workflow-instances/$workflowId/execution-state" -H "Authorization: Bearer $($tokens['admin'])"
```

Captured Finished instance (Completed) excerpt:

```json
{
  "id": "aa7aaaa3c939dc4f",
  "status": "Finished",
  "workflowState": {
    "output": {
      "ticketStatus": "Completed",
      "configurationVersion": 1
    }
  }
}
```

Captured Finished instance (Rejected) excerpt:

```json
{
  "id": "e5d42457510b71d7",
  "status": "Finished",
  "workflowState": {
    "output": {
      "ticketStatus": "Rejected",
      "configurationVersion": 1
    }
  }
}
```

Completed task data preserves the snapshot for that stage visit. Combine task details, each task's events and the workflow journal for the journey. There is no separate custom journey API, frontend or numeric attribute-revision resource. Native pagination applies when many stage visits exist; follow the endpoint's pagination schema.

### 7.11 Errors and response interpretation

| Result | Meaning |
|---|---|
| 200 login, accessToken present | Authenticated; save the accessToken |
| 200 execute, subStatus Suspended | Started and waiting for human work |
| 200 execute with a faulted subStatus/incidents | Activity failed validation/execution; HTTP 200 alone is insufficient |
| 202 completion accepted | Poll for resume/next task |
| 401 | Missing, invalid or expired bearer token |
| 403 | Missing permission or denied operation |
| 404 task | Missing or hidden from this identity |
| 409 | Revision/operation conflict; competing claim may instead yield 403/404 |
| 422 form-invalid | Invalid response data, e.g. missing rejection reason or edited attributes during review |
| Empty list | Check actor membership, scope, instance ID, current stage and version |

Captured invalid rejection response (HTTP 422):

```json
{
  "code": "form-invalid",
  "message": "The response did not pass validation."
}
```

The host adds X-Trace-Id to responses. Logs include method/path/status/duration, workflow ID, stage/visit, sanitized database target and database SQLSTATE. Request bodies, passwords, bearer tokens and attribute values are not intentionally logged. Refresh tokens/signing keys must not be exposed in diagnostic output.

## 8. Host integration: minimum registrations

The source registers all Elsa packages at 3.9.0. Copy only packages needed by the receiving app; preserve its existing target/framework unless incompatible. This sample host additionally enables native JavaScript/Flowchart for the separate native-only experiment; the configuration interpreter below does not depend on those expressions for its routing.

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UserSecretsId>ilworkflow-elsa-local</UserSecretsId>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Elsa" Version="3.9.0" />
    <PackageReference Include="Elsa.Persistence.EFCore.PostgreSql" Version="3.9.0" />
    <PackageReference Include="Elsa.Identity" Version="3.9.0" />
    <PackageReference Include="Elsa.Http" Version="3.9.0" />
    <PackageReference Include="Elsa.Workflows.Api" Version="3.9.0" />
    <PackageReference Include="Elsa.UserTasks" Version="3.9.0" />
    <PackageReference Include="Elsa.UserTasks.Persistence.EFCore.PostgreSql" Version="3.9.0" />
      <PackageReference Include="Elsa.Expressions.JavaScript" Version="3.9.0" />
  </ItemGroup>
</Project>
```

```csharp
using Elsa.Extensions;
using Elsa.UserTasks.Activities;
using Elsa.UserTasks.Contracts;
using Elsa.UserTasks.Services;
using IlWorkflow.Api.Tickets;

// Merge into the receiving application's existing AddElsa configuration.
builder.Services.AddElsa(elsa =>
{
    elsa.UseWorkflowsApi()
        .UseUserTasks()
        .AddActivitiesFrom<UserTask>()
        .AddActivitiesFrom<TicketFlow>();
    // Keep the real app's authentication, persistence and Swagger integration.
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IUserTaskFormProvider, TicketFormProvider>();

// This research app needs native service lifetime overrides in Elsa 3.9.
// Both native implementations resolve scoped runtime/mediator services.
builder.Services.AddScoped<IUserTaskNotificationSink, DefaultUserTaskNotificationSink>();
builder.Services.AddScoped<IUserTaskWorkflowResumer, DefaultUserTaskWorkflowResumer>();
```

This is an integration excerpt, not a standalone production Program.cs. The exact research host appears in appendix D. Do not create a second competing AddElsa host if the receiving app already has one. Register the activity and form provider on its existing Elsa setup. Keep native authentication, workflow management/runtime and endpoint/middleware wiring in place.

The current TicketFlow checks the HTTP caller subject (`ClaimTypes.NameIdentifier` or `sub`) and the original requester. It assumes synchronous execute with an HttpContext. **Do not replace execute with background dispatch without redesigning this ownership check.**

Member/requester validation uses native IUserProvider.FindAsync(UserFilter) and roles. Configure the receiving application's identity integration consistently; preserve candidate and requester validation. Keep the same IDs in JWT identities and ParticipantReference IDs. The sample uses provider `default`, user type, and empty tenant; tenant-aware apps must adapt that consistently. Native POST /identity/users accepts name/password/roles and generates the user ID; it does not accept a fixed id field. Use the returned ID when building configurationJson and requesterId.

## 9. PostgreSQL: configuration versus evidence

Research database target: localhost:5432, database ilworkflow, username postgres. Store real credentials and signing keys in user secrets or the receiving application's secret provider. Do not copy development secrets into another app's committed configuration.

```powershell
dotnet user-secrets set "ConnectionStrings:Elsa" "Host=localhost;Port=5432;Database=ilworkflow;Username=postgres;Password=<local-password>"
dotnet user-secrets set "Identity:Tokens:SigningKey" "<random-secret-of-at-least-32-characters>"
dotnet user-secrets set "Identity:Admin:Password" "<local-admin-password>"
# Research host only: SwaggerPreview=false enables persistence.
# A different receiving app may use its own environment/configuration convention.
```

The host registers PostgreSQL persistence for management/runtime/identity/UserTasks. In Development it checks/creates only ilworkflow through the postgres maintenance database; Elsa applies migrations. A role needs suitable connection/database creation permissions.

Do not overstate the results:

- The local server previously returned recovery-mode SQLSTATE 57P03.
- Separate native Elsa 3.9 UserTasks experiments found TagsJson model/migration mapping and nullable JSON deserialization issues.
- Prior custom PostgreSQL compatibility adapters were removed. The final restored app does not contain those patches.
- The 148 captured exchanges are preview/in-memory evidence, not restart-persistence evidence.
- The receiving Codex must reproduce provider failures, identify the cause and test any explicit workaround before claiming PostgreSQL support. Never reset an existing database to hide migration problems.

## 10. Instructions to the receiving Codex

Copy the following prompt when starting work in the other application:

```text
Implement the Elsa 3.9 ticket workflow described in this handoff in the CURRENT application.

1. Inspect the current repository, AGENTS.md, target framework, authentication, database and existing Elsa setup. Preserve existing work. Do not replace Program.cs or authentication wholesale.
2. Summarize the integration points briefly, then implement. Ask only for genuinely missing blocking information. Do not silently substitute a native-only Complete workflow.
3. Keep Elsa packages at 3.9.0 and use native Elsa User Tasks/APIs. Do not create a second task engine or custom ticket REST controllers just for this sample.
4. Port the THREE source files in appendices A-C. Adapt namespaces and real user-directory/subject/tenant mapping. Keep Tickets.TicketFlow as the Elsa type name unless you also update the definition JSON. Keep the configurationJson Literal/String wrapper.
5. Register the custom activity, native UserTask, TicketFormProvider and required native scoped lifetimes in the existing Elsa host. Keep native identity/runtime/persistence/Swagger wiring working. No Elsa Studio/frontend is requested.
6. Start in a disposable research/development environment. Use JSON configuration fixtures, native API publication and one workflow instance per claim. Do not add unnecessary application tables. Existing app identity can be used instead of sample JSON accounts.
7. Implement the exact A/B bucket, C threshold, D owner, Approve/Reject/Resubmit behaviors. B is NOT round robin. Rejection at D resumes D after correction, even if the amount increases. Do not change this policy without explicit human direction.
8. Preserve configuration snapshots and native task snapshots. Final business outcome must remain in workflowState.output.ticketStatus after completion; activity-local properties alone are insufficient once Elsa prunes completed activity contexts.
9. Validate membership and frozen attributes. Do not trust client-supplied requester IDs, assignees or routing destinations. Preserve the ownership check; redesign it before using background dispatch.
10. Run the acceptance cases below through real APIs. Record sanitized actual requests/responses. Check business status and workflow incidents, not only HTTP codes. Test task operation retries using the same operationId/payload; do not claim execute is idempotent.
11. If PostgreSQL is required, verify native migrations, task read/write and restart/resume on the receiving database. Document any provider failures. Do not claim the source research run proved persistence.
12. Deliver a working implementation, request collection and concise setup/validation notes. State which behavior was tested, which remains unverified and every material deviation from this handoff. Do not claim configurationJson is built into Elsa.

Use this document as the behavioral reference. Reuse the appendix code rather than inventing additional abstractions. Do not add attachments, email, SLA, delegation, custom admin UI or Elsa 4 migration.
```

### Receiving-agent acceptance checks

Already verified in the source research app:

- 4000 and 5000: A -> B -> D -> business Completed.
- 6000: A -> B -> C -> D -> business Completed.
- Reject A: requester task, correction to 4000, new A -> B -> D; same workflow ID.
- Reject B: new A visit, then B and D.
- Reject C: terminal business Rejected; Elsa status Finished.
- Reject D: requester task, correction to 6000, resume D; C is not replayed.
- Reject without reason -> 422.
- Attribute edit embedded in a review decision -> 422.
- Replaying the same decision operation ID/payload does not create a second transition.
- Unauthenticated task list -> 401; requester publishing -> 403; unrelated task detail -> 404.
- A competing/stale claim is denied. The restored suite tests this serially; do not describe it as a full load/concurrency test.
- Separate build: zero warnings/errors.

Required additional validation before production claims (not proven by the final source run):

- Real account disabling, tenant boundaries and external group membership.
- Concurrent claim races under the selected persistence provider.
- Blocked/no-matching-assignee behavior and native supervisor reassignment.
- New configuration publication while older instances remain active.
- PostgreSQL migrations, task querying and durable restart/resume.
- Reliable/idempotent submission if the receiving app requires it.

Use `Tests/ConfigurationApi.ps1` as the starting API suite. It creates disposable test workflows and captures exchanges. Do not run it against a shared production database. The source's ordinary Debug build was blocked by a still-running Visual Studio app locking its executable; the isolated bin/configresearch build and API checks passed. Stop debugging before rebuilding the ordinary output.

## 11. Boundaries and known gaps

- No automatic round robin; B is a user bucket.
- No full ticket-type CRUD/version publishing subsystem. Native Elsa definition versions exist; the interpreter snapshots configuration at start, but application-level immutable configuration authoring APIs are not implemented.
- Configuration validation is at execution, not publish time; malformed/null-heavy configuration is not a fully hardened public schema contract.
- New attributes need matching workflow input declarations. Initial execution builds values from configured attributes; unrelated extra workflow input keys are not necessarily rejected as unknown business attributes.
- No custom ticket/history tables, custom journey endpoint, attachment support, notifications, frontend, unanimous group approval, delegation or SLA handling.
- No guaranteed disabled-account enforcement beyond whatever the receiving identity system supplies.
- No demonstrated transactional outbox or start idempotency. Native operation IDs/revisions cover task commands, not all external application writes.
- No migration of old active instances from earlier experiments. Do not assume changing activity code safely upgrades persisted callbacks/forms/configuration from a different build. Test upgrades separately or finish old instances before changing behavior.
- No completed-journey persistence claim for the final stock PostgreSQL setup.

## Appendix A. Ticket configuration models and rules (exact source)

```csharp
using System.Text.Json;
using Elsa.Identity.Contracts;
using Elsa.Identity.Models;

namespace IlWorkflow.Api.Tickets;

public sealed record TicketConfiguration(string Id, int Version, TicketDefinition Definition);
public sealed record TicketDefinition(string Name, FieldDefinition[] Attributes, string StartStage, StageDefinition[] Stages);
public sealed record FieldDefinition(string Key, string Type, bool Required = false, string[]? Choices = null);
public sealed record Condition(string Attribute, string Operator, JsonElement Value);
public sealed record RouteRule(Condition When, string Destination);
public sealed record AssignmentRule(Condition When, string UserId);
public sealed record AssignmentDefinition(string Policy, string[] Members, string? UserId = null, AssignmentRule[]? Rules = null);
public sealed record StageDefinition(string Key, string Name, AssignmentDefinition Assignment, string ApproveTo, string RejectTo, RouteRule[]? ApproveRules = null);
public sealed record StageTask(string Key, string Title, string[] Candidates, string? Assignee, bool Correction, bool Blocked);

public static class TicketJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options) ?? throw new ArgumentException("Missing configuration.");

    public static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }

    public static void ValidateAttributes(TicketDefinition definition, Dictionary<string, JsonElement> attributes)
    {
        Require(attributes.Keys.All(key => definition.Attributes.Any(f => f.Key == key)), "Unknown attribute.");
        foreach (var field in definition.Attributes)
        {
            if (!attributes.TryGetValue(field.Key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                Require(!field.Required, $"{field.Key} is required.");
                continue;
            }
            var valid = field.Type switch
            {
                "string" => value.ValueKind == JsonValueKind.String && (!field.Required || !string.IsNullOrWhiteSpace(value.GetString())),
                "decimal" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "date" => value.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", out _),
                "choice" => value.ValueKind == JsonValueKind.String && (field.Choices ?? []).Contains(value.GetString()),
                _ => false
            };
            Require(valid, $"Invalid {field.Key} ({field.Type}).");
        }
    }

    public static bool Matches(Condition condition, IReadOnlyDictionary<string, JsonElement> attributes)
    {
        if (!attributes.TryGetValue(condition.Attribute, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return false;
        var comparison = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDecimal().CompareTo(condition.Value.GetDecimal()),
            JsonValueKind.String => string.CompareOrdinal(value.GetString(), condition.Value.GetString()),
            JsonValueKind.True or JsonValueKind.False => value.GetBoolean().CompareTo(condition.Value.GetBoolean()),
            _ => throw new ArgumentException("Unsupported condition value.")
        };
        return condition.Operator switch
        {
            "equals" => comparison == 0, "notEquals" => comparison != 0,
            "greaterThan" => comparison > 0, "greaterThanOrEqual" => comparison >= 0,
            "lessThan" => comparison < 0, "lessThanOrEqual" => comparison <= 0,
            _ => throw new ArgumentException("Unknown condition operator.")
        };
    }

    public static async Task ValidateConfigurationAsync(TicketConfiguration configuration, IUserProvider users, CancellationToken cancellationToken = default)
    {
        var definition = configuration.Definition;
        Require(configuration.Version > 0 && definition.Stages.Length > 0, "A version and stages are required.");
        Require(definition.Stages.Select(s => s.Key).Distinct().Count() == definition.Stages.Length, "Duplicate stage keys.");
        Require(definition.Attributes.Select(f => f.Key).Distinct().Count() == definition.Attributes.Length, "Duplicate attribute keys.");
        Require(definition.Stages.Any(s => s.Key == definition.StartStage), "Unknown start stage.");
        bool Destination(string key) => key is "$completed" or "$rejected" or "$requester" || definition.Stages.Any(s => s.Key == key);
        void ValidateCondition(Condition condition)
        {
            var field = definition.Attributes.SingleOrDefault(f => f.Key == condition.Attribute);
            Require(field is not null, "Unknown condition attribute.");
            ValidateAttributes(new("condition", [field! with { Required = true }], "", []), new() { [condition.Attribute] = condition.Value });
            Require(new[] { "equals", "notEquals", "greaterThan", "greaterThanOrEqual", "lessThan", "lessThanOrEqual" }.Contains(condition.Operator), "Unknown condition operator.");
            Require(condition.Operator is "equals" or "notEquals" || field!.Type is "decimal" or "integer" or "date", "Ordered comparisons need a number or date.");
        }
        foreach (var stage in definition.Stages)
        {
            Require(Destination(stage.ApproveTo) && Destination(stage.RejectTo), "Unknown stage destination.");
            Require(stage.ApproveTo != "$requester", "Use rejectTo to return for correction.");
            Require(new[] { "bucket", "specific", "condition" }.Contains(stage.Assignment.Policy), "Supported policies: bucket, specific, condition.");
            foreach (var member in stage.Assignment.Members)
            {
                var user = await users.FindAsync(new UserFilter { Id = member }, cancellationToken);
                Require(user is not null && user.Roles.Contains("Approver"), $"Unknown approver {member}.");
            }
            if (stage.Assignment.Policy == "specific") Require(stage.Assignment.Members.Contains(stage.Assignment.UserId), "Specific assignee must be a candidate.");
            foreach (var rule in stage.Assignment.Rules ?? [])
            {
                ValidateCondition(rule.When);
                Require(stage.Assignment.Members.Contains(rule.UserId), "Rule assignee must be a candidate.");
            }
            foreach (var rule in stage.ApproveRules ?? [])
            {
                ValidateCondition(rule.When);
                Require(Destination(rule.Destination) && rule.Destination != "$requester", "Invalid approval route.");
            }
        }
        // Sequential approval paths must terminate; explicit rejection returns are allowed.
        void Visit(string key, HashSet<string> path)
        {
            if (key is "$completed" or "$rejected") return;
            Require(path.Add(key), "Approval cycles are unsupported.");
            var stage = definition.Stages.Single(s => s.Key == key);
            foreach (var destination in (stage.ApproveRules ?? []).Select(r => r.Destination).Append(stage.ApproveTo)) Visit(destination, new(path));
        }
        Visit(definition.StartStage, []);
    }
}
```

## Appendix B. TicketFlow activity (exact source)

```csharp
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Expressions.Models;
using Elsa.Identity.Contracts;
using Elsa.Identity.Models;
using UserTask = Elsa.UserTasks.Activities.UserTask;
using Elsa.UserTasks.Activities;
using Elsa.UserTasks.Models;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;

namespace IlWorkflow.Api.Tickets;

[Activity("Tickets", "Tickets", "Interprets configurationJson and schedules native Elsa User Tasks.")]
public sealed class TicketFlow : Activity
{
    [Input] public Input<string> ConfigurationJson { get; set; } = null!;
    [JsonIgnore, Port("Review")] public UserTask Review { get; set; }

    private static ActivityExecutionContext Parent(ExpressionExecutionContext context) => context.GetActivityExecutionContext().ParentActivityExecutionContext!;
    private static StageTask State(ExpressionExecutionContext context) => TicketJson.Read<StageTask>(Parent(context).GetProperty<string>("stageTask")!);
    private static ParticipantReference User(string id) => new("", "default", UserTaskParticipantType.User, id);

    public TicketFlow()
    {
        Review = new UserTask
        {
            Id = "review",
            Title = new(ctx => State(ctx).Title),
            Summary = new(ctx => State(ctx).Blocked ? "No eligible assignee. Supervisor assignment required." : "Complete the configured stage action."),
            Reference = new(ctx => Parent(ctx).WorkflowExecutionContext.CorrelationId),
            TaskType = new("ticket"),
            Requester = new(ctx => User(Parent(ctx).GetProperty<string>("requester")!)),
            Assignee = new(ctx => State(ctx).Assignee is { } id ? User(id) : null),
            CandidateUsers = new(ctx => State(ctx).Candidates.Select(User).ToArray()),
            MembershipResolutionMode = new(UserTaskMembershipResolutionMode.Snapshot),
            AllowManagerExclusionOverride = new(true),
            TaskData = new(ctx => JsonDocument.Parse(Parent(ctx).GetProperty<string>("attributes")!).RootElement.Clone()),
            Form = new(ctx => new UserTaskFormReference("ticket-json", State(ctx).Correction ? "correction" : "review", Parent(ctx).GetProperty<string>("configuration"))),
            ActionDefinitions = new(ctx => State(ctx).Correction
                ? [new UserTaskActionDefinition("Resubmit", "Resubmit corrections")]
                : [new UserTaskActionDefinition("Approve", "Approve"), new UserTaskActionDefinition("Reject", "Reject")])
        };
    }

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var configuration = ConfigurationJson.Get(context);
        var ticket = TicketJson.Read<TicketConfiguration>(configuration);
        var users = context.GetRequiredService<IUserProvider>();
        await TicketJson.ValidateConfigurationAsync(ticket, users, context.CancellationToken);
        var attributes = ticket.Definition.Attributes.ToDictionary(f => f.Key, f => JsonSerializer.SerializeToElement(context.WorkflowExecutionContext.Input.GetValueOrDefault(f.Key)));
        TicketJson.ValidateAttributes(ticket.Definition, attributes);
        var requester = context.GetWorkflowInput<string>("requesterId");
        var actor = context.GetRequiredService<IHttpContextAccessor>().HttpContext?.User;
        var actorId = actor?.FindFirstValue(ClaimTypes.NameIdentifier) ?? actor?.FindFirstValue("sub");
        TicketJson.Require(actor?.Identity?.IsAuthenticated == true && (actorId == requester || actor.IsInRole("admin")), "Submit only for your own user ID using synchronous execute.");
        var requesterAccount = await users.FindAsync(new UserFilter { Id = requester }, context.CancellationToken);
        TicketJson.Require(requesterAccount is not null && requesterAccount.Roles.Contains("Requester"), "Unknown requester.");
        context.SetProperty("configuration", configuration);
        context.SetProperty("attributes", TicketJson.Write(attributes));
        context.SetProperty("requester", requester);
        context.SetProperty("stage", ticket.Definition.StartStage);
        context.SetProperty("visit", 0);
        await Schedule(context, false);
    }

    private async ValueTask Schedule(ActivityExecutionContext context, bool correction)
    {
        var ticket = TicketJson.Read<TicketConfiguration>(context.GetProperty<string>("configuration")!);
        var stageKey = context.GetProperty<string>("stage")!;
        if (stageKey is "$completed" or "$rejected")
        {
            var status = stageKey == "$completed" ? "Completed" : "Rejected";
            context.SetProperty("ticketStatus", status);
            context.WorkflowExecutionContext.Output["ticketStatus"] = status;
            context.WorkflowExecutionContext.Output["configurationVersion"] = ticket.Version;
            await context.CompleteActivityAsync();
            return;
        }
        var visit = context.GetProperty<int>("visit") + 1;
        context.SetProperty("visit", visit);
        StageTask state;
        if (correction)
        {
            var requester = context.GetProperty<string>("requester")!;
            state = new("$requester", "Correct and resubmit " + ticket.Definition.Name, [requester], requester, true, false);
        }
        else
        {
            var stage = ticket.Definition.Stages.Single(s => s.Key == stageKey);
            var attributes = TicketJson.Read<Dictionary<string, JsonElement>>(context.GetProperty<string>("attributes")!);
            var assignee = stage.Assignment.Policy switch
            {
                "specific" => stage.Assignment.UserId,
                "condition" => (stage.Assignment.Rules ?? []).FirstOrDefault(r => TicketJson.Matches(r.When, attributes))?.UserId,
                _ => null
            };
            var blocked = stage.Assignment.Members.Length == 0 || stage.Assignment.Policy != "bucket" && assignee is null;
            state = new(stage.Key, stage.Name, blocked ? [] : stage.Assignment.Members, assignee, false, blocked);
        }
        context.SetProperty("stageTask", TicketJson.Write(state));
        context.SetProperty("ticketStatus", correction ? "Returned" : state.Blocked ? "AssignmentBlocked" : "InReview");
        context.GetRequiredService<ILogger<TicketFlow>>().LogInformation("WorkflowId={WorkflowId} Stage={Stage} Visit={Visit} AssignmentBlocked={Blocked}", context.WorkflowExecutionContext.Id, state.Key, visit, state.Blocked);
        await context.ScheduleActivityAsync(Review, OnCompleted);
    }

    private async ValueTask OnCompleted(ActivityCompletedContext completed)
    {
        var context = completed.TargetContext;
        var stimulus = context.GetWorkflowInput<UserTaskStimulus>(UserTask.InputKey);
        var ticket = TicketJson.Read<TicketConfiguration>(context.GetProperty<string>("configuration")!);
        var state = TicketJson.Read<StageTask>(context.GetProperty<string>("stageTask")!);
        if (state.Correction)
        {
            TicketJson.Require(stimulus.ActionKey == "Resubmit", "Expected Resubmit.");
            var attributes = stimulus.CompletionData!.Value.GetProperty("attributes").Deserialize<Dictionary<string, JsonElement>>(TicketJson.Options)!;
            TicketJson.ValidateAttributes(ticket.Definition, attributes);
            context.SetProperty("attributes", TicketJson.Write(attributes));
            await Schedule(context, false);
            return;
        }
        var stage = ticket.Definition.Stages.Single(s => s.Key == state.Key);
        var values = TicketJson.Read<Dictionary<string, JsonElement>>(context.GetProperty<string>("attributes")!);
        var destination = stimulus.ActionKey == "Approve"
            ? (stage.ApproveRules ?? []).FirstOrDefault(r => TicketJson.Matches(r.When, values))?.Destination ?? stage.ApproveTo
            : stage.RejectTo;
        if (destination != "$requester") context.SetProperty("stage", destination);
        await Schedule(context, destination == "$requester");
    }
}
```

## Appendix C. Form provider (exact source)

```csharp
using System.Text.Json;
using Elsa.UserTasks.Contracts;
using Elsa.UserTasks.Models;

namespace IlWorkflow.Api.Tickets;

public sealed class TicketFormProvider : IUserTaskFormProvider
{
    public string Name => "ticket-json";

    public Task<ResolvedUserTaskForm?> ResolveAsync(UserTaskFormReference reference, CancellationToken cancellationToken = default)
    {
        var configuration = TicketJson.Read<TicketConfiguration>(reference.Binding!);
        return Task.FromResult<ResolvedUserTaskForm?>(new(reference, configuration.Version.ToString(), new Dictionary<string, object?>())
        {
            Fields = reference.Key == "correction"
                ? configuration.Definition.Attributes.Select(f => new UserTaskFormFieldDescriptor(f.Key, f.Key, f.Type, f.Required)).ToArray()
                : [new UserTaskFormFieldDescriptor("reason", "Reason (required for rejection)")]
        });
    }

    public Task<UserTaskFormValidationResult> ValidateAndNormalizeAsync(ResolvedUserTaskForm form, string actionKey, JsonElement data, CancellationToken cancellationToken = default)
    {
        try
        {
            TicketJson.Require(data.ValueKind == JsonValueKind.Object, "Supply a JSON data object.");
            if (form.Requested.Key == "correction")
            {
                TicketJson.Require(actionKey == "Resubmit" && data.TryGetProperty("attributes", out _), "Supply attributes with Resubmit.");
                TicketJson.Require(data.EnumerateObject().All(p => p.Name == "attributes"), "Unknown correction field.");
                var configuration = TicketJson.Read<TicketConfiguration>(form.Requested.Binding!);
                TicketJson.ValidateAttributes(configuration.Definition, data.GetProperty("attributes").Deserialize<Dictionary<string, JsonElement>>(TicketJson.Options)!);
            }
            else
            {
                TicketJson.Require(actionKey is "Approve" or "Reject", "Unknown review action.");
                TicketJson.Require(data.EnumerateObject().All(p => p.Name is "reason" or "comment"), "Only reason/comment are accepted during review; attributes are frozen.");
                if (actionKey == "Reject")
                    TicketJson.Require(data.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(reason.GetString()), "Rejection requires a reason.");
                foreach (var field in data.EnumerateObject()) TicketJson.Require(field.Value.ValueKind == JsonValueKind.String, "Reason/comment must be strings.");
            }
            return Task.FromResult(new UserTaskFormValidationResult(true, data.Clone()));
        }
        catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException)
        {
            return Task.FromResult(new UserTaskFormValidationResult(false, Errors: [error.Message]));
        }
    }
}
```

## Appendix D. Research host reference (exact source)

Reference only: merge the necessary registrations into the receiving host. Do not overwrite its authentication or database setup with this entire file.

```csharp
using System.Diagnostics;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Elsa.Extensions;
using Elsa.Persistence.EFCore.Extensions;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Persistence.EFCore.Modules.Runtime;

using Npgsql;

using Elsa.Workflows.Activities.Flowchart.Extensions;
using Elsa.Persistence.EFCore.Modules.Identity;
using Elsa.UserTasks.Contracts;
using Elsa.UserTasks.Activities;
using Elsa.UserTasks.Services;
using Elsa.UserTasks.Persistence.EFCore.Extensions;
using Elsa.UserTasks.Persistence.EFCore.PostgreSql.Extensions;
using IlWorkflow.Api.Tickets;

var builder = WebApplication.CreateBuilder(args);
var useConfigurationIdentity = builder.Environment.IsDevelopment() && builder.Configuration.GetValue("Identity:UseConfigurationProviders", true);
if (useConfigurationIdentity) builder.Configuration.AddJsonFile("Samples/identity.json", optional: false, reloadOnChange: true);
var configuration = builder.Configuration;
var swaggerPreview = builder.Environment.IsDevelopment() && configuration.GetValue<bool>("SwaggerPreview");
var connectionString = configuration.GetConnectionString("Elsa")!;
var signingKey = configuration["Identity:Tokens:SigningKey"];
if (swaggerPreview && string.IsNullOrWhiteSpace(signingKey))
    configuration["Identity:Tokens:SigningKey"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
if (!swaggerPreview && (string.IsNullOrWhiteSpace(signingKey) || signingKey.Length < 32))
    throw new InvalidOperationException("Set Identity:Tokens:SigningKey to a random secret of at least 32 characters using dotnet user-secrets.");
if (!swaggerPreview && string.IsNullOrWhiteSpace(configuration["Identity:Admin:Password"]))
    throw new InvalidOperationException("Set Identity:Admin:Password using dotnet user-secrets.");

builder.Services.AddElsa(elsa =>
{
    elsa.UseIdentity(identity =>
        {
            identity.TokenOptions += options => configuration.GetSection("Identity:Tokens").Bind(options);
            if (useConfigurationIdentity)
            {
                identity.UseConfigurationBasedUserProvider(options => configuration.GetSection("Identity").Bind(options));
                identity.UseConfigurationBasedRoleProvider(options => configuration.GetSection("Identity").Bind(options));
            }
            if (!swaggerPreview) identity.UseEntityFrameworkCore(ef => ef.UsePostgreSql(connectionString));
            if (!useConfigurationIdentity) identity.UseDefaultAdmin(admin => admin
                .WithAdminUserName(configuration["Identity:Admin:UserName"] ?? "admin")
                .WithAdminRoleName("admin")
                .WithAdminPassword(configuration["Identity:Admin:Password"]!));
        })
        .UseDefaultAuthentication()
        .UseWorkflowManagement(management => { if (!swaggerPreview) management.UseEntityFrameworkCore(ef => ef.UsePostgreSql(connectionString)); })
        .UseWorkflowRuntime(runtime => { if (!swaggerPreview) runtime.UseEntityFrameworkCore(ef => ef.UsePostgreSql(connectionString)); })
        .UseWorkflowsApi()
        .UseUserTasks(tasks =>
        {
            if (!swaggerPreview) tasks.UseEntityFrameworkCore(ef => ef.UsePostgreSql(connectionString));
        })
        .UseFlowchart(flowchart => flowchart.UseTokenBasedExecution())
        .UseJavaScript()
        .AddActivitiesFrom<UserTask>()
        .AddActivitiesFrom<TicketFlow>()
        .UseHttp(http => http.ConfigureHttpOptions = options => configuration.GetSection("Http").Bind(options))
        ;
    if (builder.Environment.IsDevelopment())
        elsa.AddSwagger();
});

// Elsa 3.9 defaults capture the root provider; both services resolve scoped runtime/mediator services.
builder.Services.AddScoped<IUserTaskNotificationSink, DefaultUserTaskNotificationSink>();
builder.Services.AddScoped<IUserTaskWorkflowResumer, DefaultUserTaskWorkflowResumer>();
builder.Services.AddSingleton<IUserTaskFormProvider, TicketFormProvider>();
// Development accounts come from Elsa's configuration providers, rather than store seeding.
if (useConfigurationIdentity)
    builder.Services.Configure<Elsa.Identity.Options.DefaultAdminUserOptions>(options =>
    {
        options.AdminUserName = configuration["Identity:Admin:UserName"] ?? "admin";
        options.AdminPassword = configuration["Identity:Admin:Password"] ?? "";
    });




builder.Services.AddHttpContextAccessor();

builder.Logging.AddSimpleConsole(options =>
{
    options.TimestampFormat = "HH:mm:ss ";
    options.IncludeScopes = true;
});
builder.Services.PostConfigureAll<JwtBearerOptions>(options =>
{
    var previous = options.Events.OnAuthenticationFailed;
    options.Events.OnAuthenticationFailed = async context =>
    {
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Authentication");
        logger.LogWarning("Bearer authentication failed: {FailureType}. Sign in again and use the accessToken in Swagger Authorize. TraceId={TraceId}",
            context.Exception.GetType().Name, context.HttpContext.TraceIdentifier);
        if (previous is not null) await previous(context);
    };
});

var app = builder.Build();
app.UseExceptionHandler(errors => errors.Run(async context =>
{
    context.Response.StatusCode = 500;
    context.Response.Headers["X-Trace-Id"] = context.TraceIdentifier;
    await context.Response.WriteAsJsonAsync(new { Code = "internal_error", Message = "An unexpected error occurred. Check server logs using the trace ID.", TraceId = context.TraceIdentifier });
}));
app.Logger.LogInformation("Storage mode: {StorageMode}", swaggerPreview ? "Swagger preview (in memory)" : "PostgreSQL");
var databaseSettings = new NpgsqlConnectionStringBuilder(connectionString);
app.Logger.LogInformation("Database configured: Host={Host}; Port={Port}; Database={Database}; Username={Username}; SSL={SslMode}. Credentials omitted.",
    databaseSettings.Host, databaseSettings.Port, databaseSettings.Database, databaseSettings.Username, databaseSettings.SslMode);
if (swaggerPreview)
{
    app.Logger.LogInformation("SwaggerPreview=true: PostgreSQL connections and migrations are skipped. Set SwaggerPreview=false to enable database persistence.");
}
else if (app.Environment.IsDevelopment())
{
    if (databaseSettings.Database != "ilworkflow")
        throw new InvalidOperationException("Development database creation is restricted to ilworkflow.");
    var started = Stopwatch.GetTimestamp();
    try
    {
        var maintenance = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        app.Logger.LogInformation("Opening PostgreSQL maintenance connection to check database {Database}", databaseSettings.Database);
        await using var connection = new NpgsqlConnection(maintenance.ConnectionString);
        await connection.OpenAsync();
        app.Logger.LogInformation("PostgreSQL connection established in {ElapsedMs:F1} ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        exists.Parameters.AddWithValue("name", databaseSettings.Database);
        if (await exists.ExecuteScalarAsync() is null)
        {
            app.Logger.LogInformation("Creating database ilworkflow");
            await using var create = new NpgsqlCommand("CREATE DATABASE ilworkflow", connection);
            try { await create.ExecuteNonQueryAsync(); }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DuplicateDatabase)
            { app.Logger.LogInformation("Database ilworkflow was created by another process"); }
        }
        else app.Logger.LogInformation("Database ilworkflow already exists");
        app.Logger.LogInformation("Database initialization complete. Elsa will apply management and runtime migrations during startup.");
    }
    catch (Exception exception)
    {
        app.Logger.LogError("PostgreSQL initialization failed: {FailureType}; SQLSTATE={SqlState}; Host={Host}; Port={Port}; Database={Database}. Check PostgreSQL readiness, credentials and database permissions.",
            exception.GetType().Name, (exception as PostgresException)?.SqlState, databaseSettings.Host, databaseSettings.Port, databaseSettings.Database);
        throw;
    }
}
app.Lifetime.ApplicationStarted.Register(() => app.Logger.LogInformation("Application ready. Storage mode: {StorageMode}", swaggerPreview ? "in memory" : "PostgreSQL; Elsa startup migrations completed"));

app.Use(async (context, next) =>
{
    var started = Stopwatch.GetTimestamp();
    using var scope = app.Logger.BeginScope("TraceId={TraceId}", context.TraceIdentifier);
    context.Response.Headers["X-Trace-Id"] = context.TraceIdentifier;
    try
    {
        await next(context);
    }
    catch (Exception exception)
    {
        if (!context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        app.Logger.LogError(exception, "Unhandled error for {Method} {Path}", context.Request.Method, context.Request.Path);
        throw;
    }
    finally
    {
        app.Logger.LogInformation("{Method} {Path} => {StatusCode} in {ElapsedMs:F1} ms",
            context.Request.Method, context.Request.Path, context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (context.Response.StatusCode == StatusCodes.Status401Unauthorized)
            app.Logger.LogWarning("401: Authorization header present={HeaderPresent}. POST /elsa/api/identity/login, then paste accessToken into Swagger Authorize (JWT bearer field). Tokens from a different signing key or expired tokens must be replaced.",
                context.Request.Headers.ContainsKey("Authorization"));
        if (context.Response.StatusCode == StatusCodes.Status403Forbidden)
            app.Logger.LogWarning("403: Request denied by authorization. Authenticated={Authenticated}; check the account permissions.", context.User.Identity?.IsAuthenticated == true);
    }
});
app.MapWorkflowsApi();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseWorkflows();
if (app.Environment.IsDevelopment())
{
    app.UseSwaggerUI();
    app.MapGet("/", () => Results.Redirect("/swagger"));
}
await app.RunAsync();
```

## Appendix E. Development identity fixture

Only for an isolated research host. These are sample password hashes for the documented development credentials. Prefer the receiving application's identity system in an existing app. Preserve native permission checks; merely adding a role name without its Elsa permissions is not sufficient.

```json
{
  "Identity": {
    "Users": [
      {
        "id": "admin",
        "hashedPasswordSalt": "q5Xa8BRdFKVZe5KvHm3Oa48039X4120PM6zC0dsGFcc=",
        "hashedPassword": "cGJrZGYyLXNoYTI1NiQ2MDAwMDAkdnNFWUJuZWcvWjkzVlpWRGZYVVRmNlNjWHVoMThiV0doMVRNY3pwNjZTQT0=",
        "name": "admin",
        "roles": [
          "admin"
        ]
      },
      {
        "id": "configurator",
        "hashedPasswordSalt": "sQ9wVMww6fUz8GmRUAlCg+4jexqg/ngDh9PEJxSX4og=",
        "hashedPassword": "cGJrZGYyLXNoYTI1NiQ2MDAwMDAkL2xHc0U0L3h0Y00yZ212NUYxU1ZiYTEvS3VwWHJBSEhBOGdkNGY1dnRNND0=",
        "name": "configurator",
        "roles": [
          "Configurator"
        ]
      },
      {
        "id": "requester",
        "hashedPasswordSalt": "sQ9wVMww6fUz8GmRUAlCg+4jexqg/ngDh9PEJxSX4og=",
        "hashedPassword": "cGJrZGYyLXNoYTI1NiQ2MDAwMDAkL2xHc0U0L3h0Y00yZ212NUYxU1ZiYTEvS3VwWHJBSEhBOGdkNGY1dnRNND0=",
        "name": "requester",
        "roles": [
          "Requester"
        ]
      },
      {
        "id": "alice",
        "hashedPasswordSalt": "sQ9wVMww6fUz8GmRUAlCg+4jexqg/ngDh9PEJxSX4og=",
        "hashedPassword": "cGJrZGYyLXNoYTI1NiQ2MDAwMDAkL2xHc0U0L3h0Y00yZ212NUYxU1ZiYTEvS3VwWHJBSEhBOGdkNGY1dnRNND0=",
        "name": "alice",
        "roles": [
          "Approver"
        ]
      },
      {
        "id": "amy",
        "hashedPasswordSalt": "sQ9wVMww6fUz8GmRUAlCg+4jexqg/ngDh9PEJxSX4og=",
        "hashedPassword": "cGJrZGYyLXNoYTI1NiQ2MDAwMDAkL2xHc0U0L3h0Y00yZ212NUYxU1ZiYTEvS3VwWHJBSEhBOGdkNGY1dnRNND0=",
        "name": "amy",
        "roles": [
          "Approver"
        ]
      },
      {
        "id": "bob",
        "hashedPasswordSalt": "sQ9wVMww6fUz8GmRUAlCg+4jexqg/ngDh9PEJxSX4og=",
        "hashedPassword": "cGJrZGYyLXNoYTI1NiQ2MDAwMDAkL2xHc0U0L3h0Y00yZ212NUYxU1ZiYTEvS3VwWHJBSEhBOGdkNGY1dnRNND0=",
        "name": "bob",
        "roles": [
          "Approver"
        ]
      },
      {
        "id": "bella",
        "hashedPasswordSalt": "sQ9wVMww6fUz8GmRUAlCg+4jexqg/ngDh9PEJxSX4og=",
        "hashedPassword": "cGJrZGYyLXNoYTI1NiQ2MDAwMDAkL2xHc0U0L3h0Y00yZ212NUYxU1ZiYTEvS3VwWHJBSEhBOGdkNGY1dnRNND0=",
        "name": "bella",
        "roles": [
          "Approver"
        ]
      },
      {
        "id": "clara",
        "hashedPasswordSalt": "sQ9wVMww6fUz8GmRUAlCg+4jexqg/ngDh9PEJxSX4og=",
        "hashedPassword": "cGJrZGYyLXNoYTI1NiQ2MDAwMDAkL2xHc0U0L3h0Y00yZ212NUYxU1ZiYTEvS3VwWHJBSEhBOGdkNGY1dnRNND0=",
        "name": "clara",
        "roles": [
          "Approver"
        ]
      },
      {
        "id": "dan",
        "hashedPasswordSalt": "sQ9wVMww6fUz8GmRUAlCg+4jexqg/ngDh9PEJxSX4og=",
        "hashedPassword": "cGJrZGYyLXNoYTI1NiQ2MDAwMDAkL2xHc0U0L3h0Y00yZ212NUYxU1ZiYTEvS3VwWHJBSEhBOGdkNGY1dnRNND0=",
        "name": "dan",
        "roles": [
          "Approver"
        ]
      },
      {
        "id": "supervisor",
        "hashedPasswordSalt": "sQ9wVMww6fUz8GmRUAlCg+4jexqg/ngDh9PEJxSX4og=",
        "hashedPassword": "cGJrZGYyLXNoYTI1NiQ2MDAwMDAkL2xHc0U0L3h0Y00yZ212NUYxU1ZiYTEvS3VwWHJBSEhBOGdkNGY1dnRNND0=",
        "name": "supervisor",
        "roles": [
          "Supervisor"
        ]
      }
    ],
    "Roles": [
      {
        "id": "Supervisor",
        "permissions": [
          "user-tasks:view",
          "user-tasks:assign",
          "user-tasks:supervise"
        ],
        "name": "Supervisor"
      },
      {
        "id": "Requester",
        "permissions": [
          "user-tasks:view",
          "user-tasks:complete",
          "workflows/definitions:view",
          "workflows/definitions:execute"
        ],
        "name": "Requester"
      },
      {
        "id": "admin",
        "permissions": [
          "*"
        ],
        "name": "admin"
      },
      {
        "id": "Administrator",
        "permissions": [
          "*"
        ],
        "name": "Administrator"
      },
      {
        "id": "Approver",
        "permissions": [
          "user-tasks:view",
          "user-tasks:claim",
          "user-tasks:complete"
        ],
        "name": "Approver"
      },
      {
        "id": "Configurator",
        "permissions": [
          "workflows/definitions:*",
          "workflows/instances:view"
        ],
        "name": "Configurator"
      }
    ]
  }
}
```

## Appendix F. Exact reusable HTTP request collection

```http
# Expense Claim: existing JSON sample accounts; execute requests ONE AT A TIME in order.
# VS Code REST Client extension: named responses supply tokens, IDs and revisions.
# Do not use Send All: completion is asynchronous and optional branches must not run together.
# Other clients: copy the values from the named response manually.
#
# MAIN SEQUENCE: setup -> publish -> execute 6000 -> Alice A -> Bob B -> Clara C -> Dan D -> Finished.
# The alternative rejection/update requests at the bottom are NOT part of that happy path.
# New operationId for a new command. For retry, resend the SAME body/ID/revision;
# {{$guid}} changes on every send, so save the original request before retrying it.
@baseUrl = http://localhost:5000
@adminPassword = password
@samplePassword = Password123!


# DEFAULT MODE: Identity:UseConfigurationProviders=true (current default).
# These users/roles already exist in Samples/identity.json; do not POST-create them again.
# For ALL role/user creation requests followed by the same A-B-C-D flow,
# use Identity-And-ABCD.http with its documented store-backed startup switch.
# Sample user IDs equal names in this mode only.


### 01. Admin login (bootstrap / diagnostic access)
# @name adminLogin
POST {{baseUrl}}/elsa/api/identity/login
Content-Type: application/json

{
  "username": "admin",
  "password": "{{adminPassword}}"
}

### 02. Inspect permission catalog
# Identity list APIs query the writable store; JSON-provided users are not necessarily listed there.
# @name permissionCatalog
GET {{baseUrl}}/elsa/api/identity/permissions
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}


### 03. Configurator login
# @name configuratorLogin
POST {{baseUrl}}/elsa/api/identity/login
Content-Type: application/json

{
  "username": "configurator",
  "password": "{{samplePassword}}"
}

### 04. Publish expense-claim-v1 (configurationJson Literal / String)
# HTTP 200: check workflowDefinition.isPublished. Existing definition publication may create a newer version.
# @name publishClaim
POST {{baseUrl}}/elsa/api/workflow-definitions
Authorization: Bearer {{configuratorLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "publish": true,
  "model": {
    "definitionId": "expense-claim-v1",
    "name": "Expense Claim",
    "description": "configurationJson interpreter backed by native Elsa User Tasks",
    "root": {
      "id": "expense-claim",
      "name": "ExpenseClaim",
      "configurationJson": {
        "expression": {
          "value": "{\"id\":\"expense-claim\",\"version\":1,\"definition\":{\"name\":\"Expense Claim\",\"attributes\":[{\"key\":\"firstName\",\"type\":\"string\",\"required\":true},{\"key\":\"lastName\",\"type\":\"string\",\"required\":true},{\"key\":\"claimAmount\",\"type\":\"decimal\",\"required\":true}],\"startStage\":\"A\",\"stages\":[{\"key\":\"A\",\"name\":\"A: Initial review (Alice/Amy bucket)\",\"assignment\":{\"policy\":\"bucket\",\"members\":[\"alice\",\"amy\"]},\"approveTo\":\"B\",\"rejectTo\":\"$requester\"},{\"key\":\"B\",\"name\":\"B: Claims review (Bella/Bob bucket)\",\"assignment\":{\"policy\":\"bucket\",\"members\":[\"bella\",\"bob\"]},\"approveTo\":\"D\",\"rejectTo\":\"A\",\"approveRules\":[{\"when\":{\"attribute\":\"claimAmount\",\"operator\":\"greaterThan\",\"value\":5000},\"destination\":\"C\"}]},{\"key\":\"C\",\"name\":\"C: High value approval (Clara)\",\"assignment\":{\"policy\":\"condition\",\"members\":[\"clara\"],\"rules\":[{\"when\":{\"attribute\":\"claimAmount\",\"operator\":\"greaterThan\",\"value\":5000},\"userId\":\"clara\"}]},\"approveTo\":\"D\",\"rejectTo\":\"$rejected\"},{\"key\":\"D\",\"name\":\"D: Final approval (Dan)\",\"assignment\":{\"policy\":\"specific\",\"members\":[\"dan\"],\"userId\":\"dan\"},\"approveTo\":\"$completed\",\"rejectTo\":\"$requester\"}]}}",
          "type": "Literal"
        },
        "typeName": "String"
      },
      "type": "Tickets.TicketFlow",
      "version": 1
    },
    "inputs": [
      {
        "name": "requesterId",
        "type": "String"
      },
      {
        "name": "firstName",
        "type": "String"
      },
      {
        "name": "lastName",
        "type": "String"
      },
      {
        "name": "claimAmount",
        "type": "Decimal"
      }
    ]
  }
}

### 05. Requester login
# @name requesterLogin
POST {{baseUrl}}/elsa/api/identity/login
Content-Type: application/json

{
  "username": "requester",
  "password": "{{samplePassword}}"
}

### 06. Submit 6000; save workflowState.id
# Check subStatus=Suspended and no incident. For 4000/5000, skip ALL C requests and go straight from B to D.
# @name startClaim
POST {{baseUrl}}/elsa/api/workflow-definitions/expense-claim-v1/execute
Authorization: Bearer {{requesterLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "correlationId": "CLAIM-{{$guid}}",
  "input": {
    "requesterId": "requester",
    "firstName": "Pranav",
    "lastName": "Shah",
    "claimAmount": 6000
  }
}

### 07. Requester effective permissions
# @name requesterPermissions
GET {{baseUrl}}/elsa/api/identity/me/permissions
Authorization: Bearer {{requesterLogin.response.body.$.accessToken}}


### 08. Stage A: login as alice
# @name aliceLogin
POST {{baseUrl}}/elsa/api/identity/login
Content-Type: application/json

{
  "username": "alice",
  "password": "{{samplePassword}}"
}

### 09. Stage A: find available task; wait/retry until items is nonempty
# Use this new task ID, never the previous stage task ID. If empty after a 202, repeat this GET before continuing.
# @name aTasks
GET {{baseUrl}}/elsa/api/user-tasks?scope=available&workflowInstanceId={{startClaim.response.body.$.workflowState.id}}&includeTotalCount=true
Authorization: Bearer {{aliceLogin.response.body.$.accessToken}}


### 10. Stage A: alice claims bucket ownership
# expectedRevision must be a JSON number after substitution; the collection writes numeric references without quotes.
# @name aClaim
POST {{baseUrl}}/elsa/api/user-tasks/{{aTasks.response.body.$.items[0].id}}/claim
Authorization: Bearer {{aliceLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "expectedRevision": {{aTasks.response.body.$.items[0].revision}},
  "operationId": "{{$guid}}"
}

### 11. Stage A: read latest detail/revision
# @name aDetail
GET {{baseUrl}}/elsa/api/user-tasks/{{aTasks.response.body.$.items[0].id}}
Authorization: Bearer {{aliceLogin.response.body.$.accessToken}}


### 12. Stage A: Approve (or choose its Reject alternative below INSTEAD)
# Expect 202 accepted/Completing, then allow Elsa to resume. D approval ends this claim.
# @name aApprove
POST {{baseUrl}}/elsa/api/user-tasks/{{aTasks.response.body.$.items[0].id}}/complete
Authorization: Bearer {{aliceLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "expectedRevision": {{aDetail.response.body.$.revision}},
  "operationId": "{{$guid}}",
  "actionKey": "Approve",
  "data": {
    "comment": "A reviewed and approved"
  }
}

### 13. Stage B: login as bob
# @name bobLogin
POST {{baseUrl}}/elsa/api/identity/login
Content-Type: application/json

{
  "username": "bob",
  "password": "{{samplePassword}}"
}

### 14. Stage B: find available task; wait/retry until items is nonempty
# Use this new task ID, never the previous stage task ID. If empty after a 202, repeat this GET before continuing.
# @name bTasks
GET {{baseUrl}}/elsa/api/user-tasks?scope=available&workflowInstanceId={{startClaim.response.body.$.workflowState.id}}&includeTotalCount=true
Authorization: Bearer {{bobLogin.response.body.$.accessToken}}


### 15. Stage B: bob claims bucket ownership
# expectedRevision must be a JSON number after substitution; the collection writes numeric references without quotes.
# @name bClaim
POST {{baseUrl}}/elsa/api/user-tasks/{{bTasks.response.body.$.items[0].id}}/claim
Authorization: Bearer {{bobLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "expectedRevision": {{bTasks.response.body.$.items[0].revision}},
  "operationId": "{{$guid}}"
}

### 16. Stage B: read latest detail/revision
# @name bDetail
GET {{baseUrl}}/elsa/api/user-tasks/{{bTasks.response.body.$.items[0].id}}
Authorization: Bearer {{bobLogin.response.body.$.accessToken}}


### 17. Stage B: Approve (or choose its Reject alternative below INSTEAD)
# Expect 202 accepted/Completing, then allow Elsa to resume. D approval ends this claim.
# @name bApprove
POST {{baseUrl}}/elsa/api/user-tasks/{{bTasks.response.body.$.items[0].id}}/complete
Authorization: Bearer {{bobLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "expectedRevision": {{bDetail.response.body.$.revision}},
  "operationId": "{{$guid}}",
  "actionKey": "Approve",
  "data": {
    "comment": "B reviewed and approved"
  }
}

### 18. Stage C: login as clara
# @name claraLogin
POST {{baseUrl}}/elsa/api/identity/login
Content-Type: application/json

{
  "username": "clara",
  "password": "{{samplePassword}}"
}

### 19. Stage C: find assigned task; wait/retry until items is nonempty
# Use this new task ID, never the previous stage task ID. If empty after a 202, repeat this GET before continuing. For C, amount must be > 5000.
# @name cTasks
GET {{baseUrl}}/elsa/api/user-tasks?scope=assigned&workflowInstanceId={{startClaim.response.body.$.workflowState.id}}&includeTotalCount=true
Authorization: Bearer {{claraLogin.response.body.$.accessToken}}


### 20. Stage C: read latest detail/revision
# @name cDetail
GET {{baseUrl}}/elsa/api/user-tasks/{{cTasks.response.body.$.items[0].id}}
Authorization: Bearer {{claraLogin.response.body.$.accessToken}}


### 21. Stage C: Approve (or choose its Reject alternative below INSTEAD)
# Expect 202 accepted/Completing, then allow Elsa to resume. D approval ends this claim.
# @name cApprove
POST {{baseUrl}}/elsa/api/user-tasks/{{cTasks.response.body.$.items[0].id}}/complete
Authorization: Bearer {{claraLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "expectedRevision": {{cDetail.response.body.$.revision}},
  "operationId": "{{$guid}}",
  "actionKey": "Approve",
  "data": {
    "comment": "C reviewed and approved"
  }
}

### 22. Stage D: login as dan
# @name danLogin
POST {{baseUrl}}/elsa/api/identity/login
Content-Type: application/json

{
  "username": "dan",
  "password": "{{samplePassword}}"
}

### 23. Stage D: find assigned task; wait/retry until items is nonempty
# Use this new task ID, never the previous stage task ID. If empty after a 202, repeat this GET before continuing.
# @name dTasks
GET {{baseUrl}}/elsa/api/user-tasks?scope=assigned&workflowInstanceId={{startClaim.response.body.$.workflowState.id}}&includeTotalCount=true
Authorization: Bearer {{danLogin.response.body.$.accessToken}}


### 24. Stage D: read latest detail/revision
# @name dDetail
GET {{baseUrl}}/elsa/api/user-tasks/{{dTasks.response.body.$.items[0].id}}
Authorization: Bearer {{danLogin.response.body.$.accessToken}}


### 25. Stage D: Approve (or choose its Reject alternative below INSTEAD)
# Expect 202 accepted/Completing, then allow Elsa to resume. D approval ends this claim.
# @name dApprove
POST {{baseUrl}}/elsa/api/user-tasks/{{dTasks.response.body.$.items[0].id}}/complete
Authorization: Bearer {{danLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "expectedRevision": {{dDetail.response.body.$.revision}},
  "operationId": "{{$guid}}",
  "actionKey": "Approve",
  "data": {
    "comment": "D reviewed and approved"
  }
}

### 26. Final instance: Finished and output.ticketStatus=Completed
# Poll until Finished; approved business outcome is workflowState.output.ticketStatus=Completed.
# @name finishedInstance
GET {{baseUrl}}/elsa/api/workflow-instances/{{startClaim.response.body.$.workflowState.id}}
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}


### 27. All four stage tasks for this claim
# Completed A/B/C/D in one list is expected. Each is a distinct task in the same workflow instance.
# @name allStageTasks
GET {{baseUrl}}/elsa/api/user-tasks?scope=all&workflowInstanceId={{startClaim.response.body.$.workflowState.id}}&includeTotalCount=true
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}


### 28. A task event history
# @name aEvents
GET {{baseUrl}}/elsa/api/user-tasks/{{aTasks.response.body.$.items[0].id}}/events
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}


### 29. B task event history
# @name bEvents
GET {{baseUrl}}/elsa/api/user-tasks/{{bTasks.response.body.$.items[0].id}}/events
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}


### 30. C task event history
# @name cEvents
GET {{baseUrl}}/elsa/api/user-tasks/{{cTasks.response.body.$.items[0].id}}/events
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}


### 31. D task event history
# @name dEvents
GET {{baseUrl}}/elsa/api/user-tasks/{{dTasks.response.body.$.items[0].id}}/events
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}


### 32. Workflow journal
# @name claimJournal
GET {{baseUrl}}/elsa/api/workflow-instances/{{startClaim.response.body.$.workflowState.id}}/journal
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}


# END MAIN SEQUENCE
# ---------------------------------------------------------------------------
# OPTIONAL ALTERNATIVES: use a NEW claim, stop at the desired active stage,
# then run its Reject request INSTEAD OF the stage's Approve request.
# Do not run these on the already completed happy-path tasks above.
# Refresh that stage's Detail request immediately before its Reject request.
# Re-run startClaim + discovery as necessary; named references track the last response.


### 33. ALTERNATIVE Reject A -> requester correction, then A
# @name aReject
POST {{baseUrl}}/elsa/api/user-tasks/{{aTasks.response.body.$.items[0].id}}/complete
Authorization: Bearer {{aliceLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "expectedRevision": {{aDetail.response.body.$.revision}},
  "operationId": "{{$guid}}",
  "actionKey": "Reject",
  "data": {
    "reason": "A: please correct/review the claim"
  }
}

### 34. ALTERNATIVE Reject B -> new A visit
# @name bReject
POST {{baseUrl}}/elsa/api/user-tasks/{{bTasks.response.body.$.items[0].id}}/complete
Authorization: Bearer {{bobLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "expectedRevision": {{bDetail.response.body.$.revision}},
  "operationId": "{{$guid}}",
  "actionKey": "Reject",
  "data": {
    "reason": "B: please correct/review the claim"
  }
}

### 35. ALTERNATIVE Reject C -> terminal Rejected
# @name cReject
POST {{baseUrl}}/elsa/api/user-tasks/{{cTasks.response.body.$.items[0].id}}/complete
Authorization: Bearer {{claraLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "expectedRevision": {{cDetail.response.body.$.revision}},
  "operationId": "{{$guid}}",
  "actionKey": "Reject",
  "data": {
    "reason": "C: please correct/review the claim"
  }
}

### 36. ALTERNATIVE Reject D -> requester correction, then D
# @name dReject
POST {{baseUrl}}/elsa/api/user-tasks/{{dTasks.response.body.$.items[0].id}}/complete
Authorization: Bearer {{danLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "expectedRevision": {{dDetail.response.body.$.revision}},
  "operationId": "{{$guid}}",
  "actionKey": "Reject",
  "data": {
    "reason": "D: please correct/review the claim"
  }
}

### 37. After A/D rejection: requester finds correction task
# Find title Correct and resubmit Expense Claim. Use its ID, not the rejected stage ID.
# @name correctionTasks
GET {{baseUrl}}/elsa/api/user-tasks?scope=assigned&workflowInstanceId={{startClaim.response.body.$.workflowState.id}}
Authorization: Bearer {{requesterLogin.response.body.$.accessToken}}


### 38. Read correction detail
# @name correctionDetail
GET {{baseUrl}}/elsa/api/user-tasks/{{correctionTasks.response.body.$.items[0].id}}
Authorization: Bearer {{requesterLogin.response.body.$.accessToken}}


### 39. Resubmit corrected attributes as requester
# Same instance resumes the rejected stage. Re-run that stage discovery/detail and approval. Correction at D does NOT replay B/C.
# @name resubmitCorrection
POST {{baseUrl}}/elsa/api/user-tasks/{{correctionTasks.response.body.$.items[0].id}}/complete
Authorization: Bearer {{requesterLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "expectedRevision": {{correctionDetail.response.body.$.revision}},
  "operationId": "{{$guid}}",
  "actionKey": "Resubmit",
  "data": {
    "attributes": {
      "firstName": "Pranav",
      "lastName": "Shah",
      "claimAmount": 4000
    }
  }
}

### 40. After rejection/resubmission: current business outcome
# C rejection: Finished + Rejected. A/D correction: stays active until the resumed stage(s) are approved.
# @name branchInstance
GET {{baseUrl}}/elsa/api/workflow-instances/{{startClaim.response.body.$.workflowState.id}}
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}
```

## Appendix G. Exact API verification script

PowerShell 7. Set BaseUrl to the receiving research server. This writes docs/api-captures.json with issued authentication tokens redacted.

```powershell
param([string]$BaseUrl = 'http://localhost:5007')
$ErrorActionPreference = 'Stop'
$tokens = @{}
$captures = [System.Collections.Generic.List[object]]::new()
function Call([string]$user, [string]$method, [string]$path, $body=$null, [int[]]$expected=@(200), [string]$title='') {
    $parameters=@{Uri="$BaseUrl$path";Method=$method;SkipHttpErrorCheck=$true}
    if($user){$parameters.Headers=@{Authorization="Bearer $($tokens[$user])"}}
    if($null -ne $body){$parameters.ContentType='application/json';$parameters.Body=$body | ConvertTo-Json -Depth 60 -Compress}
    $response=Invoke-WebRequest @parameters
    $result=if($response.Content){$response.Content | ConvertFrom-Json}else{$null}
    if([int]$response.StatusCode -notin $expected){throw "$method $path -> $($response.StatusCode), expected $expected; response: $($response.Content)"}
    $safeBody=if($null -ne $body){$body | ConvertTo-Json -Depth 60 | ConvertFrom-Json}else{$null}
    if($path -eq '/elsa/api/identity/login'){
        $safeBody.password='[development sample password]'
        $safeResult=$result | ConvertTo-Json -Depth 60 | ConvertFrom-Json
        foreach($field in @('accessToken','refreshToken','idToken')){if($safeResult.PSObject.Properties[$field]){$safeResult.$field='[REDACTED]'}}
    }else{$safeResult=$result}
    $captures.Add([ordered]@{title=$title;actor=$user;request=[ordered]@{method=$method;path=$path;authorization=$(if($user){"Bearer <$user-accessToken>"}else{''});body=$safeBody};statusCode=[int]$response.StatusCode;traceId=$response.Headers['X-Trace-Id'];response=$safeResult})
    return $result
}
foreach($user in @('admin','configurator','requester','alice','amy','bella','bob','clara','dan','supervisor')){
    $password=if($user -eq 'admin'){'password'}else{'Password123!'}
    $login=Call '' POST '/elsa/api/identity/login' @{username=$user;password=$password} @(200) "Login $user"
    if(-not $login.accessToken){throw "Login failed: $user"}
    $tokens[$user]=$login.accessToken
}
$definition=Get-Content "$PSScriptRoot/../Samples/Definitions/expense-claim.definition.json" -Raw | ConvertFrom-Json
if($definition.model.root.configurationJson.expression.type -ne 'Literal' -or $definition.model.root.configurationJson.typeName -ne 'String'){throw 'configurationJson must be a literal string'}
Call '' GET '/elsa/api/user-tasks' $null @(401) 'Unauthenticated tasks rejected' | Out-Null
Call 'requester' POST '/elsa/api/workflow-definitions' $definition @(403) 'Requester cannot publish' | Out-Null
Call 'configurator' POST '/elsa/api/workflow-definitions' $definition @(200) 'Publish configurationJson definition' | Out-Null
function StartClaim([int]$amount,[string]$scenario){
    $reference="DOC-$scenario-$([guid]::NewGuid().ToString('N').Substring(0,6))"
    $run=Call 'requester' POST '/elsa/api/workflow-definitions/expense-claim-v1/execute' @{correlationId=$reference;input=@{requesterId='requester';firstName='Pranav';lastName='Shah';claimAmount=$amount}} @(200) "Start $scenario"
    if($run.workflowState.subStatus -ne 'Suspended'){throw "Expected Suspended: $($run | ConvertTo-Json -Depth 20 -Compress)"}
    return $run.workflowState.id
}
function Active([string]$workflow,[string]$prefix){
    for($i=0;$i -lt 30;$i++){
        $list=Call 'admin' GET "/elsa/api/user-tasks?scope=all&workflowInstanceId=$workflow&includeTotalCount=true" $null @(200) "Find $prefix task"
        $task=$list.items | Where-Object {$_.title.StartsWith($prefix) -and $_.status -in @('Available','Assigned')} | Select-Object -First 1
        if($task){return $task}; Start-Sleep -Milliseconds 100
    }; throw "Missing active $prefix in $workflow"
}
function Own($task,[string]$user){
    if($task.status -eq 'Available'){Call $user POST "/elsa/api/user-tasks/$($task.id)/claim" @{expectedRevision=$task.revision;operationId=[guid]::NewGuid().ToString()} @(200) "Claim $($task.title)" | Out-Null}
    return Call $user GET "/elsa/api/user-tasks/$($task.id)" $null @(200) "Read $($task.title)"
}
function Decision($task,[string]$user,[string]$action,$data=@{},[string]$operationId=[guid]::NewGuid().ToString()){
    $body=@{expectedRevision=$task.revision;operationId=$operationId;actionKey=$action;data=$data}
    Call $user POST "/elsa/api/user-tasks/$($task.id)/complete" $body @(200,202) "$action $($task.title)" | Out-Null
    return $body
}
function Approve([string]$workflow,[string]$stage,[string]$user){$task=Own (Active $workflow "$($stage):") $user; Decision $task $user 'Approve' @{comment='Reviewed and approved'} | Out-Null}
function Finish([string]$workflow,[string]$status='Completed'){
    $instance=Call 'admin' GET "/elsa/api/workflow-instances/$workflow" $null @(200) "Finished instance ($status)"
    if($instance.status -ne 'Finished'){throw "Not Finished: $($instance.status)"}
    $state=$instance | ConvertTo-Json -Depth 100 -Compress
    if($state -notmatch ('ticketStatus.{0,30}'+$status)){throw "Business status $status not found in persisted activity properties"}
}
foreach($amount in @(4000,5000,6000)){
    $workflow=StartClaim $amount "approved-$amount"
    $a=Own (Active $workflow 'A:') 'alice'
    Call 'dan' GET "/elsa/api/user-tasks/$($a.id)" $null @(404) 'Unrelated approver cannot read A' | Out-Null
    Call 'alice' POST "/elsa/api/user-tasks/$($a.id)/complete" @{expectedRevision=$a.revision;operationId=[guid]::NewGuid().ToString();actionKey='Reject';data=@{}} @(422) 'Reject without reason is invalid' | Out-Null
    Call 'alice' POST "/elsa/api/user-tasks/$($a.id)/complete" @{expectedRevision=$a.revision;operationId=[guid]::NewGuid().ToString();actionKey='Approve';data=@{attributes=@{claimAmount=1}}} @(422) 'Attributes frozen during review' | Out-Null
    $decision=Decision $a 'alice' 'Approve' @{comment='Initial checks passed'}
    Call 'alice' POST "/elsa/api/user-tasks/$($a.id)/complete" $decision @(200,202) 'Duplicate decision replay' | Out-Null
    Approve $workflow 'B' 'bob'
    if($amount -gt 5000){Approve $workflow 'C' 'clara'}
    Approve $workflow 'D' 'dan'; Finish $workflow
    Call 'alice' GET "/elsa/api/user-tasks/$($a.id)/events" $null @(200) 'Task event history' | Out-Null
    Write-Host "PASS approval $amount"
}
$workflow=StartClaim 6000 'reject-A-correct'
$a=Own (Active $workflow 'A:') 'alice'; Decision $a 'alice' 'Reject' @{reason='Please correct the claim amount'} | Out-Null
$correction=Own (Active $workflow 'Correct') 'requester'
Decision $correction 'requester' 'Resubmit' @{attributes=@{firstName='Pranav';lastName='Shah';claimAmount=4000}} | Out-Null
Approve $workflow 'A' 'amy'; Approve $workflow 'B' 'bella'; Approve $workflow 'D' 'dan'; Finish $workflow
Write-Host 'PASS A rejection -> requester -> corrected A-B-D'
$workflow=StartClaim 4000 'reject-B-back-to-A'; Approve $workflow 'A' 'alice'
$b=Own (Active $workflow 'B:') 'bob'; Decision $b 'bob' 'Reject' @{reason='Initial checks need another review'} | Out-Null
Approve $workflow 'A' 'amy'; Approve $workflow 'B' 'bella'; Approve $workflow 'D' 'dan'; Finish $workflow
Write-Host 'PASS B rejection -> A'
$workflow=StartClaim 6000 'reject-C-terminal'; Approve $workflow 'A' 'alice'; Approve $workflow 'B' 'bob'
$c=Own (Active $workflow 'C:') 'clara'; Decision $c 'clara' 'Reject' @{reason='High-value claim declined'} | Out-Null; Finish $workflow 'Rejected'
Write-Host 'PASS C rejection -> Rejected'
$workflow=StartClaim 4000 'reject-D-correct'; Approve $workflow 'A' 'alice'; Approve $workflow 'B' 'bob'
$d=Own (Active $workflow 'D:') 'dan'; Decision $d 'dan' 'Reject' @{reason='Final details need correction'} | Out-Null
$correction=Own (Active $workflow 'Correct') 'requester'; Decision $correction 'requester' 'Resubmit' @{attributes=@{firstName='Pranav';lastName='Shah';claimAmount=6000}} | Out-Null
$dAgain=Own (Active $workflow 'D:') 'dan'; Decision $dAgain 'dan' 'Approve' @{} | Out-Null; Finish $workflow
Write-Host 'PASS D rejection -> requester -> D (same rejected stage)'
$workflow=StartClaim 4000 'claim-conflict'; $a=Active $workflow 'A:'
$oldRevision=$a.revision; $a=Own $a 'alice'
Call 'amy' POST "/elsa/api/user-tasks/$($a.id)/claim" @{expectedRevision=$oldRevision;operationId=[guid]::NewGuid().ToString()} @(403,404,409) 'Competing/stale claim rejected' | Out-Null
New-Item -ItemType Directory -Force "$PSScriptRoot/../docs" | Out-Null
$captures | ConvertTo-Json -Depth 100 | Set-Content "$PSScriptRoot/../docs/api-captures.json"
Write-Host "PASS: $($captures.Count) API responses captured; tokens redacted"
```

## Companion files

- workflow-requests/expense-claim.definition.json: complete native publication request.
- workflow-requests/expense-claim.configuration.json: readable configuration source.
- workflow-requests/submit.json and login-*.json: development curl body fixtures.
- workflow-requests/Tickets.http: ordered default-fixture workflow.
- workflow-requests/Identity-And-ABCD.http: native role/user creation and full A-B-C-D workflow; run in store-backed identity mode.
- api-captures.json: all 148 original exchanges, authentication secrets redacted.
- Elsa-3.9-Ticket-Workflow-API-Guide.pdf: API-only walkthrough.

All essential source/configuration/request bodies are embedded in this Markdown, so it can be handed to another Codex on its own. Companion files make the curl examples easier to run. No real production secrets are included.

## Appendix H. Role/user API setup and full A-B-C-D HTTP sequence

This is a second, optional identity mode. Start a fresh research instance with:

```powershell
dotnet run --launch-profile http -- --Identity:UseConfigurationProviders=false
```

The native administrator is bootstrapped at startup. Then run the requests below one at a time in VS Code REST Client. Named responses substitute generated user IDs, tokens, task IDs and current revisions. Do not run Reject alternatives after the completed approval path. Store-backed preview data is still ephemeral. The five role creations, nine user creations, generated-ID workflow and all four approvals were verified through 47 actual HTTP requests.

```http
# Expense Claim: API-created accounts; execute requests ONE AT A TIME in order.
# VS Code REST Client extension: named responses supply tokens, IDs and revisions.
# Do not use Send All: completion is asynchronous and optional branches must not run together.
# Other clients: copy the values from the named response manually.
#
# MAIN SEQUENCE: setup -> publish -> execute 6000 -> Alice A -> Bob B -> Clara C -> Dan D -> Finished.
# The alternative rejection/update requests at the bottom are NOT part of that happy path.
# New operationId for a new command. For retry, resend the SAME body/ID/revision;
# {{$guid}} changes on every send, so save the original request before retrying it.
@baseUrl = http://localhost:5000
@adminPassword = password
@samplePassword = Password123!


# REQUIRED API-IDENTITY MODE (leave the default JSON fixture mode unchanged):
# dotnet run --launch-profile http -- --Identity:UseConfigurationProviders=false
# In preview this creates an in-memory admin; all API-created accounts disappear on restart.
# On a fresh server, admin is bootstrapped by Elsa. No anonymous create-admin endpoint exists.
# Use the configured admin password. Create roles/users once; do not rerun POST setup blindly.
# Native POST users accepts name/password/roles; it GENERATES id (not the username).
# All workflow candidate IDs below are taken from the creation responses.


### 01. Admin login (bootstrap / diagnostic access)
# @name adminLogin
POST {{baseUrl}}/elsa/api/identity/login
Content-Type: application/json

{
  "username": "admin",
  "password": "{{adminPassword}}"
}

### 02. Create Supervisor role
# @name createRoleSupervisor
POST {{baseUrl}}/elsa/api/identity/roles
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "id": "Supervisor",
  "permissions": [
    "user-tasks:view",
    "user-tasks:assign",
    "user-tasks:supervise"
  ],
  "name": "Supervisor"
}

### 03. Create Requester role
# @name createRoleRequester
POST {{baseUrl}}/elsa/api/identity/roles
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "id": "Requester",
  "permissions": [
    "user-tasks:view",
    "user-tasks:complete",
    "workflows/definitions:view",
    "workflows/definitions:execute"
  ],
  "name": "Requester"
}

### 04. Create Administrator role
# @name createRoleAdministrator
POST {{baseUrl}}/elsa/api/identity/roles
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "id": "Administrator",
  "permissions": [
    "*"
  ],
  "name": "Administrator"
}

### 05. Create Approver role
# @name createRoleApprover
POST {{baseUrl}}/elsa/api/identity/roles
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "id": "Approver",
  "permissions": [
    "user-tasks:view",
    "user-tasks:claim",
    "user-tasks:complete"
  ],
  "name": "Approver"
}

### 06. Create Configurator role
# @name createRoleConfigurator
POST {{baseUrl}}/elsa/api/identity/roles
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "id": "Configurator",
  "permissions": [
    "workflows/definitions:*",
    "workflows/instances:view"
  ],
  "name": "Configurator"
}

### 07. List stored roles
# @name listRoles
GET {{baseUrl}}/elsa/api/identity/roles
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}


### 08. Create configurator; keep response.id
# @name createConfigurator
POST {{baseUrl}}/elsa/api/identity/users
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "name": "configurator",
  "password": "{{samplePassword}}",
  "roles": [
    "Configurator"
  ]
}

### 09. Create requester; keep response.id
# @name createRequester
POST {{baseUrl}}/elsa/api/identity/users
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "name": "requester",
  "password": "{{samplePassword}}",
  "roles": [
    "Requester"
  ]
}

### 10. Create alice; keep response.id
# @name createAlice
POST {{baseUrl}}/elsa/api/identity/users
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "name": "alice",
  "password": "{{samplePassword}}",
  "roles": [
    "Approver"
  ]
}

### 11. Create amy; keep response.id
# @name createAmy
POST {{baseUrl}}/elsa/api/identity/users
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "name": "amy",
  "password": "{{samplePassword}}",
  "roles": [
    "Approver"
  ]
}

### 12. Create bella; keep response.id
# @name createBella
POST {{baseUrl}}/elsa/api/identity/users
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "name": "bella",
  "password": "{{samplePassword}}",
  "roles": [
    "Approver"
  ]
}

### 13. Create bob; keep response.id
# @name createBob
POST {{baseUrl}}/elsa/api/identity/users
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "name": "bob",
  "password": "{{samplePassword}}",
  "roles": [
    "Approver"
  ]
}

### 14. Create clara; keep response.id
# @name createClara
POST {{baseUrl}}/elsa/api/identity/users
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "name": "clara",
  "password": "{{samplePassword}}",
  "roles": [
    "Approver"
  ]
}

### 15. Create dan; keep response.id
# @name createDan
POST {{baseUrl}}/elsa/api/identity/users
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "name": "dan",
  "password": "{{samplePassword}}",
  "roles": [
    "Approver"
  ]
}

### 16. Create supervisor; keep response.id
# @name createSupervisor
POST {{baseUrl}}/elsa/api/identity/users
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "name": "supervisor",
  "password": "{{samplePassword}}",
  "roles": [
    "Supervisor"
  ]
}

### 17. List stored users (IDs are generated)
# @name listUsers
GET {{baseUrl}}/elsa/api/identity/users
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}


### 18. Configurator login
# @name configuratorLogin
POST {{baseUrl}}/elsa/api/identity/login
Content-Type: application/json

{
  "username": "configurator",
  "password": "{{samplePassword}}"
}

### 19. Publish expense-claim-v1 (configurationJson Literal / String)
# HTTP 200: check workflowDefinition.isPublished. Existing definition publication may create a newer version.
# @name publishClaim
POST {{baseUrl}}/elsa/api/workflow-definitions
Authorization: Bearer {{configuratorLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "publish": true,
  "model": {
    "definitionId": "expense-claim-v1",
    "name": "Expense Claim",
    "description": "configurationJson interpreter backed by native Elsa User Tasks",
    "root": {
      "id": "expense-claim",
      "name": "ExpenseClaim",
      "configurationJson": {
        "expression": {
          "value": "{\"id\":\"expense-claim\",\"version\":1,\"definition\":{\"name\":\"Expense Claim\",\"attributes\":[{\"key\":\"firstName\",\"type\":\"string\",\"required\":true},{\"key\":\"lastName\",\"type\":\"string\",\"required\":true},{\"key\":\"claimAmount\",\"type\":\"decimal\",\"required\":true}],\"startStage\":\"A\",\"stages\":[{\"key\":\"A\",\"name\":\"A: Initial review (Alice/Amy bucket)\",\"assignment\":{\"policy\":\"bucket\",\"members\":[\"{{createAlice.response.body.$.id}}\",\"{{createAmy.response.body.$.id}}\"]},\"approveTo\":\"B\",\"rejectTo\":\"$requester\"},{\"key\":\"B\",\"name\":\"B: Claims review (Bella/Bob bucket)\",\"assignment\":{\"policy\":\"bucket\",\"members\":[\"{{createBella.response.body.$.id}}\",\"{{createBob.response.body.$.id}}\"]},\"approveTo\":\"D\",\"rejectTo\":\"A\",\"approveRules\":[{\"when\":{\"attribute\":\"claimAmount\",\"operator\":\"greaterThan\",\"value\":5000},\"destination\":\"C\"}]},{\"key\":\"C\",\"name\":\"C: High value approval (Clara)\",\"assignment\":{\"policy\":\"condition\",\"members\":[\"{{createClara.response.body.$.id}}\"],\"rules\":[{\"when\":{\"attribute\":\"claimAmount\",\"operator\":\"greaterThan\",\"value\":5000},\"userId\":\"{{createClara.response.body.$.id}}\"}]},\"approveTo\":\"D\",\"rejectTo\":\"$rejected\"},{\"key\":\"D\",\"name\":\"D: Final approval (Dan)\",\"assignment\":{\"policy\":\"specific\",\"members\":[\"{{createDan.response.body.$.id}}\"],\"userId\":\"{{createDan.response.body.$.id}}\"},\"approveTo\":\"$completed\",\"rejectTo\":\"$requester\"}]}}",
          "type": "Literal"
        },
        "typeName": "String"
      },
      "type": "Tickets.TicketFlow",
      "version": 1
    },
    "inputs": [
      {
        "name": "requesterId",
        "type": "String"
      },
      {
        "name": "firstName",
        "type": "String"
      },
      {
        "name": "lastName",
        "type": "String"
      },
      {
        "name": "claimAmount",
        "type": "Decimal"
      }
    ]
  }
}

### 20. Requester login
# @name requesterLogin
POST {{baseUrl}}/elsa/api/identity/login
Content-Type: application/json

{
  "username": "requester",
  "password": "{{samplePassword}}"
}

### 21. Submit 6000; save workflowState.id
# Check subStatus=Suspended and no incident. For 4000/5000, skip ALL C requests and go straight from B to D.
# @name startClaim
POST {{baseUrl}}/elsa/api/workflow-definitions/expense-claim-v1/execute
Authorization: Bearer {{requesterLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "correlationId": "CLAIM-{{$guid}}",
  "input": {
    "requesterId": "{{createRequester.response.body.$.id}}",
    "firstName": "Pranav",
    "lastName": "Shah",
    "claimAmount": 6000
  }
}

### 22. Requester effective permissions
# @name requesterPermissions
GET {{baseUrl}}/elsa/api/identity/me/permissions
Authorization: Bearer {{requesterLogin.response.body.$.accessToken}}


### 23. Stage A: login as alice
# @name aliceLogin
POST {{baseUrl}}/elsa/api/identity/login
Content-Type: application/json

{
  "username": "alice",
  "password": "{{samplePassword}}"
}

### 24. Stage A: find available task; wait/retry until items is nonempty
# Use this new task ID, never the previous stage task ID. If empty after a 202, repeat this GET before continuing.
# @name aTasks
GET {{baseUrl}}/elsa/api/user-tasks?scope=available&workflowInstanceId={{startClaim.response.body.$.workflowState.id}}&includeTotalCount=true
Authorization: Bearer {{aliceLogin.response.body.$.accessToken}}


### 25. Stage A: alice claims bucket ownership
# expectedRevision must be a JSON number after substitution; the collection writes numeric references without quotes.
# @name aClaim
POST {{baseUrl}}/elsa/api/user-tasks/{{aTasks.response.body.$.items[0].id}}/claim
Authorization: Bearer {{aliceLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "expectedRevision": {{aTasks.response.body.$.items[0].revision}},
  "operationId": "{{$guid}}"
}

### 26. Stage A: read latest detail/revision
# @name aDetail
GET {{baseUrl}}/elsa/api/user-tasks/{{aTasks.response.body.$.items[0].id}}
Authorization: Bearer {{aliceLogin.response.body.$.accessToken}}


### 27. Stage A: Approve (or choose its Reject alternative below INSTEAD)
# Expect 202 accepted/Completing, then allow Elsa to resume. D approval ends this claim.
# @name aApprove
POST {{baseUrl}}/elsa/api/user-tasks/{{aTasks.response.body.$.items[0].id}}/complete
Authorization: Bearer {{aliceLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "expectedRevision": {{aDetail.response.body.$.revision}},
  "operationId": "{{$guid}}",
  "actionKey": "Approve",
  "data": {
    "comment": "A reviewed and approved"
  }
}

### 28. Stage B: login as bob
# @name bobLogin
POST {{baseUrl}}/elsa/api/identity/login
Content-Type: application/json

{
  "username": "bob",
  "password": "{{samplePassword}}"
}

### 29. Stage B: find available task; wait/retry until items is nonempty
# Use this new task ID, never the previous stage task ID. If empty after a 202, repeat this GET before continuing.
# @name bTasks
GET {{baseUrl}}/elsa/api/user-tasks?scope=available&workflowInstanceId={{startClaim.response.body.$.workflowState.id}}&includeTotalCount=true
Authorization: Bearer {{bobLogin.response.body.$.accessToken}}


### 30. Stage B: bob claims bucket ownership
# expectedRevision must be a JSON number after substitution; the collection writes numeric references without quotes.
# @name bClaim
POST {{baseUrl}}/elsa/api/user-tasks/{{bTasks.response.body.$.items[0].id}}/claim
Authorization: Bearer {{bobLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "expectedRevision": {{bTasks.response.body.$.items[0].revision}},
  "operationId": "{{$guid}}"
}

### 31. Stage B: read latest detail/revision
# @name bDetail
GET {{baseUrl}}/elsa/api/user-tasks/{{bTasks.response.body.$.items[0].id}}
Authorization: Bearer {{bobLogin.response.body.$.accessToken}}


### 32. Stage B: Approve (or choose its Reject alternative below INSTEAD)
# Expect 202 accepted/Completing, then allow Elsa to resume. D approval ends this claim.
# @name bApprove
POST {{baseUrl}}/elsa/api/user-tasks/{{bTasks.response.body.$.items[0].id}}/complete
Authorization: Bearer {{bobLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "expectedRevision": {{bDetail.response.body.$.revision}},
  "operationId": "{{$guid}}",
  "actionKey": "Approve",
  "data": {
    "comment": "B reviewed and approved"
  }
}

### 33. Stage C: login as clara
# @name claraLogin
POST {{baseUrl}}/elsa/api/identity/login
Content-Type: application/json

{
  "username": "clara",
  "password": "{{samplePassword}}"
}

### 34. Stage C: find assigned task; wait/retry until items is nonempty
# Use this new task ID, never the previous stage task ID. If empty after a 202, repeat this GET before continuing. For C, amount must be > 5000.
# @name cTasks
GET {{baseUrl}}/elsa/api/user-tasks?scope=assigned&workflowInstanceId={{startClaim.response.body.$.workflowState.id}}&includeTotalCount=true
Authorization: Bearer {{claraLogin.response.body.$.accessToken}}


### 35. Stage C: read latest detail/revision
# @name cDetail
GET {{baseUrl}}/elsa/api/user-tasks/{{cTasks.response.body.$.items[0].id}}
Authorization: Bearer {{claraLogin.response.body.$.accessToken}}


### 36. Stage C: Approve (or choose its Reject alternative below INSTEAD)
# Expect 202 accepted/Completing, then allow Elsa to resume. D approval ends this claim.
# @name cApprove
POST {{baseUrl}}/elsa/api/user-tasks/{{cTasks.response.body.$.items[0].id}}/complete
Authorization: Bearer {{claraLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "expectedRevision": {{cDetail.response.body.$.revision}},
  "operationId": "{{$guid}}",
  "actionKey": "Approve",
  "data": {
    "comment": "C reviewed and approved"
  }
}

### 37. Stage D: login as dan
# @name danLogin
POST {{baseUrl}}/elsa/api/identity/login
Content-Type: application/json

{
  "username": "dan",
  "password": "{{samplePassword}}"
}

### 38. Stage D: find assigned task; wait/retry until items is nonempty
# Use this new task ID, never the previous stage task ID. If empty after a 202, repeat this GET before continuing.
# @name dTasks
GET {{baseUrl}}/elsa/api/user-tasks?scope=assigned&workflowInstanceId={{startClaim.response.body.$.workflowState.id}}&includeTotalCount=true
Authorization: Bearer {{danLogin.response.body.$.accessToken}}


### 39. Stage D: read latest detail/revision
# @name dDetail
GET {{baseUrl}}/elsa/api/user-tasks/{{dTasks.response.body.$.items[0].id}}
Authorization: Bearer {{danLogin.response.body.$.accessToken}}


### 40. Stage D: Approve (or choose its Reject alternative below INSTEAD)
# Expect 202 accepted/Completing, then allow Elsa to resume. D approval ends this claim.
# @name dApprove
POST {{baseUrl}}/elsa/api/user-tasks/{{dTasks.response.body.$.items[0].id}}/complete
Authorization: Bearer {{danLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "expectedRevision": {{dDetail.response.body.$.revision}},
  "operationId": "{{$guid}}",
  "actionKey": "Approve",
  "data": {
    "comment": "D reviewed and approved"
  }
}

### 41. Final instance: Finished and output.ticketStatus=Completed
# Poll until Finished; approved business outcome is workflowState.output.ticketStatus=Completed.
# @name finishedInstance
GET {{baseUrl}}/elsa/api/workflow-instances/{{startClaim.response.body.$.workflowState.id}}
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}


### 42. All four stage tasks for this claim
# Completed A/B/C/D in one list is expected. Each is a distinct task in the same workflow instance.
# @name allStageTasks
GET {{baseUrl}}/elsa/api/user-tasks?scope=all&workflowInstanceId={{startClaim.response.body.$.workflowState.id}}&includeTotalCount=true
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}


### 43. A task event history
# @name aEvents
GET {{baseUrl}}/elsa/api/user-tasks/{{aTasks.response.body.$.items[0].id}}/events
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}


### 44. B task event history
# @name bEvents
GET {{baseUrl}}/elsa/api/user-tasks/{{bTasks.response.body.$.items[0].id}}/events
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}


### 45. C task event history
# @name cEvents
GET {{baseUrl}}/elsa/api/user-tasks/{{cTasks.response.body.$.items[0].id}}/events
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}


### 46. D task event history
# @name dEvents
GET {{baseUrl}}/elsa/api/user-tasks/{{dTasks.response.body.$.items[0].id}}/events
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}


### 47. Workflow journal
# @name claimJournal
GET {{baseUrl}}/elsa/api/workflow-instances/{{startClaim.response.body.$.workflowState.id}}/journal
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}


# END MAIN SEQUENCE
# ---------------------------------------------------------------------------
# OPTIONAL ALTERNATIVES: use a NEW claim, stop at the desired active stage,
# then run its Reject request INSTEAD OF the stage's Approve request.
# Do not run these on the already completed happy-path tasks above.
# Refresh that stage's Detail request immediately before its Reject request.
# Re-run startClaim + discovery as necessary; named references track the last response.


### 48. ALTERNATIVE Reject A -> requester correction, then A
# @name aReject
POST {{baseUrl}}/elsa/api/user-tasks/{{aTasks.response.body.$.items[0].id}}/complete
Authorization: Bearer {{aliceLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "expectedRevision": {{aDetail.response.body.$.revision}},
  "operationId": "{{$guid}}",
  "actionKey": "Reject",
  "data": {
    "reason": "A: please correct/review the claim"
  }
}

### 49. ALTERNATIVE Reject B -> new A visit
# @name bReject
POST {{baseUrl}}/elsa/api/user-tasks/{{bTasks.response.body.$.items[0].id}}/complete
Authorization: Bearer {{bobLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "expectedRevision": {{bDetail.response.body.$.revision}},
  "operationId": "{{$guid}}",
  "actionKey": "Reject",
  "data": {
    "reason": "B: please correct/review the claim"
  }
}

### 50. ALTERNATIVE Reject C -> terminal Rejected
# @name cReject
POST {{baseUrl}}/elsa/api/user-tasks/{{cTasks.response.body.$.items[0].id}}/complete
Authorization: Bearer {{claraLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "expectedRevision": {{cDetail.response.body.$.revision}},
  "operationId": "{{$guid}}",
  "actionKey": "Reject",
  "data": {
    "reason": "C: please correct/review the claim"
  }
}

### 51. ALTERNATIVE Reject D -> requester correction, then D
# @name dReject
POST {{baseUrl}}/elsa/api/user-tasks/{{dTasks.response.body.$.items[0].id}}/complete
Authorization: Bearer {{danLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "expectedRevision": {{dDetail.response.body.$.revision}},
  "operationId": "{{$guid}}",
  "actionKey": "Reject",
  "data": {
    "reason": "D: please correct/review the claim"
  }
}

### 52. After A/D rejection: requester finds correction task
# Find title Correct and resubmit Expense Claim. Use its ID, not the rejected stage ID.
# @name correctionTasks
GET {{baseUrl}}/elsa/api/user-tasks?scope=assigned&workflowInstanceId={{startClaim.response.body.$.workflowState.id}}
Authorization: Bearer {{requesterLogin.response.body.$.accessToken}}


### 53. Read correction detail
# @name correctionDetail
GET {{baseUrl}}/elsa/api/user-tasks/{{correctionTasks.response.body.$.items[0].id}}
Authorization: Bearer {{requesterLogin.response.body.$.accessToken}}


### 54. Resubmit corrected attributes as requester
# Same instance resumes the rejected stage. Re-run that stage discovery/detail and approval. Correction at D does NOT replay B/C.
# @name resubmitCorrection
POST {{baseUrl}}/elsa/api/user-tasks/{{correctionTasks.response.body.$.items[0].id}}/complete
Authorization: Bearer {{requesterLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "expectedRevision": {{correctionDetail.response.body.$.revision}},
  "operationId": "{{$guid}}",
  "actionKey": "Resubmit",
  "data": {
    "attributes": {
      "firstName": "Pranav",
      "lastName": "Shah",
      "claimAmount": 4000
    }
  }
}

### 55. After rejection/resubmission: current business outcome
# C rejection: Finished + Rejected. A/D correction: stays active until the resumed stage(s) are approved.
# @name branchInstance
GET {{baseUrl}}/elsa/api/workflow-instances/{{startClaim.response.body.$.workflowState.id}}
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}


### 56. OPTIONAL replace Approver role permissions
# Preserve all intended permissions. Re-login affected users after identity changes.
# @name updateApproverRole
PUT {{baseUrl}}/elsa/api/identity/roles/Approver
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "name": "Approver",
  "permissions": [
    "user-tasks:view",
    "user-tasks:claim",
    "user-tasks:complete"
  ]
}

### 57. OPTIONAL update Amy roles/password
# Path parameter is generated user ID. Supply the full intended role list; login again after changing it.
# @name updateAmy
PUT {{baseUrl}}/elsa/api/identity/users/{{createAmy.response.body.$.id}}
Authorization: Bearer {{adminLogin.response.body.$.accessToken}}
Content-Type: application/json

{
  "password": "{{samplePassword}}",
  "roles": [
    "Approver"
  ]
}
```

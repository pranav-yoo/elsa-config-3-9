# Elsa 3.9 Ticket API research

The application is restored to the tested `expense-claim-v1` flow: approve, reject, return for correction and resubmit through native Elsa APIs. The definition uses a literal-string `configurationJson` input interpreted by the custom `Tickets.TicketFlow` activity. `TicketFormProvider` validates decision/correction payloads. Elsa owns User Tasks, permissions, claiming, revisions, operation IDs, runtime and task events. There are no custom ticket REST controllers or ticket tables.

## Run

```powershell
dotnet restore
dotnet build
dotnet run --launch-profile http
```

Open http://localhost:5000/swagger. Development now uses **PostgreSQL for Elsa Identity** (`Identity:UsePostgreSql=true`, `Identity:UseConfigurationProviders=false`). Native user/role API changes persist in `ilworkflow` and survive restart. Elsa seeds the `admin` account idempotently using `Identity:Admin:Password` (user secrets override the development default). Existing database admin passwords are not reset on startup.

Workflow definitions, instances and tasks still use `SwaggerPreview=true` in-memory storage. Set `SwaggerPreview=false` to persist those too. John's external-token mapping remains in `Samples/external-users.json`; switching Elsa Identity storage does not copy external mappings or JSON fixture accounts into database users.

Run `Setup.ps1` once if connection credentials or the stable Elsa token signing key are missing. Do not change signing keys on every startup.

## API walkthrough

The API walkthrough is available in [Markdown](docs/TICKET-WALKTHROUGH.md). Reusable requests are in [Tickets.http](Tickets.http). Full captured requests/responses, with issued tokens and login passwords redacted, are in [api-captures.json](docs/api-captures.json).

### Ordered role/user setup and A-B-C-D requests

- [Tickets.http](Tickets.http): the existing JSON accounts, in exact login/publish/submit/A/B/C/D/final-status/history order. Uses named responses to carry tokens, IDs and revisions automatically in VS Code REST Client.
- [Identity-And-ABCD.http](Identity-And-ABCD.http): a complete native role/user creation sequence followed by the same workflow. Use a fresh research database for the first run. PostgreSQL identity mode is now the default; Elsa bootstraps the administrator; then the collection creates five roles and nine users through native identity APIs. User IDs are generated and substituted into the literal configurationJson. Repeating creation on an existing store can conflict.
- Both collections are also in `output/documentation/workflow-requests/`. Run one request at a time. After completion returns 202, retry the next task-list request if its items are still empty. Do not run the optional Reject alternatives after already approving those tasks.

The default PostgreSQL identity mode uses native `IUserProvider` and persists API-created accounts/roles. Use `Identity-And-ABCD.http` to create the accounts and use returned generated IDs in workflow definitions. `Tickets.http` targets the old fixed JSON fixture IDs and requires the optional sample mode:

```powershell
dotnet run --launch-profile http -- --Identity:UsePostgreSql=false --Identity:UseConfigurationProviders=true
```

Do not use the old JSON fixture IDs for newly API-created database accounts. John's external token continues to use its existing mapping and user ID.

Verify either collection directly with `python Tests/VerifyHttpSequence.py Identity-And-ABCD.http --base-url http://localhost:5000` (or substitute Tickets.http). This executes only the main sequence, creates research data, and never prints issued tokens.

1. Log in using `POST /elsa/api/identity/login`, then paste `accessToken` into Swagger's JWT bearer Authorize field.
2. As configurator, post [the complete definition](Samples/Definitions/expense-claim.definition.json) to `POST /elsa/api/workflow-definitions`.
3. As requester, execute `POST /elsa/api/workflow-definitions/expense-claim-v1/execute` with `correlationId` and input fields `requesterId`, `firstName`, `lastName`, `claimAmount`.
4. A: Alice/Amy bucket; B: Bella/Bob bucket. Claim the task using its latest revision before completing it.
5. Above 5000, B routes to C (Clara), then D (Dan). At 5000 or below, B routes directly to D.
6. Send `Approve` or `Reject` to the native task complete endpoint. Reject requires `data.reason`. A/D return to requester; B returns to A; C ends as Rejected.
7. Requester submits `Resubmit` with `data.attributes` on the correction task. This resumes the rejected stage. A change at D does not replay B/C.
8. Inspect instance output: Elsa status is Finished for both outcomes; `workflowState.output.ticketStatus` distinguishes Completed from Rejected.

`operationId` identifies one task command; reuse the exact payload/ID only for retries. `expectedRevision` comes from the current task response. A correlation ID does not make workflow starts idempotent.

## Verification

```powershell
./Tests/ConfigurationApi.ps1 -BaseUrl http://localhost:5000
```

This creates research data and captures responses; use a disposable instance. Verified: 4000/5000/6000 approvals, every configured rejection route, correction, duplicate decision replay, stale claim denial, role/task access and validation failures. The final build passes with zero warnings. `Tests/TicketApi.ps1` separately tests the optional native-only, Complete-only research definition; it does not test Approve/Reject.

## PostgreSQL and logs

The host has PostgreSQL persistence configured for Elsa management/runtime/identity/UserTasks, database `ilworkflow`, host `localhost:5432`, username `postgres`. Use `Setup.ps1` or .NET user secrets for credentials/signing key, then set `SwaggerPreview=false`. Development checks/creates `ilworkflow` and Elsa runs migrations. Preview keeps workflow/task stores in memory, but PostgreSQL identity remains enabled by `Identity:UsePostgreSql=true`.

The final restored version was verified in preview mode; PostgreSQL restart durability is not verified. The original service reported recovery mode. Separate stock Elsa 3.9 UserTasks PostgreSQL experiments found TagsJson mapping and nullable JSON read issues. Prior compatibility adapters were removed; do not assume those earlier patched persistence results apply to this version.

Logs contain trace IDs, HTTP status/timing, sanitized database target, workflow ID, stage and visit; the request logger excludes bodies, query strings, passwords and bearer tokens. Swagger is Development-only. No frontend, attachments, notifications, automatic round robin or migration of existing old workflow instances is included.

## Verify PostgreSQL identity across restart

```powershell
./Tests/IdentityPersistence.ps1
# Restart the application, then:
./Tests/IdentityPersistence.ps1 -Verify
```

This creates one research role/user and records their IDs in ignored `App_Data/identity-persistence-check.json`. The check verifies native user/role API results, role membership and password sign-in after restart; tokens are never logged. Verified against local PostgreSQL `ilworkflow` with Elsa's native identity migrations. John's development external token also returned HTTP 200 after the switch.

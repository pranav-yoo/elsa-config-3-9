# Elsa 3.9 Ticket API research

The application is restored to the tested `expense-claim-v1` flow: approve, reject, return for correction and resubmit through native Elsa APIs. The definition uses a literal-string `configurationJson` input interpreted by the custom `Tickets.TicketFlow` activity. `TicketFormProvider` validates decision/correction payloads. Elsa owns User Tasks, permissions, claiming, revisions, operation IDs, runtime and task events. There are no custom ticket REST controllers or ticket tables.

## Run

```powershell
dotnet restore
dotnet build
dotnet run --launch-profile http
```

Open http://localhost:5000/swagger. Development defaults to `SwaggerPreview=true` (in-memory; restart clears state). Sample admin login is `admin` / `password`; all other sample accounts use `Password123!`. Development authentication is supplied by Elsa configuration providers reading `Samples/identity.json`; an admin password in user secrets does not change these sample account hashes.

## API walkthrough

The API-only guide is [PDF](output/pdf/Elsa-3.9-Ticket-Workflow-API-Guide.pdf) or [Markdown](docs/TICKET-WALKTHROUGH.md). Reusable requests are in [Tickets.http](Tickets.http). Full captured requests/responses, with issued tokens and login passwords redacted, are in [api-captures.json](docs/api-captures.json).

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

The host has PostgreSQL persistence configured for Elsa management/runtime/identity/UserTasks, database `ilworkflow`, host `localhost:5432`, username `postgres`. Use `Setup.ps1` or .NET user secrets for credentials/signing key, then set `SwaggerPreview=false`. Development checks/creates `ilworkflow` and Elsa runs migrations. Preview never connects to PostgreSQL.

The final restored version was verified in preview mode; PostgreSQL restart durability is not verified. The original service reported recovery mode. Separate stock Elsa 3.9 UserTasks PostgreSQL experiments found TagsJson mapping and nullable JSON read issues. Prior compatibility adapters were removed; do not assume those earlier patched persistence results apply to this version.

Logs contain trace IDs, HTTP status/timing, sanitized database target, workflow ID, stage and visit; the request logger excludes bodies, query strings, passwords and bearer tokens. Swagger is Development-only. No frontend, attachments, notifications, automatic round robin or migration of existing old workflow instances is included.

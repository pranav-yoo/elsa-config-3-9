# Handoff: ilworkflow task UI (Angular 19)

Goal: a small Angular app for the ilworkflow API (Elsa 3.9, `http://localhost:5000`, Development mode).
A header dropdown lists Elsa users. Picking one creates that user's token and loads their tasks.
Each task opens in a side panel where the user claims, approves, rejects, reassigns or releases it.

This repo folder (`ilworkflow.UI/`) already contains a working implementation. Rebuild it from this spec, or extend it.

## Stack and rules
- Angular 19, standalone components, signals, new control flow (`@if`, `@for`). No NgModules.
- Component-based: every component has its own `.ts`, `.html` and `.scss` (`styleUrl`). SCSS only.
- Shared colours live in `src/styles/_variables.scss` (ICICI theme: orange `#f37021`, maroon `#ae282e`). Components use `@use '../../styles/variables' as *;`.
- `ng serve --proxy-config proxy.conf.json` proxies `/elsa` and `/api` to `http://localhost:5000` (no CORS setup needed).
- Windows PowerShell blocks `npm.ps1`. Use `npm.cmd install` and `npm.cmd start`.

## Components
| Component | Job |
|---|---|
| `AppComponent` | Header, scope tabs, status chips, error box, all API orchestration |
| `UserSelectComponent` | Users dropdown. Input `users`, `selectedId`. Output `userChange` |
| `TaskTableComponent` | Table. Claim button on `Available` rows, Open button on `Assigned` rows. Outputs `claim`, `open` |
| `TaskDetailComponent` | Right-side drawer. Task info, submitted data, action buttons, input box, Submit, Reassign, Release, History |
| `ElsaService` | All HTTP calls and the token creation |

## Authentication (dev only)
1. Admin login: `POST /elsa/api/identity/login` `{username:"admin", password:"password"}` returns `accessToken`.
2. Users: `GET /elsa/api/identity/users` (admin bearer) returns `{users:[{id,name,roles}]}`.
3. Selecting a user: sign a JWT in the browser with Web Crypto (HS256). Header `{alg:"HS256",typ:"JWT"}`, payload `{uid: user.id, name: user.name, iat, exp: now+3600}`, key `dev-only-user-token-signing-key-change-me-0123456789` (appsettings.Development.json `UserToken:SigningKey`). Send it as `Authorization: Bearer <token>`.
4. Production must issue the token on a backend. Never ship the signing key in a browser.

## Task API (`/api/tasks`, all properties and query names are snake_case)
| Call | Notes |
|---|---|
| `GET /api/tasks?scope=&status=&include_total_count=true` | Returns `{items:[...]}`. scope: assigned, available, history, all |
| `GET /api/tasks/{id}` | Detail: `actions` (stage buttons), `allowed_actions`, `revision`, `data`, `form`, `workflow` |
| `GET /api/tasks/{id}/events?limit=50` | History: `{items:[...]}` |
| `POST /api/tasks/{id}/claim` | Body `{expected_revision, operation_id}` |
| `POST /api/tasks/{id}/release` | Same body |
| `POST /api/tasks/{id}/assign` | `{user_id, reason?, expected_revision, operation_id}` |
| `POST /api/tasks/{id}/complete` | `{action_key, data, expected_revision, operation_id}` |

- Commands return 202 (accepted; the next task appears shortly, so wait about 1.2 s then reload) or 409 (conflict; reload and retry).
- `operation_id` is a fresh GUID per call (`crypto.randomUUID()`). `expected_revision` is the task's `revision`; read the task right before sending.
- Responses are snake_case. `ElsaService` converts keys to camelCase with a recursive `camel()` helper, except `data` (ticket attributes such as `claimAmount` keep their names). Components only see camelCase.
- Example task keys after conversion: `id, title, summary, status, assignee{displayName}, workflowInstanceId, createdAt, revision, actions[{key,label}], allowedActions[], data, workflow{definitionName}`.

## Behaviour
- **Tabs** (scope): assigned, available, history, all.
- **Status chips** (all tabs): Assigned, Available, Completing, Completed. A chip is sent as `&status=`. It overrides the tab default (assigned tab sends `Assigned`, available tab sends `Available`). Clicking the active chip clears it.
- **Table columns**: name, status, data (JSON), assignee (show `assignee.displayName`, not the object), workflow instance, created, actions.
- **Claim**: only on `Available` rows. After claiming, switch to the assigned tab.
- **Open** (assigned rows, or click the row): `GET /api/tasks/{id}` then open the drawer. Also load events (ignore failures).
- **Drawer**:
  - Shows summary, workflow name, reference, assignee, revision and the submitted `data` as key/value rows.
  - **Decision**: one button per `actions` entry (`key`, `label`), keys sent exactly as given (for example `Approve`, `Reject`, `REFER_UW`). `allowed_actions` holds permissions (`release`, `complete`, `assign`...). A decision button is enabled when `allowed_actions` contains `complete`. Observers get disabled buttons.
  - Choosing a button shows an input box. Submit stays disabled until a button is chosen.
  - **Submit** reads the task again (fresh revision), posts complete, closes the drawer, waits 1.2 s, reloads the list.
  - **Release** (footer) shows when `allowed_actions` has `release`. **Reassign** (user select + reason) shows when it has `assign`.
  - **History** lists events (type, time, reason).
  - Errors from the server are shown inside the drawer.

## Complete payloads (server validation, `TaskDecisionValidator`)
| Action key (case-insensitive match in UI) | `data` |
|---|---|
| APPROVE | `{}` or `{"comment": "..."}`. No other fields |
| REJECT | `{"reason": "..."}`. Reason is required and non-empty |
| RESUBMIT | `{"attributes": {...}}`. Complete attribute object, nothing else |
| any other key | `{"reason": "..."}` if given, otherwise `{}` |

## Files
```
proxy.conf.json            /elsa and /api -> :5000
angular.json               scss, styles src/styles.scss
src/styles.scss            global reset + body
src/styles/_variables.scss theme tokens
src/main.ts                bootstrapApplication(AppComponent, [provideHttpClient()])
src/app/elsa.service.ts    auth, API calls, camel()
src/app/app.component.*    root
src/app/user-select/*
src/app/task-table/*
src/app/task-detail/*
```

## Run
```powershell
cd ilworkflow.UI
npm.cmd install
npm.cmd start        # http://localhost:4200, API must be running in Development on :5000
```

## Known gaps
- The exact shape of event items is a guess (`type`, `createdAt`, `reason`). Check one real `/events` response and adjust `eventText()`.
- Resubmit takes attributes as JSON text, not a generated form.
- Not tested against the live API yet. The build passes.

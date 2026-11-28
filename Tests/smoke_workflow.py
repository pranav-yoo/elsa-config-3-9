"""End-to-end smoke test for configured ticket workflows.

Runs against a live API backed by PostgreSQL and creates its own users with a random suffix.
    python Tests/smoke_workflow.py [--attributes]
Environment: ILWORKFLOW_BASE_URL (default http://localhost:5000), ILWORKFLOW_ADMIN_PASSWORD (default "password").
"""
import os, sys, time, uuid
import requests

BASE = os.environ.get("ILWORKFLOW_BASE_URL", "http://localhost:5000").rstrip("/")
PW = "Password123!"
ATTRS = "--attributes" in sys.argv
S = requests.Session()
results = []


def check(name, cond, detail=""):
    results.append((name, bool(cond)))
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else f"  -> {detail}"))


def call(method, path, token=None, body=None, expect=None):
    h = {"Authorization": f"Bearer {token}"} if token else {}
    r = S.request(method, BASE + path, json=body, headers=h)
    if expect is not None and r.status_code != expect:
        print(f"   !! {method} {path} -> {r.status_code} (expected {expect}): {r.text[:400]}")
    try:
        return r.status_code, r.json()
    except ValueError:
        return r.status_code, r.text


def login(user, pw=PW):
    code, body = call("POST", "/elsa/api/identity/login", body={"username": user, "password": pw}, expect=200)
    return body["accessToken"]


admin = login("admin", os.environ.get("ILWORKFLOW_ADMIN_PASSWORD", "password"))
roles = {
    "Approver": ["user-tasks:view", "user-tasks:claim", "user-tasks:complete", "workflows/instances:view"],
    "Requester": ["user-tasks:view", "user-tasks:complete", "workflows/definitions:view", "workflows/definitions:execute", "workflows/instances:view"],
    "Configurator": ["workflows/definitions:*", "workflows/instances:view"],
    "workflow_supervisor": ["user-tasks:view", "user-tasks:assign", "workflows/instances:view"],
}
for rid, perms in roles.items():
    call("POST", "/elsa/api/identity/roles", admin, {"id": rid, "name": rid, "permissions": perms})

run = uuid.uuid4().hex[:6]
ids = {}
for name, role in [("requester", "Requester"), ("configurator", "Configurator"), ("alice", "Approver"), ("amy", "Approver"),
                   ("bob", "Approver"), ("clara", "Approver"), ("dan", "Approver"), ("pia", "Approver"), ("quinn", "Approver"),
                   ("sam", "workflow_supervisor")]:
    uname = f"smoke-{name}-{run}"
    code, body = call("POST", "/elsa/api/identity/users", admin, {"name": uname, "password": PW, "roles": [role]}, expect=200)
    ids[name] = body["id"]
tok = {n: login(f"smoke-{n}-{run}") for n in ids}


def action(key, kind, stage=None, reason=False):
    d = {"kind": kind}
    if stage:
        d["stage_key"] = stage
    a = {"key": key, "label": key.title(), "destination": d}
    if reason:
        a["require_reason"] = True
    return a


def stage(key, policy, members, actions, **extra):
    s = {"key": key, "title": f"{key} review", "assignment": {"policy": policy, "members": [ids[m] for m in members]}, "actions": actions}
    s.update(extra)
    return s


def save(defn, setup, token=None, expect=200):
    code, body = call("POST", "/api/workflow-configurations", token or tok["configurator"],
                      {"workflow_definition_id": defn, "configuration": setup}, expect=expect)
    return code, body


def publish(defn, version):
    return call("POST", f"/api/workflow-configurations/{defn}/versions/{version}/publish", tok["configurator"], expect=200)


def wait_task(instance, user, scope="assigned", tries=40):
    for _ in range(tries):
        code, body = call("GET", f"/elsa/api/user-tasks?scope={scope}&workflowInstanceId={instance}", tok[user])
        items = [t for t in (body.get("items", []) if isinstance(body, dict) else []) if t["status"] in ("Assigned", "Available", "Unassigned")]
        if items:
            return items[0]
        time.sleep(0.25)
    return None


def detail(task_id, user):
    return call("GET", f"/elsa/api/user-tasks/{task_id}", tok[user])[1]


def complete(task_id, user, key, data=None, expect=None):
    d = detail(task_id, user)
    return call("POST", f"/elsa/api/user-tasks/{task_id}/complete", tok[user],
                {"expectedRevision": d["revision"], "operationId": str(uuid.uuid4()), "actionKey": key, "data": data or {}}, expect=expect)


def claim(task_id, user):
    d = detail(task_id, user)
    return call("POST", f"/elsa/api/user-tasks/{task_id}/claim", tok[user],
                {"expectedRevision": d["revision"], "operationId": str(uuid.uuid4())}, expect=None)


def outcome(instance, tries=40):
    for _ in range(tries):
        code, body = call("GET", f"/elsa/api/workflow-instances/{instance}", tok["requester"])
        out = (body.get("workflowState") or {}).get("output", {}) if isinstance(body, dict) else {}
        if out.get("ticketOutcome") in ("Approved", "Rejected"):
            return out
        time.sleep(0.25)
    return out


def start(defn, attributes=None, expect=200):
    code, body = call("POST", f"/elsa/api/workflow-definitions/{defn}/execute", tok["requester"],
                      {"correlationId": "SMOKE-" + uuid.uuid4().hex[:8], "input": {"attributes": attributes or {}}}, expect=expect)
    return code, body


# ---------- configuration ----------
defn = f"smoke-expense-{run}"
setup = {
    "name": "Smoke expense",
    "start_stage": "A",
    "supervisors": [ids["sam"]],
    "stages": [
        stage("A", "Bucket", ["alice", "amy"], [action("APPROVE", "Stage", "B"), action("REJECT", "Correction", "A", True)]),
        stage("B", "Bucket", ["bob"], [action("APPROVE", "Stage", "D"), action("REJECT", "Stage", "A", True),
                                        action("REFER_UW", "Stage", "C", True)], viewable_by_stages=["D"]),
        stage("C", "Bucket", ["clara"], [action("APPROVE", "Stage", "D"), action("REJECT", "Rejected", reason=True)]),
        {**stage("D", "Specific", ["dan"], [action("APPROVE", "Approved"), action("REJECT", "Correction", "D", True)])},
    ],
}
code, body = save(defn, setup)
check("create configuration", code == 200, body)
version = body["data"]["workflow_version"]
bad = {**setup, "start_stage": "ZZ"}
check("invalid start stage -> 400", save(defn + "-bad", bad, expect=400)[0] == 400)
code, body = publish(defn, version)
check("publish", code == 200 and body["data"]["is_published"], body)

# ---------- happy path with a custom action ----------
code, body = start(defn, {"claimAmount": 6000} if ATTRS else None)
check("execute", code == 200, body)
inst = body["workflowState"]["id"]
t = wait_task(inst, "alice", "available")
check("A task available to bucket", t is not None)
claim(t["id"], "alice")
check("A approve", complete(t["id"], "alice", "APPROVE", {"comment": "ok"})[0] in (200, 202))
t = wait_task(inst, "bob")
check("B auto-assigned (single member bucket)", t is not None)
check("D member observes B task", any(x["id"] == t["id"] for x in call("GET", f"/api/tasks?scope=observed&workflow_instance_id={inst}", tok["dan"])[1]["data"]["items"]))
check("supervisor sees B task", any(x["id"] == t["id"] for x in call("GET", f"/api/tasks?scope=supervised&workflow_instance_id={inst}", tok["sam"])[1]["data"]["items"]))
r = complete(t["id"], "bob", "REFER_UW"); check("custom action without reason -> rejected", r[0] in (400, 422))
r = complete(t["id"], "bob", "NOPE"); check("unknown action -> rejected", r[0] in (400, 422))
check("B REFER_UW", complete(t["id"], "bob", "REFER_UW", {"reason": "big"})[0] in (200, 202))
t = wait_task(inst, "clara")
check("C reached via custom action", t is not None)
complete(t["id"], "clara", "APPROVE")
t = wait_task(inst, "dan")
check("D specific assignee", t is not None)
complete(t["id"], "dan", "REJECT", {"reason": "fix"})
t = wait_task(inst, "requester")
check("D reject -> requester correction", t is not None)
check("resubmit", complete(t["id"], "requester", "RESUBMIT", {"attributes": {}})[0] in (200, 202))
t = wait_task(inst, "dan")
check("correction returns to D", t is not None)
complete(t["id"], "dan", "APPROVE")
out = outcome(inst)
check("ticket Approved", out.get("ticketOutcome") == "Approved", out)
check("attribute revision incremented", str(out.get("attributeRevision")) == "2", out)
if ATTRS:
    hist = out.get("attributeHistory")
    hist = hist.get("$values", hist) if isinstance(hist, dict) else hist
    check("submitted attributes kept in history", hist and hist[0]["attributes"].get("claimAmount") == 6000, hist)

# ---------- rejection to terminal ----------
code, body = start(defn)
inst = body["workflowState"]["id"]
t = wait_task(inst, "alice", "available"); claim(t["id"], "alice"); complete(t["id"], "alice", "APPROVE")
t = wait_task(inst, "bob"); complete(t["id"], "bob", "REFER_UW", {"reason": "r"})
t = wait_task(inst, "clara"); complete(t["id"], "clara", "REJECT", {"reason": "no"})
check("C reject -> Rejected", outcome(inst).get("ticketOutcome") == "Rejected")

# ---------- parallel group ----------
pdef = f"smoke-parallel-{run}"
psetup = {
    "name": "Smoke parallel", "start_stage": "P",
    "stages": [
        stage("P", "Bucket", ["pia"], [action("APPROVE", "Approved"), action("REJECT", "Rejected", reason=True)], parallel_with=["Q"]),
        stage("Q", "Bucket", ["quinn"], [action("APPROVE", "Approved"), action("REJECT", "Rejected", reason=True)]),
    ],
}
code, body = save(pdef, psetup); check("parallel config", code == 200, body)
publish(pdef, body["data"]["workflow_version"])
code, body = start(pdef); inst = body["workflowState"]["id"]
tp, tq = wait_task(inst, "pia"), wait_task(inst, "quinn")
check("parallel opens both tasks", tp is not None and tq is not None)
complete(tp["id"], "pia", "REJECT", {"reason": "no"})
time.sleep(0.5)
check("one reject keeps group open", wait_task(inst, "quinn") is not None)
complete(tq["id"], "quinn", "APPROVE")
check("parallel: approval wins", outcome(inst).get("ticketOutcome") == "Approved")

failed = [n for n, ok in results if not ok]
print(f"\n{len(results) - len(failed)}/{len(results)} passed")
sys.exit(1 if failed else 0)

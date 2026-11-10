"""Live GET-only master/JWT checks; no tokens or signing keys are written to disk.
Run once, restart the server, then rerun with --verify-restart.
"""
import argparse, base64, hashlib, hmac, json, subprocess, time, uuid
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen

parser = argparse.ArgumentParser()
parser.add_argument('--base-url', default='http://localhost:5000')
parser.add_argument('--verify-restart', action='store_true')
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
state_file = root / 'App_Data/demo-master-check.json'
secret_output = subprocess.check_output(['dotnet', 'user-secrets', 'list', '--json', '--project', str(root / 'ilworkflow.API/ilworkflow.API.csproj')], text=True)
# The .NET CLI surrounds JSON with //BEGIN and //END markers.
secrets = json.loads('\n'.join(line for line in secret_output.splitlines() if not line.lstrip().startswith('//')))
key = secrets['ExternalAuth:DevelopmentTokens:SigningKey'].encode()

def api(method, path, body=None, token=None, expected=(200, 201, 202)):
    headers = {'Authorization': 'Bearer ' + token} if token else {}
    if body is not None: headers['Content-Type'] = 'application/json'
    try:
        with urlopen(Request(args.base_url + path, data=json.dumps(body).encode() if body is not None else None, headers=headers, method=method), timeout=30) as response:
            status, raw = response.status, response.read().decode()
    except HTTPError as error:
        status, raw = error.code, error.read().decode()
    assert status in expected, f'{method} {path}: HTTP {status}: {raw[:1200]}'
    return json.loads(raw) if raw and raw.lstrip().startswith(('{', '[')) else None

def token(provider, identity, **overrides):
    now = int(time.time())
    claim = 'name' if provider == 'username-demo' else 'email'
    payload = {'sub': str(uuid.uuid4()), claim: identity, 'iss': 'ilworkflow-demo-' + ('usernames' if provider == 'username-demo' else 'emails'), 'aud': 'ilworkflow-api', 'iat': now, 'nbf': now, 'exp': now + 1800, **overrides}
    encode = lambda value: base64.urlsafe_b64encode(value).decode().rstrip('=')
    unsigned = encode(b'{"alg":"HS256","typ":"JWT"}') + '.' + encode(json.dumps(payload).encode())
    return unsigned + '.' + encode(hmac.new(key, unsigned.encode(), hashlib.sha256).digest())

admin = api('POST', '/elsa/api/identity/login', {'username': 'admin', 'password': 'password'})['accessToken']
user_ids_before = {x['id'] for x in api('GET', '/elsa/api/identity/users', token=admin)['users']}
providers = api('GET', '/api/external-providers', token=admin)['items']
assert {x['id'] for x in providers} == {'username-demo', 'email-demo'}
directories = {}
for kind, provider in [('usernames', 'username-demo'), ('emails', 'email-demo')]:
    master = api('GET', '/api/demo-masters/' + kind)
    listed = api('GET', '/api/external-providers/' + provider + '/members', token=admin)
    assert master == listed and len(master['items']) == 5
    assert len({x['participantId'] for x in master['items']}) == 5
    directories[provider] = master['items']
assert not {x['participantId'] for x in directories['username-demo']} & {x['participantId'] for x in directories['email-demo']}
api('POST', '/api/external-members', {'providerId': 'username-demo', 'masterMemberId': 'U001'}, admin, (404,))
print('PASS two GET masters, five members each, stable provider-scoped IDs; no registration POST')

def assigned(instance, jwt):
    for _ in range(60):
        tasks = api('GET', f'/elsa/api/user-tasks?scope=assigned&status=Assigned&workflowInstanceId={instance}', token=jwt)['items']
        if tasks: return tasks[0]
        time.sleep(.1)
    raise AssertionError('No assigned task')

def approve(saved, jwt):
    task = assigned(saved['instanceId'], jwt)
    assert task['id'] == saved['taskId'] and task['assignee']['id'] == saved['participantId']
    detail = api('GET', '/elsa/api/user-tasks/' + task['id'], token=jwt)
    approve_key = next(action['key'] for action in detail['actions'] if action['key'].upper() == 'APPROVE')
    assert args.verify_restart or approve_key == 'APPROVE'
    api('POST', '/elsa/api/user-tasks/' + task['id'] + '/complete', {'expectedRevision': detail['revision'], 'operationId': str(uuid.uuid4()), 'actionKey': approve_key, 'data': {}}, jwt)
    for _ in range(60):
        instance = api('GET', '/elsa/api/workflow-instances/' + saved['instanceId'], token=admin)
        if instance['status'] == 'Finished': break
        time.sleep(.1)
    assert instance['workflowState']['output']['ticketOutcome'] == 'Approved'

if args.verify_restart:
    saved = json.loads(state_file.read_text())
    for provider, record in saved.items():
        assert directories[provider][0]['participantId'] == record['participantId']
        approve(record, token(provider, 'john' if provider == 'username-demo' else 'john@example.test'))
    print('PASS restart: same GET member IDs, persisted native assignments and approval with fresh provider tokens')
    raise SystemExit()

saved = {}
for provider in directories:
    member = directories[provider][0]
    jwt = token(provider, '  JOHN  ' if provider == 'username-demo' else ' JOHN@EXAMPLE.TEST ')
    setup = {'name': 'Master John ' + provider, 'startStage': 'A', 'stages': [{'key': 'A', 'title': 'John approval', 'assignment': {'policy': 'Specific', 'members': [member['participantId']], 'userId': member['participantId']}, 'approveTo': {'kind': 'Approved'}, 'rejectTo': {'kind': 'Rejected'}}]}
    config = api('POST', '/api/workflow-configurations', {'id': 0, 'configuration': setup}, admin)
    config = api('POST', f'/api/workflow-configurations/{config["id"]}/publish', {'expectedRevision': config['revision']}, admin)
    # Internal users own submission. External employees cannot create instances, even if their JWT says admin.
    execution = f'/elsa/api/workflow-definitions/{config["definitionId"]}/execute'
    api('POST', execution, {}, jwt, (403,))
    def start():
        result = api('POST', execution, {'correlationId': 'MASTER-' + str(uuid.uuid4())}, admin)['workflowState']
        assert result['subStatus'] == 'Suspended', result.get('incidents')
        task = assigned(result['id'], jwt)
        assert task['assignee']['id'] == member['participantId']
        return {'instanceId': result['id'], 'taskId': task['id'], 'participantId': member['participantId']}
    completed = start()
    other = token('email-demo', 'john@example.test') if provider == 'username-demo' else token('username-demo', 'john')
    assert api('GET', f'/elsa/api/user-tasks?scope=assigned&workflowInstanceId={completed["instanceId"]}', token=other)['items'] == []
    api('GET', '/elsa/api/user-tasks/' + completed['taskId'], token=other, expected=(403, 404))
    approve(completed, jwt)
    saved[provider] = start()
    # Elevated incoming claims are discarded, not translated into configuration access.
    spoof = token(provider, 'john' if provider == 'username-demo' else 'john@example.test', admin=True, roles=['Administrator'], permissions=['*'])
    api('POST', '/api/workflow-configurations', {'id': 0, 'configuration': setup}, spoof, (403,))
    api('POST', execution, {'input': {'attributes': {'claimAmount': 6000}}}, spoof, (403,))
    print('PASS ' + provider + ': internal submission, external execution denied, native approval, provider isolation and no role escalation')

for invalid in [token('username-demo', 'unknown'), token('username-demo', 'john', name=None), token('username-demo', 'john', aud='wrong'), token('username-demo', 'john', iss='unknown'), token('username-demo', 'john', exp=int(time.time())-300), token('username-demo', 'john', nbf=int(time.time())+300)]:
    api('GET', '/elsa/api/user-tasks', token=invalid, expected=(401,))
valid = token('username-demo', 'john')
parts = valid.split('.')
bad_signature = parts[0] + '.' + parts[1] + '.' + ('A' if parts[2][0] != 'A' else 'B') + parts[2][1:]
api('GET', '/elsa/api/user-tasks', token=bad_signature, expected=(401,))
assert user_ids_before == {x['id'] for x in api('GET', '/elsa/api/identity/users', token=admin)['users']}
state_file.parent.mkdir(exist_ok=True)
state_file.write_text(json.dumps(saved, indent=2))
print('PASS invalid/missing identities, signature, issuer, audience, expiry and future token denied; no Elsa users created')

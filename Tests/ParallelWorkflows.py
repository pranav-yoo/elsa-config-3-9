"""Live PostgreSQL checks. Run again with --verify-restart after restarting the same API."""
import argparse
import copy
import json
import time
import uuid
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen

parser = argparse.ArgumentParser()
parser.add_argument('--base-url', default='http://localhost:5000')
parser.add_argument('--verify-restart', action='store_true')
args = parser.parse_args()
state_file = Path(__file__).resolve().parents[1] / 'App_Data/parallel-workflow-check.json'
tokens = {}


def api(method, path, body=None, actor='admin', expected=(200, 202)):
    headers = {'Authorization': 'Bearer ' + tokens[actor]} if actor else {}
    if body is not None:
        headers['Content-Type'] = 'application/json'
    request = Request(args.base_url + path, data=json.dumps(body).encode() if body is not None else None,
                      headers=headers, method=method)
    try:
        with urlopen(request, timeout=35) as response:
            status, raw = response.status, response.read().decode()
    except HTTPError as error:
        status, raw = error.code, error.read().decode()
    assert status in expected, (path, status, raw[:800])
    return status, json.loads(raw) if raw else None


def login(actor, username, password):
    _, result = api('POST', '/elsa/api/identity/login', {'username': username, 'password': password}, None)
    tokens[actor] = result['accessToken']


login('admin', 'admin', 'password')
_, roles = api('GET', '/elsa/api/identity/roles')
if not any(role['id'] == 'Approver' for role in roles['roles']):
    api('POST', '/elsa/api/identity/roles', {
        'id': 'Approver', 'name': 'Approver',
        'permissions': ['user-tasks:view', 'user-tasks:claim', 'user-tasks:complete']
    })
_, users = api('GET', '/elsa/api/identity/users')
ids = {}
for key in ('B', 'C', 'D', 'E'):
    name = 'parallel-check-' + key.lower()
    user = next((user for user in users['users'] if user['name'] == name), None)
    if user is None:
        _, user = api('POST', '/elsa/api/identity/users',
                      {'name': name, 'password': 'Password123!', 'roles': ['Approver']}, expected=(200, 201))
    ids[key] = user['id']
    login(key, name, 'Password123!')


def wait_for(get, predicate):
    for _ in range(60):
        result = get()
        if predicate(result):
            return result
        time.sleep(.1)
    raise AssertionError(str(result)[:1500])


def tasks(instance):
    return api('GET', f'/elsa/api/user-tasks?scope=all&workflowInstanceId={instance}')[1]['items']


def active(instance, title):
    found = wait_for(lambda: tasks(instance), lambda items: any(
        task['title'] == title and task['status'] == 'Assigned' for task in items))
    return next(task for task in found if task['title'] == title and task['status'] == 'Assigned')


def send(task, actor, action, body=None, expected=(200, 202)):
    if body is None:
        data = {'reason': 'Parallel test rejection'} if action == 'REJECT' else {}
        body = {'actionKey': action, 'expectedRevision': task['revision'], 'operationId': str(uuid.uuid4()), 'data': data}
    status, _ = api('POST', '/elsa/api/user-tasks/' + task['id'] + '/complete', body, actor, expected)
    return status, body


def check_finished(instance, outcome):
    state = wait_for(lambda: api('GET', '/elsa/api/workflow-instances/' + instance)[1],
                     lambda result: result['status'] == 'Finished')
    assert state['workflowState']['output']['ticketOutcome'] == outcome, state.get('subStatus')


def check_advance(instance, winner, loser, loser_status='Cancelled'):
    final_task = active(instance, 'E')
    result = tasks(instance)
    assert len([task for task in result if task['title'] == 'E']) == 1
    assert {task['id']: task['status'] for task in result if task['id'] in (winner['id'], loser['id'])} == {
        winner['id']: 'Completed', loser['id']: loser_status
    }, result
    send(final_task, 'E', 'APPROVE')
    check_finished(instance, 'Approved')


if args.verify_restart:
    saved = json.loads(state_file.read_text())
    _, observed = api('GET', f'/elsa/api/user-tasks?scope=observed&workflowInstanceId={saved["instance"]}', actor='D')
    assert [task['id'] for task in observed['items']] == [saved['C']['id']]
    _, detail = api('GET', '/elsa/api/user-tasks/' + saved['C']['id'], actor='D')
    assert detail['data']['claimAmount'] == 6000 and detail['allowedActions'] == []
    send(saved['D'], 'D', 'REJECT')
    check_finished(saved['instance'], 'Rejected')
    print('PASS restart preserves C rejection and observer access while D can resolve the group')
    raise SystemExit()


def dest(key):
    return {'kind': 'Stage', 'stageKey': key}


def stage(key, approve, reject):
    return {'key': key, 'title': key, 'approveTo': approve, 'rejectTo': reject,
            'assignment': {'policy': 'Specific', 'members': [ids[key]], 'userId': ids[key]}}


def config(correction=False, invalid=None):
    definition = 'parallel-' + uuid.uuid4().hex
    reject = {'kind': 'Correction', 'stageKey': 'C'} if correction else {'kind': 'Rejected'}
    stages = [stage('B', dest('C'), {'kind': 'Rejected'}), stage('C', dest('E'), reject),
              stage('D', dest('E'), reject), stage('E', {'kind': 'Approved'}, {'kind': 'Rejected'})]
    stages[1]['parallelWith'] = ['D']
    stages[1]['viewableByStages'] = ['D']
    stages[2]['assignment'] = {'policy': 'Condition', 'members': [ids['D']], 'rules': [
        {'when': {'attribute': 'claimAmount', 'operator': 'GreaterThan', 'value': 0}, 'userId': ids['D']}]}
    api('POST', '/api/workflow-attributes', {'definitionId': definition, 'attribute': {'key': 'claimAmount', 'type': 'Decimal'}})
    setup = {'name': 'Parallel approval check', 'startStage': 'B', 'stages': stages}
    if invalid:
        invalid(setup, stages)
    _, saved = api('POST', '/api/workflow-configurations', {'definition_id': definition, 'configuration': setup},
                   expected=(400,) if invalid else (200,))
    if invalid:
        return
    assert [field['key'] for field in saved['configuration']['attributes']] == ['claimAmount']
    api('POST', f'/api/workflow-configurations/{saved["id"]}/publish', {'expectedRevision': saved['revision']})
    return definition


def start(definition):
    _, response = api('POST', f'/elsa/api/workflow-definitions/{definition}/execute',
                      {'input': {'attributes': {'claimAmount': 6000}}})
    instance = response['workflowState']['id']
    send(active(instance, 'B'), 'B', 'APPROVE')
    return instance, active(instance, 'C'), active(instance, 'D')


for mutate in (
    lambda setup, stages: setup.update(startStage='D'),
    lambda setup, stages: stages[0].update(approveTo=dest('D')),
    lambda setup, stages: stages[1].update(parallelWith=['UNKNOWN']),
    lambda setup, stages: stages[1].update(parallelWith=['C']),
    lambda setup, stages: stages[1].update(parallelWith=['D', 'D']),
    lambda setup, stages: stages[2].update(parallelWith=['E']),
    lambda setup, stages: stages[3].update(parallelWith=['D']),
    lambda setup, stages: stages[2].update(approveTo={'kind': 'Approved'}),
    lambda setup, stages: stages[1].update(viewableByStages=['UNKNOWN']),
    lambda setup, stages: stages[1].update(viewableByStages=['C']),
    lambda setup, stages: stages[1].update(viewableByStages=['D', 'D']),
    lambda setup, stages: stages[2].update(approveRules=[{'when': copy.deepcopy(stages[2]['assignment']['rules'][0]['when']),
                                                         'destination': {'kind': 'Approved'}}]),
):
    config(invalid=mutate)
print('PASS invalid parallel membership, direct entry, conflicting routes and viewer stages')

definition = config()
instance, c, d = start(definition)
_, observed = api('GET', f'/elsa/api/user-tasks?scope=observed&workflowInstanceId={instance}', actor='D')
assert [task['id'] for task in observed['items']] == [c['id']]
assert observed['items'][0]['allowedActions'] == []
_, own = api('GET', f'/elsa/api/user-tasks?scope=assigned&workflowInstanceId={instance}', actor='D')
assert [task['id'] for task in own['items']] == [d['id']]
send(c, 'D', 'APPROVE', expected=(403, 404))
send(d, 'D', 'APPROVE')
check_advance(instance, d, c)
print('PASS D observes parallel C while retaining authority over its own D task')

for actor in ('C', 'D'):
    instance, c, d = start(definition)
    winner, loser, other = (c, d, 'D') if actor == 'C' else (d, c, 'C')
    _, body = send(winner, actor, 'APPROVE')
    send(winner, actor, 'APPROVE', body)
    send(loser, other, 'APPROVE', expected=(403, 404, 409, 422))
    check_advance(instance, winner, loser)
print('PASS both approval orders, cancellation, operation retry and late completion')

instance, c, d = start(definition)
send(c, 'C', 'REJECT')
assert active(instance, 'D')['id'] == d['id']
assert not any(task['title'] == 'E' for task in tasks(instance))
send(d, 'D', 'APPROVE')
check_advance(instance, d, c, 'Completed')
instance, c, d = start(definition)
send(c, 'C', 'REJECT')
send(d, 'D', 'REJECT')
check_finished(instance, 'Rejected')
print('PASS first rejection keeps group open, later approval wins, two rejections reject')

instance, c, d = start(definition)
with ThreadPoolExecutor(2) as pool:
    futures = [pool.submit(send, task, key, 'APPROVE', expected=(200, 202, 403, 404, 409, 422))
               for task, key in [(c, 'C'), (d, 'D')]]
    statuses = [future.result()[0] for future in futures]
assert sum(status in (200, 202) for status in statuses) == 1, statuses
winner, loser = (c, d) if statuses[0] in (200, 202) else (d, c)
check_advance(instance, winner, loser)
print('PASS simultaneous approvals produce exactly one continuation')

instance, c, d = start(config(correction=True))
send(c, 'C', 'REJECT')
send(d, 'D', 'REJECT')
correction = active(instance, 'Correct and resubmit: C')
api('GET', '/elsa/api/user-tasks/' + correction['id'], actor='D', expected=(404,))
send(correction, 'admin', 'RESUBMIT', {'actionKey': 'RESUBMIT', 'expectedRevision': correction['revision'],
     'operationId': str(uuid.uuid4()), 'data': {'attributes': {'claimAmount': 4000}}})
new_c, new_d = active(instance, 'C'), active(instance, 'D')
assert new_c['id'] != c['id'] and new_d['id'] != d['id']
send(new_d, 'D', 'APPROVE')
check_advance(instance, new_d, new_c)
print('PASS correction creates a new parallel visit')

instance, c, d = start(definition)
send(c, 'C', 'REJECT')
state_file.parent.mkdir(exist_ok=True)
state_file.write_text(json.dumps({'instance': instance, 'C': c, 'D': d}))
print('PASS saved pending D for --verify-restart')

"""Live API acceptance checks. Creates research accounts/configurations; never logs tokens.
Run with --base-url http://localhost:5012. Restart the server, then rerun with --verify-restart.
"""
import argparse, copy, json, time, uuid
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen

parser=argparse.ArgumentParser()
parser.add_argument('--base-url',default='http://localhost:5000')
parser.add_argument('--verify-restart',action='store_true')
parser.add_argument('--verify-preview',action='store_true')
args=parser.parse_args()
BASE=args.base_url.rstrip('/')
STATE=Path(__file__).resolve().parents[1]/'App_Data/configured-workflow-check.json'
tokens={}

def api(method,path,body=None,user='admin',expected=(200,201,202),return_status=False):
    headers={}
    if body is not None: headers['Content-Type']='application/json'
    if user is not None: headers['Authorization']='Bearer '+tokens[user]
    payload=body if isinstance(body,bytes) else json.dumps(body).encode() if body is not None else None
    try:
        with urlopen(Request(BASE+path,data=payload,headers=headers,method=method),timeout=30) as response:
            status=response.status; raw=response.read().decode()
    except HTTPError as error:
        status=error.code; raw=error.read().decode()
    assert status in expected, f'{method} {path}: HTTP {status}: {raw[:1200]}'
    data=json.loads(raw) if raw else None
    return (status,data) if return_status else data

def login(name,username=None,password='Password123!'):
    data=api('POST','/elsa/api/identity/login',{'username':username or 'configured-'+name,'password':password},None)
    assert data.get('accessToken'),f'Login failed: {name}'
    tokens[name]=data['accessToken']

login('admin','admin','password')

if args.verify_preview:
    document=api('GET','/swagger/v3/swagger.json',user=None)
    assert any('workflow-configurations' in path for path in document['paths'])
    for method,path,body in [('GET','/api/workflow-configurations/1',None),
            ('POST','/elsa/api/workflow-definitions/anything/execute',{}),
            ('POST','/elsa/api/user-tasks/anything/complete',{})]:
        api(method,path,body,expected=(503,))
    print('PASS documentation preview: Swagger loads and configuration/execution/task mutations return 503')
    raise SystemExit()

def prepare_users():
    roles=api('GET','/elsa/api/identity/roles')['roles']
    grants={
        'Approver':['user-tasks:view','user-tasks:claim','user-tasks:complete','workflows/instances:view'],
        'Requester':['user-tasks:view','user-tasks:complete','workflows/definitions:view','workflows/definitions:execute','workflows/instances:view'],
        'Configurator':['workflows/definitions:*','workflows/instances:view'],
        'Supervisor':['user-tasks:view','user-tasks:assign','user-tasks:supervise','workflows/instances:view']}
    for role,permissions in grants.items():
        method='PUT' if any(x['id']==role for x in roles) else 'POST'
        api(method,'/elsa/api/identity/roles'+('/'+role if method=='PUT' else ''),{'id':role,'name':role,'permissions':permissions})
    existing=api('GET','/elsa/api/identity/users')['users']; ids={}
    for name,role in [('requester','Requester'),('alice','Approver'),('amy','Approver'),('bob','Approver'),('clara','Approver'),('dan','Approver'),('outsider','Requester'),('configurator','Configurator'),('supervisor','Supervisor')]:
        user=next((x for x in existing if x['name']=='configured-'+name),None)
        if user is None: user=api('POST','/elsa/api/identity/users',{'name':'configured-'+name,'password':'Password123!','roles':[role]})
        ids[name]=user['id']; login(name)
    return ids

ids=prepare_users()

def fixture():
    def dest(kind,key=None):return {'kind':kind,**({'stageKey':key} if key else {})}
    def assign(policy,members,user=None,rules=None):return {'policy':policy,'members':[ids[x] for x in members],**({'userId':ids[user]} if user else {}),**({'rules':rules} if rules else {})}
    condition={'attribute':'claimAmount','operator':'GreaterThan','value':5000}
    return {'name':'Configured Expense Claim','attributes':[
        {'key':'firstName','type':'String','required':True,'maxLength':100},
        {'key':'lastName','type':'String','required':True},
        {'key':'claimAmount','type':'Decimal','required':True,'minimum':0}], 'startStage':'A','stages':[
        {'key':'A','title':'A Initial review','assignment':assign('Bucket',['alice','amy']),'approveTo':dest('Stage','B'),'rejectTo':dest('Correction','A')},
        {'key':'B','title':'B Claims review','assignment':assign('Bucket',['bob']),'approveTo':dest('Stage','D'),'rejectTo':dest('Stage','A'),'approveRules':[{'when':condition,'destination':dest('Stage','C')}]},
        {'key':'C','title':'C High-value approval','assignment':assign('Condition',['clara'],rules=[{'when':condition,'userId':ids['clara']}]),'approveTo':dest('Stage','D'),'rejectTo':dest('Rejected')},
        {'key':'D','title':'D Final approval','assignment':assign('Specific',['dan'],'dan'),'approveTo':dest('Approved'),'rejectTo':dest('Correction','D')}]}

def create(setup=None):return api('POST','/api/workflow-configurations',{'id':0,'configuration':setup or fixture()},'configurator')
def publish(config):return api('POST',f'/api/workflow-configurations/{config["id"]}/publish',{'expectedRevision':config['revision']},'configurator')
def start(config,amount=6000):
    result=api('POST',f'/elsa/api/workflow-definitions/{config["definitionId"]}/execute',{'correlationId':'CHECK-'+str(uuid.uuid4()),'input':{'attributes':{'firstName':'Pranav','lastName':'Shah','claimAmount':amount}}},'requester')
    state=result['workflowState']; assert state['subStatus']=='Suspended',json.dumps(state.get('incidents'))[:2000]
    return state['id']

def task(instance,user,correction=False):
    for attempt in range(60):
        for scope in ('assigned','available'):
            result=api('GET',f'/elsa/api/user-tasks?workflowInstanceId={instance}&scope={scope}&includeTotalCount=true',user=user)
            items=[x for x in result['items'] if x['status'] in ('Available','Assigned','Unassigned')]
            if items:
                t=items[0]; return api('GET','/elsa/api/user-tasks/'+t['id'],user=user)
        time.sleep(.1)
    state=api('GET','/elsa/api/workflow-instances/'+instance)
    raise AssertionError('No task: '+json.dumps(state)[:1500])

def decide(instance,user,action='Approve',data=None,replay=False):
    t=task(instance,user)
    if t['status'] in ('Available','Unassigned'):
        api('POST',f'/elsa/api/user-tasks/{t["id"]}/claim',{'expectedRevision':t['revision'],'operationId':str(uuid.uuid4())},user)
        t=api('GET','/elsa/api/user-tasks/'+t['id'],user=user)
    payload={'expectedRevision':t['revision'],'operationId':str(uuid.uuid4()),'actionKey':action,'data':data if data is not None else {'comment':'Reviewed'}}
    api('POST',f'/elsa/api/user-tasks/{t["id"]}/complete',payload,user)
    if replay:api('POST',f'/elsa/api/user-tasks/{t["id"]}/complete',payload,user)
    return t,payload

def finished(instance,outcome):
    for attempt in range(60):
        result=api('GET','/elsa/api/workflow-instances/'+instance)
        if result['status']=='Finished':break
        time.sleep(.1)
    assert result['subStatus']=='Finished',json.dumps(result)[:1000]
    assert result['workflowState']['output']['ticketOutcome']==outcome
    return result

if args.verify_restart:
    saved=json.loads(STATE.read_text())
    assert ids==saved['userIds'], 'Native user IDs did not survive restart'
    config=api('GET','/api/workflow-configurations/'+str(saved['configId']))
    assert config['definitionVersionId']==saved['definitionVersionId'] and config['isPublished']
    assert task(saved['pendingInstance'],'alice')['id']==saved['pendingTask']
    decide(saved['pendingInstance'],'alice');decide(saved['pendingInstance'],'bob');decide(saved['pendingInstance'],'clara');decide(saved['pendingInstance'],'dan')
    finished(saved['pendingInstance'],'Approved')
    assert finished(saved['correctedInstance'],'Approved')['workflowState']['output']['attributeRevision']==2
    print('PASS restart: native user IDs/login, configuration, definition, pending task, callback resumption, corrected attributes and final outcome')
    raise SystemExit()

config=create()
config=api('POST','/api/workflow-configurations',{'id':config['id'],'expectedRevision':config['revision'],'configuration':config['configuration']},'configurator')
api('POST','/api/workflow-configurations',{'id':config['id'],'expectedRevision':0,'configuration':config['configuration']},'configurator',expected=(409,))
api('POST','/api/workflow-configurations',{'id':999999999,'expectedRevision':1,'configuration':config['configuration']},'configurator',expected=(404,))
config=publish(config)
api('POST','/api/workflow-configurations',{'id':config['id'],'expectedRevision':config['revision'],'configuration':config['configuration']},'configurator',expected=(409,))
print('PASS POST create/update, stale revision, missing ID and immutable published configuration')

for amount in (4000,5000,6000):
    instance=start(config,amount)
    decide(instance,'alice',replay=amount==6000);decide(instance,'bob')
    if amount>5000:decide(instance,'clara')
    decide(instance,'dan');finished(instance,'Approved')
    print(f'PASS {amount}: conditional approvals and final outcome')

instance=start(config);decide(instance,'alice');decide(instance,'bob','Reject',{'reason':'Return to A'})
decide(instance,'alice');decide(instance,'bob');decide(instance,'clara','Reject',{'reason':'Terminal rejection'});finished(instance,'Rejected')
print('PASS reject B to A, then terminal rejection C')

instance=start(config);decide(instance,'alice','Reject',{'reason':'Correct the amount'})
decide(instance,'requester','Resubmit',{'attributes':{'firstName':'Pranav','lastName':'Shah','claimAmount':4000}})
decide(instance,'alice');decide(instance,'bob');decide(instance,'dan');finished(instance,'Approved')
corrected=instance
print('PASS correction A, revised attributes, same instance, C bypassed')

instance=start(config,4000);decide(instance,'alice');decide(instance,'bob');decide(instance,'dan','Reject',{'reason':'Correct final details'})
decide(instance,'requester','Resubmit',{'attributes':{'firstName':'Pranav','lastName':'Shah','claimAmount':6000}})
decide(instance,'dan');finished(instance,'Approved')
print('PASS correction D resumes configured D, without replaying B/C')

instance=start(config);t=task(instance,'alice');payload={'expectedRevision':t['revision'],'operationId':str(uuid.uuid4())}
with ThreadPoolExecutor(max_workers=2) as pool:
    def claim(who):
        command={**payload,'operationId':str(uuid.uuid4())}
        # Once another candidate claims, Elsa may hide the task from the loser with 404.
        status,_=api('POST',f'/elsa/api/user-tasks/{t["id"]}/claim',command,who,expected=(200,202,403,404,409),return_status=True)
        return status in (200,202)
    results=list(pool.map(claim,['alice','amy']))
assert sum(results)==1,results
winner='alice' if results[0] else 'amy';detail=task(instance,winner)
api('POST',f'/elsa/api/user-tasks/{t["id"]}/complete',{'expectedRevision':detail['revision'],'operationId':str(uuid.uuid4()),'actionKey':'Reject','data':{}},winner,expected=(400,422))
api('GET','/elsa/api/workflow-instances/'+instance,user='outsider',expected=(404,))
api('GET','/elsa/api/workflow-instances',user='requester',expected=(403,))
api('POST','/api/workflow-configurations',{'id':0,'configuration':fixture()},'requester',expected=(403,))
api('POST',f'/elsa/api/workflow-definitions/{config["definitionId"]}/dispatch',{'input':{}},'requester',expected=(403,))
api('POST',f'/elsa/api/workflow-definitions/{config["definitionId"]}/execute',{'input':{'requesterId':ids['outsider'],'attributes':{}}},'requester',expected=(403,))
api('POST',f'/elsa/api/workflow-definitions/{config["definitionId"]}/execute',{'input':{'attributes':{'claimAmount':'wrong'}}},'requester',expected=(400,))
api('POST',f'/elsa/api/workflow-definitions/{config["definitionId"]}/execute',b'{"input":{},"input":{}}','requester',expected=(400,))
api('GET','/elsa/api/user-tasks',user=None,expected=(401,))
print('PASS concurrent claims, required reason, authentication, configuration permission, scoped reads and execution guards')

for mutate in ('duplicate','cycle','operator','member'):
    setup=fixture()
    if mutate=='duplicate':setup['attributes'].append(copy.deepcopy(setup['attributes'][0]))
    if mutate=='cycle':setup['stages'][-1]['approveTo']={'kind':'Stage','stageKey':'A'}
    if mutate=='operator':setup['stages'][1]['approveRules'][0]['when']['attribute']='firstName'
    if mutate=='member':setup['stages'][0]['assignment']['members']=[ids['requester']]
    api('POST','/api/workflow-configurations',{'id':0,'configuration':setup},'configurator',expected=(400,))
print('PASS FluentValidation duplicate keys, cycles, type/operator mismatch and ineligible member')

# A stage entered by rejection can still contain an invalid approval cycle. Validate the whole graph.
setup=fixture()
setup['stages'][0]['approveTo']={'kind':'Approved'}
setup['stages'][0]['rejectTo']={'kind':'Stage','stageKey':'B'}
setup['stages'][1]['approveRules']=[]
setup['stages'][1]['approveTo']={'kind':'Stage','stageKey':'C'}
setup['stages'][2]['approveTo']={'kind':'Stage','stageKey':'B'}
api('POST','/api/workflow-configurations',{'id':0,'configuration':setup},'configurator',expected=(400,))
print('PASS approval cycles rejected even when reached only through a rejection route')

# These cases previously diverged between C# assignment and JavaScript routing.
# Int64 values must remain exact; absent attributes must not inherit Object.constructor.
setup=fixture();setup['attributes'][2]['type']='Integer'
setup['stages'][1]['approveRules'][0]['when']['value']=9007199254740992
precise=publish(create(setup));instance=start(precise,9007199254740993)
decide(instance,'alice');decide(instance,'bob');decide(instance,'clara');decide(instance,'dan')
finished(instance,'Approved')
setup=fixture();setup['attributes'].append({'key':'constructor','type':'String'})
setup['stages'][1]['approveRules'][0]['when']={'attribute':'constructor','operator':'NotEquals','value':'blocked'}
missing=publish(create(setup));instance=start(missing)
decide(instance,'alice');decide(instance,'bob');decide(instance,'dan');finished(instance,'Approved')
print('PASS exact Int64 condition routing and omitted prototype-named attributes')

# No eligible candidate must suspend; a supervisor repairs assignment through the native API.
blocked_setup=fixture();blocked_setup['stages'][0]['assignment']['members']=[]
blocked_config=publish(create(blocked_setup));blocked_instance=start(blocked_config)
blocked=api('GET',f'/elsa/api/user-tasks?scope=all&workflowInstanceId={blocked_instance}',user='supervisor')['items'][0]
assert blocked['status']=='Unassigned'
api('POST',f'/elsa/api/user-tasks/{blocked["id"]}/assign',{'expectedRevision':blocked['revision'],'operationId':str(uuid.uuid4()),'assignee':{'kind':'user','provider':'default','id':ids['alice']}},'supervisor')
decide(blocked_instance,'alice')
assert task(blocked_instance,'bob')['title'].startswith('B')
print('PASS missing assignment never bypasses approval; supervisor repairs through native assign')


pending=start(config);pending_task=task(pending,'alice')['id']
next_config=api('POST',f'/api/workflow-configurations/{config["id"]}/next-draft',{'expectedRevision':config['revision']},'configurator')
next_config['configuration']['name']='Expense Claim v2'
next_config=api('POST','/api/workflow-configurations',{'id':next_config['id'],'expectedRevision':next_config['revision'],'configuration':next_config['configuration']},'configurator')
publish(next_config)
assert api('GET','/elsa/api/workflow-instances/'+pending)['definitionVersionId']==config['definitionVersionId']
STATE.parent.mkdir(exist_ok=True)
STATE.write_text(json.dumps({'configId':config['id'],'definitionVersionId':config['definitionVersionId'],'pendingInstance':pending,'pendingTask':pending_task,'correctedInstance':corrected,'userIds':ids}))
print('PASS next version published while pending instance remains pinned; restart fixture saved')

"""Build ordered VS Code REST Client request collections from the checked-in fixtures."""
import copy
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
identity = json.loads((ROOT/'Samples/identity.json').read_text())['Identity']
base_definition = json.loads((ROOT/'Samples/Definitions/expense-claim.definition.json').read_text(encoding='utf-8-sig'))
users = ['configurator','requester','alice','amy','bella','bob','clara','dan','supervisor']

def ref(request, path): return '{{'+request+'.response.body.$.'+path+'}}'
def token(user): return ref(user+'Login','accessToken')
def user_id(user, api): return ref('create'+user.capitalize(),'id') if api else user

def build(api):
    chunks=[]; step=0
    def request(title,name,method,path,body=None,user=None,notes=''):
        nonlocal step
        step+=1
        lines=[f'### {step:02}. {title}']
        if notes: lines += ['# '+line for line in notes.splitlines()]
        lines += [f'# @name {name}',f'{method} {{{{baseUrl}}}}{path}']
        if user: lines += ['Authorization: Bearer '+token(user)]
        if body is not None: lines += ['Content-Type: application/json']
        lines += ['']
        if body is not None: lines += [body if isinstance(body,str) else json.dumps(body,indent=2)]
        chunks.append('\n'.join(lines))
    mode='API-created accounts' if api else 'existing JSON sample accounts'
    chunks.append(f'''# Expense Claim: {mode}; execute requests ONE AT A TIME in order.
# VS Code REST Client extension: named responses supply tokens, IDs and revisions.
# Do not use Send All: completion is asynchronous and optional branches must not run together.
# Other clients: copy the values from the named response manually.
#
# MAIN SEQUENCE: setup -> publish -> execute 6000 -> Alice A -> Bob B -> Clara C -> Dan D -> Finished.
# The alternative rejection/update requests at the bottom are NOT part of that happy path.
# New operationId for a new command. For retry, resend the SAME body/ID/revision;
# {{{{$guid}}}} changes on every send, so save the original request before retrying it.
@baseUrl = http://localhost:5000
@adminPassword = password
@samplePassword = Password123!
''')
    if api:
        chunks.append('''# REQUIRED API-IDENTITY MODE (leave the default JSON fixture mode unchanged):
# dotnet run --launch-profile http -- --Identity:UseConfigurationProviders=false
# In preview this creates an in-memory admin; all API-created accounts disappear on restart.
# On a fresh server, admin is bootstrapped by Elsa. No anonymous create-admin endpoint exists.
# Use the configured admin password. Create roles/users once; do not rerun POST setup blindly.
# Native POST users accepts name/password/roles; it GENERATES id (not the username).
# All workflow candidate IDs below are taken from the creation responses.
''')
    else:
        chunks.append('''# DEFAULT MODE: Identity:UseConfigurationProviders=true (current default).
# These users/roles already exist in Samples/identity.json; do not POST-create them again.
# For ALL role/user creation requests followed by the same A-B-C-D flow,
# use Identity-And-ABCD.http with its documented store-backed startup switch.
# Sample user IDs equal names in this mode only.
''')
    request('Admin login (bootstrap / diagnostic access)','adminLogin','POST','/elsa/api/identity/login',{'username':'admin','password':'{{adminPassword}}'})
    if api:
        roles=[r for r in identity['Roles'] if r['id'] != 'admin']
        for role in roles:
            request('Create '+role['id']+' role','createRole'+role['id'],'POST','/elsa/api/identity/roles',role,'admin')
        request('List stored roles','listRoles','GET','/elsa/api/identity/roles',user='admin')
        for user in users:
            fixture=next(u for u in identity['Users'] if u['id']==user)
            request('Create '+user+'; keep response.id','create'+user.capitalize(),'POST','/elsa/api/identity/users',{'name':user,'password':'{{samplePassword}}','roles':fixture['roles']},'admin')
        request('List stored users (IDs are generated)','listUsers','GET','/elsa/api/identity/users',user='admin')
    else:
        request('Inspect permission catalog','permissionCatalog','GET','/elsa/api/identity/permissions',user='admin',notes='Identity list APIs query the writable store; JSON-provided users are not necessarily listed there.')
    request('Configurator login','configuratorLogin','POST','/elsa/api/identity/login',{'username':'configurator','password':'{{samplePassword}}'})
    definition=copy.deepcopy(base_definition)
    if api:
        config=json.loads(definition['model']['root']['configurationJson']['expression']['value'])
        for stage in config['definition']['stages']:
            a=stage['assignment']; a['members']=[user_id(u,True) for u in a['members']]
            if a.get('userId'): a['userId']=user_id(a['userId'],True)
            for rule in a.get('rules',[]): rule['userId']=user_id(rule['userId'],True)
        definition['model']['root']['configurationJson']['expression']['value']=json.dumps(config,separators=(',',':'))
    request('Publish expense-claim-v1 (configurationJson Literal / String)','publishClaim','POST','/elsa/api/workflow-definitions',definition,'configurator',notes='HTTP 200: check workflowDefinition.isPublished. Existing definition publication may create a newer version.')
    request('Requester login','requesterLogin','POST','/elsa/api/identity/login',{'username':'requester','password':'{{samplePassword}}'})
    request('Submit 6000; save workflowState.id','startClaim','POST','/elsa/api/workflow-definitions/expense-claim-v1/execute',{'correlationId':'CLAIM-{{$guid}}','input':{'requesterId':user_id('requester',api),'firstName':'Pranav','lastName':'Shah','claimAmount':6000}},'requester',notes='Check subStatus=Suspended and no incident. For 4000/5000, skip ALL C requests and go straight from B to D.')
    workflow=ref('startClaim','workflowState.id')
    request('Requester effective permissions','requesterPermissions','GET','/elsa/api/identity/me/permissions',user='requester')
    for stage,user,bucket in [('A','alice',True),('B','bob',True),('C','clara',False),('D','dan',False)]:
        lower=stage.lower()
        request(f'Stage {stage}: login as {user}',user+'Login','POST','/elsa/api/identity/login',{'username':user,'password':'{{samplePassword}}'})
        scope='available' if bucket else 'assigned'
        lookup=lower+'Tasks'; task=ref(lookup,'items[0].id')
        request(f'Stage {stage}: find {scope} task; wait/retry until items is nonempty',lookup,'GET',f'/elsa/api/user-tasks?scope={scope}&workflowInstanceId={workflow}&includeTotalCount=true',user=user,notes='Use this new task ID, never the previous stage task ID. If empty after a 202, repeat this GET before continuing.'+(' For C, amount must be > 5000.' if stage=='C' else ''))
        if bucket:
            request(f'Stage {stage}: {user} claims bucket ownership',lower+'Claim','POST',f'/elsa/api/user-tasks/{task}/claim',{'expectedRevision':ref(lookup,'items[0].revision'),'operationId':'{{$guid}}'},user,notes='expectedRevision must be a JSON number after substitution; the collection writes numeric references without quotes.')
            # Raw numeric references are required: normal JSON serialization would quote them.
            chunks[-1]=chunks[-1].replace('"'+ref(lookup,'items[0].revision')+'"',ref(lookup,'items[0].revision'))
        detail=lower+'Detail'
        request(f'Stage {stage}: read latest detail/revision',detail,'GET',f'/elsa/api/user-tasks/{task}',user=user)
        request(f'Stage {stage}: Approve (or choose its Reject alternative below INSTEAD)',lower+'Approve','POST',f'/elsa/api/user-tasks/{task}/complete',{'expectedRevision':ref(detail,'revision'),'operationId':'{{$guid}}','actionKey':'Approve','data':{'comment':f'{stage} reviewed and approved'}},user,notes='Expect 202 accepted/Completing, then allow Elsa to resume. D approval ends this claim.')
        chunks[-1]=chunks[-1].replace('"'+ref(detail,'revision')+'"',ref(detail,'revision'))
    request('Final instance: Finished and output.ticketStatus=Completed','finishedInstance','GET',f'/elsa/api/workflow-instances/{workflow}',user='admin',notes='Poll until Finished; approved business outcome is workflowState.output.ticketStatus=Completed.')
    request('All four stage tasks for this claim','allStageTasks','GET',f'/elsa/api/user-tasks?scope=all&workflowInstanceId={workflow}&includeTotalCount=true',user='admin',notes='Completed A/B/C/D in one list is expected. Each is a distinct task in the same workflow instance.')
    for stage in 'ABCD':
        request(f'{stage} task event history',stage.lower()+'Events','GET',f'/elsa/api/user-tasks/{ref(stage.lower()+"Tasks","items[0].id")}/events',user='admin')
    request('Workflow journal','claimJournal','GET',f'/elsa/api/workflow-instances/{workflow}/journal',user='admin')
    chunks.append('''# END MAIN SEQUENCE
# ---------------------------------------------------------------------------
# OPTIONAL ALTERNATIVES: use a NEW claim, stop at the desired active stage,
# then run its Reject request INSTEAD OF the stage's Approve request.
# Do not run these on the already completed happy-path tasks above.
# Refresh that stage's Detail request immediately before its Reject request.
# Re-run startClaim + discovery as necessary; named references track the last response.
''')
    for stage,user,to in [('A','alice','requester correction, then A'),('B','bob','new A visit'),('C','clara','terminal Rejected'),('D','dan','requester correction, then D')]:
        detail=stage.lower()+'Detail'; task=ref(stage.lower()+'Tasks','items[0].id')
        request(f'ALTERNATIVE Reject {stage} -> {to}',stage.lower()+'Reject','POST',f'/elsa/api/user-tasks/{task}/complete',{'expectedRevision':ref(detail,'revision'),'operationId':'{{$guid}}','actionKey':'Reject','data':{'reason':f'{stage}: please correct/review the claim'}},user)
        chunks[-1]=chunks[-1].replace('"'+ref(detail,'revision')+'"',ref(detail,'revision'))
    request('After A/D rejection: requester finds correction task','correctionTasks','GET',f'/elsa/api/user-tasks?scope=assigned&workflowInstanceId={workflow}',user='requester',notes='Find title Correct and resubmit Expense Claim. Use its ID, not the rejected stage ID.')
    correction=ref('correctionTasks','items[0].id')
    request('Read correction detail','correctionDetail','GET',f'/elsa/api/user-tasks/{correction}',user='requester')
    request('Resubmit corrected attributes as requester','resubmitCorrection','POST',f'/elsa/api/user-tasks/{correction}/complete',{'expectedRevision':ref('correctionDetail','revision'),'operationId':'{{$guid}}','actionKey':'Resubmit','data':{'attributes':{'firstName':'Pranav','lastName':'Shah','claimAmount':4000}}},'requester',notes='Same instance resumes the rejected stage. Re-run that stage discovery/detail and approval. Correction at D does NOT replay B/C.')
    chunks[-1]=chunks[-1].replace('"'+ref('correctionDetail','revision')+'"',ref('correctionDetail','revision'))
    request('After rejection/resubmission: current business outcome','branchInstance','GET',f'/elsa/api/workflow-instances/{workflow}',user='admin',notes='C rejection: Finished + Rejected. A/D correction: stays active until the resumed stage(s) are approved.')
    if api:
        request('OPTIONAL replace Approver role permissions','updateApproverRole','PUT','/elsa/api/identity/roles/Approver',{'name':'Approver','permissions':['user-tasks:view','user-tasks:claim','user-tasks:complete']},'admin',notes='Preserve all intended permissions. Re-login affected users after identity changes.')
        request('OPTIONAL update Amy roles/password','updateAmy','PUT',f'/elsa/api/identity/users/{user_id("amy",True)}',{'password':'{{samplePassword}}','roles':['Approver']},'admin',notes='Path parameter is generated user ID. Supply the full intended role list; login again after changing it.')
    result='\n\n'.join(chunks)+'\n'
    return result

(ROOT/'Tickets.http').write_text(build(False),encoding='utf-8')
(ROOT/'Identity-And-ABCD.http').write_text(build(True),encoding='utf-8')
out=ROOT/'output/pdf/workflow-requests'
out.mkdir(parents=True,exist_ok=True)
for name in ('Tickets.http','Identity-And-ABCD.http'):
    (out/name).write_text((ROOT/name).read_text(),encoding='utf-8')
print('Created ordered fixture and API-provisioning collections in root and output/pdf/workflow-requests')

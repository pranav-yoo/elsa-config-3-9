"""Create readable, ordered REST Client examples. Provision accounts using Authentication.http first."""
import json
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
source=(ROOT/'Tests/ConfiguredWorkflows.py').read_text()
namespace={'ids':{name:'{{'+name+'Id}}' for name in ['alice','amy','bob','clara','dan']}}
exec(source[source.index('def fixture():'):source.index('\ndef create(')],namespace)
setup=namespace['fixture']()
amount=next(field for field in setup.pop('attributes') if field['key']=='claimAmount')

def request(title,name,method,path,body=None,token=None,comment=''):
    text='\n### '+title+'\n'
    if comment:text+='# '+comment.replace('\n','\n# ')+'\n'
    text+='# @name '+name+'\n'+method+' {{baseUrl}}'+path+'\n'
    if token:text+='Authorization: Bearer {{'+token+'}}\n'
    if body is not None:text+='Content-Type: application/json\n\n'+(body if isinstance(body,str) else json.dumps(body,indent=2))+'\n'
    return text+'\n'

def login(name,username):
    return request('Login: '+username,name+'Login','POST','/elsa/api/identity/login',{'username':username,'password':'{{samplePassword}}'},comment='Use accessToken in Swagger without adding Bearer. Tokens expire; sign in again when needed.')

header='''# Run requests ONE AT A TIME in VS Code REST Client, not Send All.
# Named responses work within this file. Copy the required IDs between files.
# Completion may return 202: retry the next GET until the next task appears.
# A new action needs a new operationId; retry an old command with its ORIGINAL body/UUID/revision.
@baseUrl = http://localhost:5000
@adminPassword = password
@samplePassword = Password123!
'''
admin=request('Admin login','adminLogin','POST','/elsa/api/identity/login',{'username':'admin','password':'{{adminPassword}}'})
adminToken='adminLogin.response.body.$.accessToken'
auth=header+admin
for resource in ['roles','users']:
    auth+=request('Inspect existing '+resource,'existing'+resource.title(),'GET','/elsa/api/identity/'+resource,token=adminToken,comment='Inspect first. Skip provisioning POSTs below when the role/user already exists. Native list fields are roles/users, not items.')
roles={
 'Approver':['user-tasks:view','user-tasks:claim','user-tasks:complete','workflows/instances:view'],
 'Requester':['user-tasks:view','user-tasks:complete','workflows/definitions:view','workflows/definitions:execute','workflows/instances:view'],
 'Configurator':['workflows/definitions:*','workflows/instances:view'],
 'Supervisor':['user-tasks:view','user-tasks:assign','user-tasks:supervise','workflows/instances:view']}
for role,permissions in roles.items():auth+=request('Create role '+role+' IF MISSING','createRole'+role,'POST','/elsa/api/identity/roles',{'id':role,'name':role,'permissions':permissions},adminToken,comment='Native Elsa API; run once. Do not recreate an existing role.')
for name,role in [('requester','Requester'),('alice','Approver'),('amy','Approver'),('bob','Approver'),('clara','Approver'),('dan','Approver'),('configurator','Configurator'),('supervisor','Supervisor'),('outsider','Requester')]:
    auth+=request('Create configured-'+name+' IF MISSING','createUser'+name.title(),'POST','/elsa/api/identity/users',{'name':'configured-'+name,'password':'{{samplePassword}}','roles':[role]},adminToken,comment='Elsa generates id. Copy the returned user id into Workflow-Configuration.http; a username is not the user ID.')
for name in ['configurator','requester','alice','bob','clara','dan','supervisor']:auth+=login(name,'configured-'+name)
auth+='''\n# OPTIONAL John external-token test. No Elsa password/login is needed for John.
# Run pwsh -NoProfile -ExecutionPolicy Bypass -File ./New-TestAccessToken.ps1 -Provider Username -Identity john
# Restart the app if the signing key is newly created. Paste the token, not its JSON payload.
@johnToken = PASTE_GENERATED_TOKEN
'''
auth+=request('John lists assigned tasks','johnTasks','GET','/elsa/api/user-tasks?scope=assigned&status=Assigned',token='johnToken',comment='Use External-Masters.http for GET member selection and an approval assigned to John. Copy participantId from the master, not the token sub or display name. No external registration POST/database is involved.')
(ROOT/'Authentication.http').write_text(auth)

config=header+'''# Provision using Authentication.http first; copy the generated IDs below.
# Do not substitute usernames, task IDs or workflow instance IDs for these employee IDs.
@definitionId = configured-expense-v1
'''+''.join('@'+name+'Id = PASTE_'+name.upper()+'_USER_ID\n' for name in namespace['ids'])
config+=login('configurator','configured-configurator')
configToken='configuratorLogin.response.body.$.accessToken'
config+=request('Add the conditional attribute to the catalog','createAmountField','POST','/api/workflow-attributes',{'definitionId':'{{definitionId}}','attribute':amount},configToken,comment='Reuse the existing field if this definition ID already has claimAmount. The workflow makes it mandatory.')
config+=request('Create configuration + native Elsa draft','createConfiguration','POST','/api/workflow-configurations',{'id':0,'definition_id':'{{definitionId}}','configuration':setup},configToken,comment='Missing/zero id creates. Conditions are ordered; the first match wins. The API maps claimAmount from the catalog.')
config+=request('Read draft','readConfiguration','GET','/api/workflow-configurations/{{createConfiguration.response.body.$.id}}',token=configToken)
config+=request('Update same draft using POST','updateConfiguration','POST','/api/workflow-configurations','''{
  "id": {{readConfiguration.response.body.$.id}},
  "expectedRevision": {{readConfiguration.response.body.$.revision}},
  "configuration": {
    "name": "{{readConfiguration.response.body.$.configuration.name}}",
    "startStage": "{{readConfiguration.response.body.$.configuration.startStage}}",
    "stages": {{readConfiguration.response.body.$.configuration.stages}}
  }
}''',configToken,comment='No PUT API. Missing positive id returns 404. Stale revision or editing a published version returns 409. Object references are intentionally unquoted JSON.')
config+=request('Validate without publishing','validateConfiguration','POST','/api/workflow-configurations/{{createConfiguration.response.body.$.id}}/validate',{},configToken,comment='200 valid=true; FluentValidation failures return 400 with field/message errors.')
config+=request('Publish exact Elsa version','publishConfiguration','POST','/api/workflow-configurations/{{createConfiguration.response.body.$.id}}/publish','{"expectedRevision": {{updateConfiguration.response.body.$.revision}}}',configToken,comment='Copy returned definitionId into Ticket-Workflow.http. This version is now immutable.')
config+='# END MAIN SEQUENCE\n'
config+=request('OPTIONAL next editable draft','nextDraft','POST','/api/workflow-configurations/{{createConfiguration.response.body.$.id}}/next-draft','{"expectedRevision": {{publishConfiguration.response.body.$.revision}}}',configToken,comment='Returns a NEW configuration id and Elsa version; edit that id with POST. Existing instances remain pinned to their original version.')
(ROOT/'Workflow-Configuration.http').write_text(config)

flow=header+'''# Paste the published definitionId from Workflow-Configuration.http.
# Main sequence: 6000 -> A -> B -> C -> D -> Approved.
# To test 4000 or 5000, change claimAmount and SKIP all C requests.
@definitionId = PASTE_PUBLISHED_DEFINITION_ID
'''
for name in ['requester','alice','bob','clara','dan','supervisor']:flow+=login(name,'configured-'+name)
flow+=request('Submit through native Elsa execute','startTicket','POST','/elsa/api/workflow-definitions/{{definitionId}}/execute',{'correlationId':'CLAIM-{{$guid}}','input':{'attributes':{'claimAmount':6000}}},'requesterLogin.response.body.$.accessToken',comment='Expect workflowState.subStatus=Suspended. Requester is derived from your token. Supply the conditional fields under input.attributes.')
for stage,user,bucket in [('A','alice',True),('B','bob',True),('C','clara',False),('D','dan',False)]:
    prefix=stage.lower(); token=user+'Login.response.body.$.accessToken'; scope='available' if bucket else 'assigned'; status='Available' if bucket else 'Assigned'
    flow+=request(stage+': find current task',prefix+'Tasks','GET','/elsa/api/user-tasks?scope='+scope+'&status='+status+'&workflowInstanceId={{startTicket.response.body.$.workflowState.id}}&includeTotalCount=true',token=token,comment='Wait/retry until items is nonempty. Every stage/visit has a NEW task ID.')
    if bucket:flow+=request(stage+': claim ownership',prefix+'Claim','POST','/elsa/api/user-tasks/{{'+prefix+'Tasks.response.body.$.items[0].id}}/claim','{"expectedRevision": {{'+prefix+'Tasks.response.body.$.items[0].revision}}, "operationId": "{{$guid}}"}',token,comment='Native Elsa enforces one owner and revision conflicts.')
    flow+=request(stage+': read latest revision',prefix+'Detail','GET','/elsa/api/user-tasks/{{'+prefix+'Tasks.response.body.$.items[0].id}}',token=token)
    flow+=request(stage+': approve',prefix+'Approve','POST','/elsa/api/user-tasks/{{'+prefix+'Tasks.response.body.$.items[0].id}}/complete','{"expectedRevision": {{'+prefix+'Detail.response.body.$.revision}}, "operationId": "{{$guid}}", "actionKey": "APPROVE", "data": {"comment": "'+stage+' reviewed"}}',token,comment='May return 202 accepted/completing. Direct native API calls still pass through our FluentValidation form provider.')
flow+=request('Read final instance','finishedInstance','GET','/elsa/api/workflow-instances/{{startTicket.response.body.$.workflowState.id}}',token='requesterLogin.response.body.$.accessToken',comment='Poll until status and subStatus are Finished. workflowState.output.ticketOutcome=Approved. Faulted/Cancelled are technical states, not rejection.')
flow+=request('Read workflow journey','journey','GET','/elsa/api/workflow-instances/{{startTicket.response.body.$.workflowState.id}}/journal',token='requesterLogin.response.body.$.accessToken')
flow+=request('Read stage A task audit','taskEvents','GET','/elsa/api/user-tasks/{{aTasks.response.body.$.items[0].id}}/events',token='aliceLogin.response.body.$.accessToken')
flow+='# END MAIN SEQUENCE\n# ALTERNATIVES: use INSTEAD of that stage Approve on a fresh instance. Do not run all branches together.\n'
for stage,user,outcome in [('A','alice','correction then A'),('B','bob','new A task'),('C','clara','terminal Rejected'),('D','dan','correction then D')]:
    prefix=stage.lower()
    flow+=request('INSTEAD reject '+stage+' -> '+outcome,'reject'+stage,'POST','/elsa/api/user-tasks/{{'+prefix+'Tasks.response.body.$.items[0].id}}/complete','{"expectedRevision": {{'+prefix+'Detail.response.body.$.revision}}, "operationId": "{{$guid}}", "actionKey": "REJECT", "data": {"reason": "Please correct this request"}}',user+'Login.response.body.$.accessToken',comment='Read current revision first. A nonblank reason is mandatory. Earlier decisions remain in history.')
flow+=request('After A/D rejection: find requester correction','correctionTasks','GET','/elsa/api/user-tasks?scope=assigned&status=Assigned&workflowInstanceId={{startTicket.response.body.$.workflowState.id}}',token='requesterLogin.response.body.$.accessToken')
flow+=request('Resubmit corrected attributes','resubmit','POST','/elsa/api/user-tasks/{{correctionTasks.response.body.$.items[0].id}}/complete','''{
  "expectedRevision": {{correctionTasks.response.body.$.items[0].revision}},
  "operationId": "{{$guid}}",
  "actionKey": "RESUBMIT",
  "data": {"attributes": {"claimAmount": 4000}}
}''','requesterLogin.response.body.$.accessToken',comment='Same instance; attributeRevision increments. Supply the COMPLETE object. This fixture returns A to A and D to D. Repeat the applicable stage requests.')
flow+='''\n# OPTIONAL supervisor reassignment for a task with no eligible initial assignee.
@blockedTaskId = PASTE_UNASSIGNED_TASK_ID
@eligibleUserId = PASTE_ELIGIBLE_APPROVER_ID
@blockedRevision = 1
'''
flow+=request('Supervisor finds unassigned work','blockedTasks','GET','/elsa/api/user-tasks?scope=all&status=Unassigned',token='supervisorLogin.response.body.$.accessToken')
flow+=request('Supervisor assigns work','reassign','POST','/elsa/api/user-tasks/{{blockedTaskId}}/assign','''{
  "expectedRevision": {{blockedRevision}},
  "operationId": "{{$guid}}",
  "assignee": {"kind": "user", "provider": "default", "id": "{{eligibleUserId}}"}
}''','supervisorLogin.response.body.$.accessToken',comment='Read task detail for the current revision. Native Elsa requires supervisor and assignment permissions.')
(ROOT/'Ticket-Workflow.http').write_text(flow)
print('Created three commented HTTP collections')

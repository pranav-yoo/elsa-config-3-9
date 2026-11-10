"""Live named-definition/field-master checks. Creates research records; never prints tokens."""
import argparse, json, subprocess, time, uuid
from pathlib import Path
from urllib.request import Request, urlopen
from urllib.error import HTTPError

parser=argparse.ArgumentParser();parser.add_argument('--base-url',default='http://localhost:5000');args=parser.parse_args()
root=Path(__file__).resolve().parents[1]
def api(method,path,body=None,token=None,expected=(200,201,202)):
    headers={'Authorization':'Bearer '+token} if token else {}
    if body is not None:headers['Content-Type']='application/json'
    try:
        with urlopen(Request(args.base_url+path,data=json.dumps(body).encode() if body is not None else None,headers=headers,method=method),timeout=30) as r:status,raw=r.status,r.read().decode()
    except HTTPError as e:status,raw=e.code,e.read().decode()
    assert status in expected,(path,status,raw[:900])
    return json.loads(raw) if raw else None

admin=api('POST','/elsa/api/identity/login',{'username':'admin','password':'password'})['accessToken']
definition='catalog-'+uuid.uuid4().hex
def field(key,kind='String',status='Active',family=None):
    return api('POST','/api/workflow-attributes',{'definitionId':family or definition,'attribute':{'key':key,'type':kind},'status':status},admin)
field('firstName');field('claimAmount','Decimal');field('department');field('obsolete','Boolean','Inactive');field('foreignField',family=definition+'-other')
setup={'name':'Catalog claim','startStage':'A','stages':[{'key':'A','title':'John approval','assignment':{'policy':'Specific','members':['external:username-demo:U001'],'userId':'external:username-demo:U001'},'approveTo':{'kind':'Rejected'},'rejectTo':{'kind':'Rejected'},'approveRules':[{'when':{'attribute':'claimAmount','operator':'GreaterThan','value':5000},'destination':{'kind':'Approved'}}]}]}
body={'id':0,'definition_id':definition,'configuration':setup}
condition=setup['stages'][0]['approveRules'][0]['when']
for invalid in ('obsolete','foreignField','unknown'):
    condition['attribute']=invalid
    failure=api('POST','/api/workflow-configurations',body,admin,(400,))
    assert any(invalid in error['message'] for error in failure['errors'])
condition['attribute']='claimAmount'
assignment=setup['stages'][0]['assignment']
assignment.update(policy='Condition',rules=[{'when':{'attribute':'department','operator':'Equals','value':'Finance'},'userId':assignment['userId']}])
config=api('POST','/api/workflow-configurations',body,admin)
assert config['definitionId']==definition
assert {x['key']:x['required'] for x in config['configuration']['attributes']}=={'claimAmount':True,'department':True}
api('POST','/api/workflow-configurations',body,admin,(409,))
setup['attributes']=[{'key':'injected','type':'String','required':True}]
updated=api('POST','/api/workflow-configurations',{'id':config['id'],'expectedRevision':config['revision'],'configuration':setup},admin)
assert {x['key'] for x in updated['configuration']['attributes']}=={'claimAmount','department'}
published=api('POST',f'/api/workflow-configurations/{config["id"]}/publish',{'expectedRevision':updated['revision']},admin)
for attributes in [{'claimAmount':6000},{'firstName':'John','claimAmount':6000,'department':'Finance'}]:
    api('POST',f'/elsa/api/workflow-definitions/{definition}/execute',{'input':{'attributes':attributes}},admin,(400,))
instance=api('POST',f'/elsa/api/workflow-definitions/{definition}/execute',{'input':{'attributes':{'claimAmount':6000,'department':'Finance'}}},admin)['workflowState']['id']
assert api('GET','/elsa/api/workflow-instances/'+instance,token=admin)['definitionId']==definition
next_draft=api('POST',f'/api/workflow-configurations/{config["id"]}/next-draft',{'expectedRevision':published['revision']},admin)
assert {x['key'] for x in next_draft['configuration']['attributes']}=={'claimAmount','department'}
stage=setup['stages'][0]
stage['approveRules']=[]
stage['assignment']={'policy':'Specific','members':assignment['members'],'userId':assignment['userId']}
empty=api('POST','/api/workflow-configurations',{'id':next_draft['id'],'expectedRevision':next_draft['revision'],'configuration':setup},admin)
assert empty['configuration']['attributes']==[]
assert len(api('GET',f'/api/workflow-configurations/{config["id"]}',token=admin)['configuration']['attributes'])==2
john=subprocess.check_output(['pwsh','-NoProfile','-ExecutionPolicy','Bypass','-File',str(root/'New-TestAccessToken.ps1')],text=True).strip()
api('POST','/api/workflow-attributes',{'definitionId':definition,'attribute':{'key':'bad','type':'String'}},john,(403,))
task=api('GET',f'/elsa/api/user-tasks?scope=assigned&status=Assigned&workflowInstanceId={instance}',token=john)['items'][0]
api('POST',f'/elsa/api/user-tasks/{task["id"]}/complete',{'expectedRevision':task['revision'],'operationId':str(uuid.uuid4()),'actionKey':'APPROVE','data':{}},john)
for _ in range(60):
    result=api('GET','/elsa/api/workflow-instances/'+instance,token=admin)
    if result['status']=='Finished':break
    time.sleep(.1)
assert result['workflowState']['output']['ticketOutcome']=='Approved'
api('POST','/api/workflow-attributes',{'definitionId':None,'attribute':{'key':'bad','type':'String'}},admin,(400,))
print('PASS required conditional fields, inactive/missing/foreign rejection, ignored inline schema, no-condition draft, immutable versions and native approval')

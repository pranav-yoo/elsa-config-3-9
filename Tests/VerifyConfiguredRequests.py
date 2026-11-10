"""Execute the actual main sequences in the two HTTP files, without logging or saving tokens.
Requires accounts provisioned by Authentication.http or Tests/ConfiguredWorkflows.py.
"""
import argparse,json,re,time,uuid,subprocess
from pathlib import Path
from urllib.request import Request,urlopen
from urllib.error import HTTPError
parser=argparse.ArgumentParser();parser.add_argument('--base-url',default='http://localhost:5000')
mode=parser.add_mutually_exclusive_group()
mode.add_argument('--external-masters',action='store_true')
mode.add_argument('--observed',action='store_true')
args=parser.parse_args()
ROOT=Path(__file__).resolve().parents[1];responses={};variables={}
def send(method,url,headers,body=None,expected=(200,201,202)):
    try:
        with urlopen(Request(url,data=body,headers=headers,method=method),timeout=30) as result:
            raw=result.read();return result.status,json.loads(raw) if raw else None
    except HTTPError as error:
        if error.code not in expected:
            raise AssertionError(f'HTTP {error.code}: {error.read().decode()[:500]}') from None
        return error.code,None
_,login=send('POST',args.base_url+'/elsa/api/identity/login',{'Content-Type':'application/json'},b'{"username":"admin","password":"password"}')
ids={}
if not (args.external_masters or args.observed):
    _,users=send('GET',args.base_url+'/elsa/api/identity/users',{'Authorization':'Bearer '+login['accessToken']})
    ids={name+'Id':next(user['id'] for user in users['users'] if user['name']=='configured-'+name) for name in ['alice','amy','bob','clara','dan']}
def substitute(match):
    expression=match.group(1)
    if expression=='$guid':return str(uuid.uuid4())
    if expression in variables:return variables[expression]
    name,path=expression.split('.response.body.$.',1);value=responses[name]
    for key,index in re.findall(r'([\w]+)|\[(\d+)\]',path):value=value[int(index)] if index else value[key]
    return json.dumps(value) if isinstance(value,(dict,list,bool)) else str(value)
external_tokens={}
if args.external_masters or args.observed:
    accounts=[('Username','john','usernameJohnToken'),('Email','john@example.test','emailJohnToken')] if args.external_masters else [
        ('Username',name,name+'Token') for name in ('clara','dan','john')]
    for provider,identity,variable in accounts:
        value=subprocess.check_output(['pwsh','-NoProfile','-ExecutionPolicy','Bypass','-File',str(ROOT/'New-TestAccessToken.ps1'),'-Provider',provider,'-Identity',identity],text=True).strip()
        assert re.fullmatch(r'[\w-]+\.[\w-]+\.[\w-]+',value),'Token script did not return one JWT'
        external_tokens[variable]=value
count=0
files=['Observed-Tasks.http'] if args.observed else ['External-Masters.http'] if args.external_masters else ['Workflow-Configuration.http','Ticket-Workflow.http']
for filename in files:
    source=(ROOT/filename).read_text().split('# END MAIN SEQUENCE')[0]
    variables.update(dict(re.findall(r'^@(\w+)\s*=\s*(.*)$',source,re.M)))
    variables.update(ids);variables.update(external_tokens);variables['baseUrl']=args.base_url
    if filename in ('Workflow-Configuration.http','Observed-Tasks.http'):variables['definitionId']='example-'+uuid.uuid4().hex
    if filename=='Ticket-Workflow.http':variables['definitionId']=responses['publishConfiguration']['definitionId']
    for section in re.split(r'^###.*$',source,flags=re.M):
        named=re.search(r'^# @name (\w+)$',section,re.M)
        if not named:continue
        name=named.group(1);expanded=re.sub(r'\{\{(.*?)\}\}',substitute,section)
        method,url,rest=re.search(r'^(GET|POST) (\S+)\n(.*)',expanded,re.M|re.S).groups()
        header_text,_,body=rest.partition('\n\n');headers=dict(line.split(': ',1) for line in header_text.splitlines() if ': ' in line)
        data=body.strip().encode() if body.strip() else None
        if data:json.loads(data)
        expectation=re.search(r'^# @expect ([\d ]+)$',section,re.M)
        expected=tuple(map(int,expectation[1].split())) if expectation else (200,201,202)
        for attempt in range(60):
            status,result=send(method,url,headers,data,expected)
            assert status in expected,(name,status)
            if name in ('aTasks','bTasks','cTasks','dTasks','usernameTasks','emailTasks','observedTasks','observedDanTask') and not result['items']:time.sleep(.1);continue
            if name in ('finishedInstance','usernameFinished','emailFinished','observedFinished') and result['status']!='Finished':time.sleep(.1);continue
            break
        else:raise AssertionError(name+': workflow did not become ready')
        responses[name]=result;count+=1
        if name in ('finishedInstance','usernameFinished','emailFinished','observedFinished'):assert result['workflowState']['output']['ticketOutcome']==('Rejected' if name=='emailFinished' else 'Approved')
        if name=='observedTasks':
            assert result['totalCount']==1 and result['items'][0]['title']=='C - Clara review'
            assert result['items'][0]['allowedActions']==[]
        if name in ('observerAssigned','observerAvailable','outsiderObserved'):assert result['items']==[]
        if name=='outsiderObserved':assert result['totalCount']==0
        if name=='observedDetail':
            assert result['data']['claimAmount']==6000 and result['disclosure']['canViewHistory']
            assert result['allowedActions']==[]
        if name=='observedEvents':assert any(event['kind']=='Created' for event in result['items']),result
        print(f'PASS {filename}: {name} HTTP {status}')
print(f'PASS {count} actual HTTP-file requests; named response IDs/revisions and status filters verified')

if args.observed:
    def observed_api(path, actor='admin', body=None):
        bearer=login['accessToken'] if actor=='admin' else external_tokens[actor+'Token']
        return send('GET' if body is None else 'POST',args.base_url+path,
                    {'Authorization':'Bearer '+bearer,'Content-Type':'application/json'},
                    json.dumps(body).encode() if body is not None else None)[1]

    execute='/elsa/api/workflow-definitions/'+variables['definitionId']+'/execute'
    payload={'input':{'attributes':{'claimAmount':6000}}}
    existing=observed_api(execute,body=payload)['workflowState']['id']
    observed_path='/elsa/api/user-tasks?scope=observed'
    first=observed_api(observed_path+'&limit=1&includeTotalCount=true','dan')
    second=observed_api(observed_path+'&limit=1&cursor='+first['nextCursor'],'dan')
    assert first['totalCount']>=2 and first['items'][0]['id']!=second['items'][0]['id']

    published=responses['observerPublished']
    draft=observed_api(f'/api/workflow-configurations/{published["id"]}/next-draft',
                       body={'expectedRevision':published['revision']})
    setup=draft['configuration']
    setup.pop('attributes')
    setup['stages'][0].pop('viewableByStages')
    draft=observed_api('/api/workflow-configurations',
                       body={'id':draft['id'],'expectedRevision':draft['revision'],'configuration':setup})
    observed_api(f'/api/workflow-configurations/{draft["id"]}/publish',body={'expectedRevision':draft['revision']})
    new=observed_api(execute,body=payload)['workflowState']['id']
    assert len(observed_api(observed_path+'&workflowInstanceId='+existing,'dan')['items'])==1
    assert observed_api(observed_path+'&workflowInstanceId='+new,'dan')['items']==[]
    print('PASS observed pagination and observer snapshots across definition versions')

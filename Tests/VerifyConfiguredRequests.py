"""Execute the actual main sequences in the two HTTP files, without logging or saving tokens.
Requires accounts provisioned by Authentication.http or Tests/ConfiguredWorkflows.py.
"""
import argparse,json,re,time,uuid,subprocess
from pathlib import Path
from urllib.request import Request,urlopen
from urllib.error import HTTPError
parser=argparse.ArgumentParser();parser.add_argument('--base-url',default='http://localhost:5000');parser.add_argument('--external-masters',action='store_true');args=parser.parse_args()
ROOT=Path(__file__).resolve().parents[1];responses={};variables={}
def send(method,url,headers,body=None):
    try:
        with urlopen(Request(url,data=body,headers=headers,method=method),timeout=30) as result:
            raw=result.read();return result.status,json.loads(raw) if raw else None
    except HTTPError as error:raise AssertionError(f'HTTP {error.code}: {error.read().decode()[:500]}') from None
_,login=send('POST',args.base_url+'/elsa/api/identity/login',{'Content-Type':'application/json'},b'{"username":"admin","password":"password"}')
ids={}
if not args.external_masters:
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
if args.external_masters:
    for provider,identity,variable in [('Username','john','usernameJohnToken'),('Email','john@example.test','emailJohnToken')]:
        value=subprocess.check_output(['pwsh','-NoProfile','-ExecutionPolicy','Bypass','-File',str(ROOT/'New-TestAccessToken.ps1'),'-Provider',provider,'-Identity',identity],text=True).strip()
        assert re.fullmatch(r'[\w-]+\.[\w-]+\.[\w-]+',value),'Token script did not return one JWT'
        external_tokens[variable]=value
count=0
for filename in (['External-Masters.http'] if args.external_masters else ['Workflow-Configuration.http','Ticket-Workflow.http']):
    source=(ROOT/filename).read_text().split('# END MAIN SEQUENCE')[0]
    variables.update(dict(re.findall(r'^@(\w+)\s*=\s*(.*)$',source,re.M)))
    variables.update(ids);variables.update(external_tokens);variables['baseUrl']=args.base_url
    if filename=='Ticket-Workflow.http':variables['definitionId']=responses['publishConfiguration']['definitionId']
    for section in re.split(r'^###.*$',source,flags=re.M):
        named=re.search(r'^# @name (\w+)$',section,re.M)
        if not named:continue
        name=named.group(1);expanded=re.sub(r'\{\{(.*?)\}\}',substitute,section)
        method,url,rest=re.search(r'^(GET|POST) (\S+)\n(.*)',expanded,re.M|re.S).groups()
        header_text,_,body=rest.partition('\n\n');headers=dict(line.split(': ',1) for line in header_text.splitlines() if ': ' in line)
        data=body.strip().encode() if body.strip() else None
        if data:json.loads(data)
        for attempt in range(60):
            status,result=send(method,url,headers,data)
            if name in ('aTasks','bTasks','cTasks','dTasks','usernameTasks','emailTasks') and not result['items']:time.sleep(.1);continue
            if name in ('finishedInstance','usernameFinished','emailFinished') and result['status']!='Finished':time.sleep(.1);continue
            break
        else:raise AssertionError(name+': workflow did not become ready')
        responses[name]=result;count+=1
        if name in ('finishedInstance','usernameFinished','emailFinished'):assert result['workflowState']['output']['ticketOutcome']==('Rejected' if name=='emailFinished' else 'Approved')
        print(f'PASS {filename}: {name} HTTP {status}')
print(f'PASS {count} actual HTTP-file requests; named response IDs/revisions and status filters verified')

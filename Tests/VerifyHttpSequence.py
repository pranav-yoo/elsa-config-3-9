"""Execute the MAIN SEQUENCE of these .http files using only the Python standard library."""
import argparse
import json
import re
import time
import uuid
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import Request, urlopen

parser=argparse.ArgumentParser()
parser.add_argument('collection',type=Path)
parser.add_argument('--base-url',default='http://localhost:5000')
args=parser.parse_args()
source=args.collection.read_text().split('# END MAIN SEQUENCE')[0]
variables=dict(re.findall(r'^@(\w+)\s*=\s*(.*)$',source,re.M))
variables['baseUrl']=args.base_url.rstrip('/')
responses={}

def substitute(match):
    name=match.group(1)
    if name=='$guid': return str(uuid.uuid4())
    if name in variables: return variables[name]
    request_name,path=name.split('.response.body.$.',1)
    value=responses[request_name]
    for key,index in re.findall(r'([\w]+)|\[(\d+)\]',path):
        value=value[int(index)] if index else value[key]
    return str(value)

count=0
for section in re.split(r'^###.*$',source,flags=re.M):
    named=re.search(r'^# @name (\w+)$',section,re.M)
    if not named: continue
    name=named.group(1)
    expanded=re.sub(r'\{\{(.*?)\}\}',substitute,section)
    match=re.search(r'^(GET|POST|PUT|DELETE) (\S+)\n(.*)',expanded,re.M|re.S)
    method,url,rest=match.groups()
    header_text,_,body=rest.partition('\n\n')
    headers=dict(line.split(': ',1) for line in header_text.splitlines() if ': ' in line)
    data=body.strip().encode() if body.strip() else None
    if data is not None: json.loads(data)
    for attempt in range(40):
        try:
            with urlopen(Request(url,data=data,headers=headers,method=method),timeout=20) as result:
                status=result.status
                raw=result.read().decode()
                response=json.loads(raw) if raw else None
        except HTTPError as error:
            raise RuntimeError(f'{name}: HTTP {error.code}; response={error.read().decode()[:350]}') from None
        if name in ('aTasks','bTasks','cTasks','dTasks') and not response.get('items'):
            time.sleep(0.15); continue
        if name=='finishedInstance' and response.get('status')!='Finished':
            time.sleep(0.15); continue
        break
    else: raise RuntimeError(f'{name}: asynchronous state did not become ready')
    if name.endswith('Login') and not response.get('accessToken'): raise RuntimeError(f'{name}: no access token')
    if name=='startClaim' and response['workflowState']['subStatus']!='Suspended':
        raise RuntimeError(f'{name}: expected Suspended, got {response["workflowState"]["subStatus"]}')
    if name=='finishedInstance':
        assert response['workflowState']['output']['ticketStatus']=='Completed'
    responses[name]=response
    count+=1
    print(f'PASS {name}: HTTP {status}')
print(f'PASS: {count} actual HTTP requests; A-B-C-D completed; tokens not logged')

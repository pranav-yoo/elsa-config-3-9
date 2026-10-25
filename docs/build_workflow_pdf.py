"""Generate the API research handbook from captured responses and the real definition."""
import json
import textwrap
from pathlib import Path
from xml.sax.saxutils import escape

from reportlab.lib import colors
from reportlab.lib.enums import TA_LEFT
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import inch
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, Table, TableStyle, PageBreak, Preformatted
from pypdf import PdfReader

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'output/pdf'
OUT.mkdir(parents=True, exist_ok=True)
captures = json.loads((ROOT / 'docs/api-captures.json').read_text(encoding='utf-8-sig'))
definition = json.loads((ROOT / 'Samples/Definitions/expense-claim.definition.json').read_text(encoding='utf-8-sig'))
configuration = json.loads(definition['model']['root']['configurationJson']['expression']['value'])

styles = getSampleStyleSheet()
styles.add(ParagraphStyle(name='CoverTitle', fontName='Helvetica-Bold', fontSize=30, leading=35, textColor=colors.HexColor('#103754'), spaceAfter=18))
styles.add(ParagraphStyle(name='Deck', fontName='Helvetica', fontSize=14, leading=20, textColor=colors.HexColor('#4B6172'), spaceAfter=14))
styles.add(ParagraphStyle(name='SmallNote', fontName='Helvetica', fontSize=8, leading=11, textColor=colors.HexColor('#536779'), spaceAfter=6))
styles.add(ParagraphStyle(name='CodeBlock', fontName='Courier', fontSize=8, leading=10.5, leftIndent=7, rightIndent=4, backColor=colors.HexColor('#F0F4F7'), borderPadding=7, spaceBefore=4, spaceAfter=10, splitLongWords=True, wordWrap='CJK'))
styles['BodyText'].fontSize=10
styles['BodyText'].leading=14
styles['BodyText'].spaceAfter=8
styles['Heading1'].textColor=colors.HexColor('#103754')
styles['Heading1'].fontSize=18
styles['Heading1'].leading=22
styles['Heading2'].textColor=colors.HexColor('#116B78')
styles['Heading2'].fontSize=12
story=[]
markdown=[]

def p(text, style='BodyText'):
    story.append(Paragraph(text, styles[style]))
    markdown.append(text.replace('<b>','').replace('</b>','').replace('<br/>','\n'))

def heading(text):
    p(text,'Heading1')

def sub(text):
    p(text,'Heading2')

def code(value, language='json'):
    def pretty(item, indent=0):
        flat=json.dumps(item,ensure_ascii=True,separators=(', ', ': '))
        if len(flat)+indent <= 88 or not isinstance(item,(dict,list)): return flat
        padding=' '*(indent+2)
        if isinstance(item,dict):
            parts=[padding+json.dumps(k)+': '+pretty(v,indent+2) for k,v in item.items()]
            return '{\n'+',\n'.join(parts)+'\n'+' '*indent+'}'
        return '[\n'+',\n'.join(padding+pretty(v,indent+2) for v in item)+'\n'+' '*indent+']'
    raw = value if isinstance(value,str) else pretty(value)
    # Paragraph line wrapping preserves the visible JSON without clipping long strings.
    escaped = '<br/>'.join(escape(line).replace(' ', '&#160;') for line in raw.splitlines())
    story.append(Paragraph(escaped, styles['CodeBlock']))
    markdown.append(f'```{language}\n{raw}\n```')

def table(headers, rows, widths):
    data=[[Paragraph(escape(str(c)),styles['BodyText']) for c in headers]]
    data += [[Paragraph(escape(str(c)),styles['SmallNote']) for c in row] for row in rows]
    t=Table(data,colWidths=widths,repeatRows=1,hAlign='LEFT')
    t.setStyle(TableStyle([('BACKGROUND',(0,0),(-1,0),colors.HexColor('#DFEAF0')),('VALIGN',(0,0),(-1,-1),'TOP'),('TOPPADDING',(0,0),(-1,-1),6),('BOTTOMPADDING',(0,0),(-1,-1),6),('LINEBELOW',(0,0),(-1,0),0.7,colors.HexColor('#7994A5')),('ROWBACKGROUNDS',(0,1),(-1,-1),[colors.white,colors.HexColor('#F7F9FA')])]))
    story.append(t)
    markdown.append('| '+' | '.join(headers)+' |\n| '+' | '.join(['---']*len(headers))+' |\n'+'\n'.join('| '+' | '.join(str(c) for c in row)+' |' for row in rows))

def page(title):
    story.append(PageBreak()); heading(title)

def find(title, index=0):
    matches=[x for x in captures if x['title']==title]
    if not matches: raise ValueError(f'Missing capture: {title}')
    return matches[index]

def response_projection(entry):
    body=entry['response']
    if not isinstance(body,dict): return body
    if 'task' in body:
        task=body['task']
        task_excerpt={k:task[k] for k in ('id','status','revision') if k in task}
        if task.get('assignee'): task_excerpt['assignee']={'id':task['assignee']['id']}
        return {**{k:body[k] for k in ('operationId','status','revision') if k in body},'task':task_excerpt}
    return body

def exchange(title, response=None, include_body=True, caption=None):
    entry=find(title)
    if caption: sub(caption)
    req=entry['request']
    lines=[req['method']+' http://localhost:5000'+req['path']]
    if req['authorization']: lines.append('Authorization: '+req['authorization'])
    if req['body'] is not None: lines.append('Content-Type: application/json')
    code('\n'.join(lines),'http')
    if req['body'] is not None and include_body: code(req['body'])
    p(f"<b>Captured HTTP {entry['statusCode']}</b>. Response excerpt; fields outside the excerpt are omitted. IDs and timestamps are from an isolated test run.",'SmallNote')
    code(response if response is not None else response_projection(entry))

p('ELSA 3.9<br/>TICKET API GUIDE','CoverTitle')
p('Requests, responses and approval / rejection journeys','Deck')
p('Expense Claim | Tested application | 25 October 2026','Deck')
p('This guide explains how to use the tested application through Swagger and Elsa APIs. It covers login, workflow publication, ticket submission, task claiming, approval, rejection, correction and history. Configuration internals are excluded.')
p('The app has been restored to the configurationJson-backed workflow that supports Approve, Reject and Resubmit. The complete publication request is supplied as a companion JSON file; you do not need to edit it to follow this guide.')
p(f'Evidence: {len(captures)} captured HTTP exchanges from an isolated preview instance. Response excerpts use actual captured values; JWTs and sample login passwords are redacted. API examples use localhost:5000; the capture run used port 5007.','SmallNote')
table(['Step','API journey'],[['1','Choose your user and understand the stage route'],['2','Login, publish and execute'],['3','Find and claim a task'],['4','Approve and check the next stage'],['5','Reject to A, requester or terminal Rejected'],['6','Correct and resubmit'],['7','Read the full journey'],['8','Handle errors and retries'],['9','Review tested results']],[55,440])
page('1. Users and the ticket journey')
p('Run the application and open http://localhost:5000/swagger. Use the account whose role matches the next action. The verified run uses in-memory preview mode; restarting preview clears definitions and tickets. This guide does not claim PostgreSQL restart persistence.')
table(['Account','Password / purpose'],[['admin','password / administrative overview'],['configurator','Password123! / publish workflow'],['requester','Password123! / submit and correct claims'],['alice, amy','Password123! / stage A'],['bella, bob','Password123! / stage B'],['clara','Password123! / high-value stage C'],['dan','Password123! / final stage D'],['supervisor','Password123! / native supervision permissions']],[120,375])
p('These are local research credentials. All user IDs equal the sample usernames. A and B are buckets: one eligible member claims the task. C belongs to Clara and D belongs to Dan. B is not automatic round robin.')
code('A --Approve--> B --amount > 5000--> C --Approve--> D\n                 --amount <= 5000----------------> D\nD --Approve--> Completed\n\nA --Reject--> requester --Resubmit--> A\nB --Reject--> A (new task)\nC --Reject--> Rejected (terminal)\nD --Reject--> requester --Resubmit--> D','text')
p('A claim of exactly 5000 skips C. Rejected A and D return for correction to the same stage. Changes made after D returns a claim do not replay B or require a new C approval.')

page('2. Login, publish and execute')
exchange('Login configurator',caption='2.1 Login and Swagger Authorize')
p('Use Password123! for the sample configurator (the capture redacts it). Copy accessToken into the Swagger JWT bearer Authorize field. Use each task owner\'s token when acting on that task. A successful HTTP response without an accessToken does not prove authentication.')
entry=find('Publish configurationJson definition'); wd=entry['response']['workflowDefinition']
exchange('Publish configurationJson definition',response={'workflowDefinition':{k:wd[k] for k in ('id','definitionId','name','version','isPublished')}},include_body=False,caption='2.2 Publish the complete definition')
p('Request body: the complete companion definition JSON, including model.root.configurationJson.expression.value. Publishing uses the configurator token. The activity validates the business configuration when executed; publishing alone does not guarantee the configuration is valid.')
story.append(PageBreak())
run=find('Start approved-4000')['response']; state=run['workflowState']
exchange('Start approved-4000',response={'workflowState':{k:state[k] for k in ('id','definitionId','correlationId','status','subStatus')}},caption='2.3 Submit a claim as requester')
p('This 4000 claim follows A-B-D. Change claimAmount to 6000 to include Clara at C. The find, claim and first approval examples below refer to this same captured 4000 workflow. Replace captured IDs with IDs from your own server.')
p('correlationId is your business reference. requesterId is the stable identity ID, checked against the authenticated requester (admin may submit for another requester). claimAmount is a JSON number. Save workflowState.id: it is the workflow instance ID used for task filtering and history. correlationId is not a submission idempotency key; repeated execute calls may create another instance.')

page('3. Find a task and claim ownership')
entry=find('Find A: task'); items=entry['response']['items']
exchange('Find A: task',response={'items':[{k:items[0][k] for k in ('id','title','status','workflowInstanceId','revision')}],'totalCount':entry['response'].get('totalCount')},caption='3.1 Administrative overview')
p('scope=all requires elevated access. Ordinary approvers use GET /elsa/api/user-tasks?scope=available for claimable work and scope=assigned for owned work, adding workflowInstanceId as needed. Only eligible identities see their work; a valid token from the wrong member may legitimately return an empty list.')
exchange('Claim A: Initial review (Alice/Amy bucket)',caption='3.2 Claim A as Alice')
p('<b>expectedRevision</b> is the latest revision from this task, not a workflow or definition version. This sample starts A at revision 1; claiming returns revision 2. Read the task again before completion if anything changed.')
p('<b>operationId</b> is a caller-generated UUID identifying one command attempt. Keep the same ID and identical payload when retrying an uncertain request. Generate a new ID for a different command; do not reuse the claim ID for completion.')
p('A/B buckets require a claim before completion. C and D are assigned directly. Use GET /elsa/api/user-tasks/{taskId} to read current state, task data and available completion actions. GET details can contain a form descriptor supplied by TicketFormProvider; no frontend has been implemented.')

page('4. Approval process')
exchange('Approve A: Initial review (Alice/Amy bucket)',caption='4.1 Approve A as its owner')
p('actionKey is case-sensitive: Approve or Reject for review tasks, Resubmit for correction tasks. data is the form payload. During review, only string reason/comment fields are allowed; data={} is valid for approval. Reject requires a non-empty reason.')
p('An accepted completion can return HTTP 202 with status=accepted and task.status=Completing while Elsa resumes the workflow. Poll the task or instance and list the next stage task. A successful acceptance is not yet proof that the next stage was created.')
table(['Current stage','After Approve','Next actor'],[
['A','B bucket','Bella or Bob claims and approves'],
['B, amount <= 5000','D direct assignment','Dan'],
['B, amount > 5000','C direct assignment','Clara'],
['C','D direct assignment','Dan'],
['D','Terminal Completed','No further task']],[125,205,165])
p('Use the same complete endpoint with each next task ID, its latest revision and its owner token. Each stage visit creates a separate task.')
entry=find('Finished instance (Completed)'); body=entry['response']
exchange('Finished instance (Completed)',response={'id':body['id'],'status':body['status'],'workflowState':{'output':body['workflowState']['output']}},caption='4.2 Confirm completion')

page('5. Rejection process')
table(['Rejected at','Destination','Effect'],[
['A','Requester correction','Requester edits attributes and resubmits to A.'],
['B','A','A new A task is created. Earlier A/B decisions remain in history.'],
['C','$rejected','Workflow ends; output.ticketStatus is Rejected.'],
['D','Requester correction','Requester edits attributes and resubmits directly to D.']],[80,135,280])
exchange('Reject C: High value approval (Clara)',caption='5.1 Example: Clara rejects C terminally')
p('Use each stage\'s own task ID and owner token. The client sends the action and reason; it does not send a destination. The interpreter reads rejectTo from the configuration snapshot stored when the workflow began.')
entry=find('Finished instance (Rejected)'); body=entry['response']
exchange('Finished instance (Rejected)',response={'id':body['id'],'status':body['status'],'workflowState':{'output':body['workflowState']['output']}},caption='5.2 Confirm business rejection')
p('Both approved and rejected business outcomes have Elsa status=Finished. Inspect workflowState.output.ticketStatus to distinguish Completed from Rejected. Rejected is a business outcome, not an Elsa execution fault.')

page('6. Return, correct and resubmit')
exchange('Reject A: Initial review (Alice/Amy bucket)',caption='6.1 Alice returns A to the requester')
p('Log in as requester and retrieve the assigned Correct and resubmit Expense Claim task. Use Resubmit and supply the complete attributes object.')
exchange('Resubmit Correct and resubmit Expense Claim',caption='6.2 Requester changes 6000 to 4000')
p('The same instance continues: new A task, then B uses the corrected 4000 and routes to D. Earlier tasks retain their data snapshots. At D, correction resumes D even if the amount increases; B/C are not replayed.')

page('7. Read the full journey')
code('GET /elsa/api/user-tasks?scope=all&workflowInstanceId=<workflowInstanceId>&includeTotalCount=true\nGET /elsa/api/user-tasks/<taskId>\nGET /elsa/api/user-tasks/<taskId>/events\nGET /elsa/api/workflow-instances/<workflowInstanceId>\nGET /elsa/api/workflow-instances/<workflowInstanceId>/journal\nGET /elsa/api/workflow-instances/<workflowInstanceId>/execution-state','http')
p('Use an authorized administrator for the overview. Native task access can hide unrelated tasks with 404. Compare workflowInstanceId, reference/correlationId, task title and timestamps. Follow every task ID to read its individual event history; the definition ID identifies the template, not one claim journey.')
event=find('Task event history')['response']; first_events=event.get('items',[])[:2]
exchange('Task event history',response={'items':first_events},caption='7.1 Captured task events (first two)')
p('Task events record native lifecycle changes and actors. Task details retain the task-data snapshot; decision completion data is associated with that native task. Combine tasks, events and workflow journal for the overview. No custom consolidated journey endpoint or diagram UI has been added.')
page('8. Errors and retries')
exchange('Reject without reason is invalid',caption='8.1 Validation failure')
table(['Status / symptom','Interpretation / next step'],[
['401','Missing, invalid or expired bearer token. Sign in again; tokens must match this server signing key.'],
['403','Permission denied. Requester cannot publish definitions. Task membership and ownership also restrict commands.'],
['404 on task details','Task absent or hidden from this actor. Do not assume the workflow never created it.'],
['409','A conflicting revision or operation may require refreshing the task. A competing claimant may instead receive 403/404 depending on access evaluation.'],
['422 form-invalid','Review/correction payload was rejected. Reject requires reason; review cannot change attributes.'],
['200 execute with a faulted state','Activity-level business validation can become a workflow incident. Check workflowState.subStatus/incidents; do not treat HTTP 200 alone as a successful ticket submission.'],
['202 accepted','Command accepted; poll for task completion and next work. Retry uncertain requests with the same operation ID and exact payload.'],
['Empty task list','Check actor ID, candidate membership, scope, workflowInstanceId, state and definition version.'],
['PostgreSQL recovery mode','Database server readiness issue. Preview can run, but it skips migrations and cannot prove persistence.']],[130,365])
p('Logs include HTTP method/path/status/duration, X-Trace-Id, sanitized database target and connection/migration diagnostics, plus workflow ID, stage and visit. They do not intentionally log passwords, bearer tokens or attribute values. The host retains native scoped-service lifetime registrations required by this Elsa 3.9 sample.')
p('The raw capture file redacts issued tokens and sample login passwords. Do not log Authorization headers in your real application. A correlation ID alone does not make workflow start idempotent; only the tested task commands use native operation IDs/revisions here.')

page('9. Verified test results')
table(['Scenario','Result'],[
['4000 approval','A -> B -> D -> Completed'],
['5000 approval','A -> B -> D -> Completed'],
['6000 approval','A -> B -> C -> D -> Completed'],
['Reject A, correct to 4000','Requester -> A -> B -> D; same workflow instance'],
['Reject B','New A visit, then B and D'],
['Reject C','Terminal business Rejected'],
['Reject D, correct to 6000','Requester -> D; no automatic C re-check'],
['Duplicate decision','Same operation ID/payload replay accepted without a second route transition'],
['Invalid decisions','Missing rejection reason and review-time attribute edits return 422'],
['Access / claims','Unauthenticated 401; requester publishing 403; unrelated task details 404; competing/stale claim denied'],
['Build','dotnet build --no-restore -o bin/configresearch: passed, zero warnings'],
['PostgreSQL restart durability','Not validated in the captured in-memory run']],[175,320])
sub('Repeat the checks')
code('./Tests/ConfigurationApi.ps1 -BaseUrl http://localhost:5000','powershell')
p('The test creates sample definitions and workflow instances in the target environment. Run it only against a disposable research instance. Full request/response bodies, apart from authentication redaction, are in docs/api-captures.json. The PDF uses explicitly labeled excerpts for readability.')
sub('Full requests and responses')
p('See docs/api-captures.json for complete captured exchanges and Tickets.http for reusable requests. The publication body is Samples/Definitions/expense-claim.definition.json. The PDF uses labeled response excerpts for readability; dynamic IDs, revisions and timestamps differ on your server.')

def chrome(canvas, doc):
    width,height=doc.pagesize
    canvas.setStrokeColor(colors.HexColor('#C8D5DF')); canvas.line(48,43,width-48,43)
    canvas.setFillColor(colors.HexColor('#536779')); canvas.setFont('Helvetica',8)
    canvas.drawString(48,30,'Elsa 3.9 | Ticket API Guide | 25 October 2026')
    canvas.drawRightString(width-48,30,str(doc.page))

path=OUT/'Elsa-3.9-Ticket-Workflow-API-Guide.pdf'
doc=SimpleDocTemplate(str(path),pagesize=(595.28,841.89),rightMargin=48,leftMargin=48,topMargin=45,bottomMargin=58,title='Elsa 3.9 Ticket Workflow API Guide',author='IL Workflow Research')
doc.build(story,onFirstPage=chrome,onLaterPages=chrome)
(ROOT/'docs/TICKET-WALKTHROUGH.md').write_text('\n\n'.join(markdown),encoding='utf-8')
reader=PdfReader(path)
all_text='\n'.join(page.extract_text() or '' for page in reader.pages)
for phrase in ('configurationJson','Reject','Resubmit','Rejected','Approve','5000','ticketStatus'):
    assert phrase in all_text, phrase
assert len(reader.pages)>=8
print(json.dumps({'pdf':str(path),'pages':len(reader.pages),'bytes':path.stat().st_size,'captures':len(captures)}))

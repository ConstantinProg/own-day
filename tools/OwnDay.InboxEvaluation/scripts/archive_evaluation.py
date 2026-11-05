import json
from pathlib import Path

root=Path('evaluation-results')
runs=[]
for directory in ['live-baseline-v1','live-candidate-v2','live-candidate-v3']:
    m=json.loads((root/directory/'run.json').read_text(encoding='utf-8-sig'))
    s=json.loads((root/directory/'summary.json').read_text(encoding='utf-8-sig'))
    rows=[json.loads(l) for l in (root/directory/'results.jsonl').read_text(encoding='utf-8-sig').splitlines()]
    runs.append(dict(directory=directory,metadata={k:m[k] for k in ['runId','startedAt','datasetVersion','datasetHash','descriptor','promptHash','schemaHash','revision','runtime','sdkVersion','maxRequests','prices']},
        scorerVersion='inbox-scorer-v1',summary=s,selectedScenarioIds=[s['id'] for s in m['scenarios']],
        failedDiagnostics=[dict(scenario=next(s for s in m['scenarios'] if s['id']==r['scenarioId']),result=r) for r in rows if not r['checks']['joint']]))
data=dict(date='2026-10-25',method='Real OpenAI API calls on synthetic authored development examples; deterministic diagnostics only. Human semantic review pending. Not an objective semantic accuracy estimate.',
    ratesSource='https://developers.openai.com/api/docs/models/gpt-6-luna',
    costCaveat='Base token estimate only; SDK fields do not represent cache-write premiums, regional/service-tier surcharges or taxes. Failed TLS runs returned no usage and are not model-quality measurements.',
    runs=runs,comparisons=[json.loads((root/f).read_text(encoding='utf-8-sig')) for f in ['baseline-v1-vs-v3.json','v2-vs-v3.json']])
path=Path('evaluation/inbox/reports/2026-10-25.json')
path.parent.mkdir(parents=True,exist_ok=True)
path.write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')

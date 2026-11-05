import json
from pathlib import Path

data=json.loads(Path('evaluation/inbox/golden-v1.json').read_text(encoding='utf-8'))
data['version']='inbox-golden-v2'
for s in data['scenarios']:
    if s['id']=='action-10':
        s['variants'][0]['items'][0]['contentAnchorGroups'][1]=['бумаг','бумаж']
    if s['id']=='waiting-09':
        s['variants'][0]['items'][0]['contentAnchorGroups'][0]=['электриче','электроснабж','свет']
    if s['id']=='split-07':
        s['text']='Поговорить с Олей про отпуск и ремонт — вместе или это два отдельных разговора? Пока не решил.'
        s['reviewCriteria']['canonicalization']='Сохранить обе темы и явную неопределённость о числе разговоров. Не выдумывать встречу или принятое решение.'
        s['variants'].append(dict(reason='Два предварительных разговора при сохранении неуверенности о границе.', items=[
            dict(kinds=['Action'],propertySets=[dict(source=None,waitingSince=None,waitingSinceEvidence=None,unresolvedDateText=None)],projectIdSets=[[]],requireAmbiguity=True,contentAnchorGroups=[['Ол'],['отпуск']]),
            dict(kinds=['Action'],propertySets=[dict(source=None,waitingSince=None,waitingSinceEvidence=None,unresolvedDateText=None)],projectIdSets=[[]],requireAmbiguity=True,contentAnchorGroups=[['Ол'],['ремонт']])]))
Path('evaluation/inbox/golden-v2.json').write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')

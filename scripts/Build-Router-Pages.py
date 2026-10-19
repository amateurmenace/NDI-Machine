"""Build only owned Companion router pages from a secret-excluded local export.

No network access or writes to Companion. The installer uses single-page import
for owned pages 37-43, then copies exactly six explicitly authorized home cells.
"""
import copy, gzip, json, sys, uuid
from pathlib import Path

backup, output = map(Path, sys.argv[1:3])
raw = backup.read_bytes()
config = json.loads(gzip.decompress(raw) if raw[:2] == b'\x1f\x8b' else raw)
assert config['version'] == 12 and len(config['pages']) in (36,43), 'Inspect current page count/schema before installing'
connection = next(k for k,v in config['instances'].items() if v.get('label') == 'ndi_machine')
base = config['pages']['1']['controls']['2']['0']
uid = lambda: uuid.uuid4().hex[:21]
value = lambda x: {'value':x, 'isExpression':False}
navy, teal, blue, gold, red = 0x132536, 0x0b9b97, 0x295dac, 0xd69635, 0xb53b4c
def action(name, options, internal=False):
    return {'type':'action','id':uid(),'definitionId':name,'connectionId':'internal' if internal else connection,'options':{k:value(v) for k,v in options.items()},'children':{}}
def button(text, color=navy, deck='mk', command=None, index=0, page=None, group=None):
    b=copy.deepcopy(base)
    b['feedbacks']=[]; b['localVariables']=[]
    b['options']['notes']='NDI Machine quick router: source, Input 3/4, TAKE on one screen. More inputs opens all eight. Navigation never routes.'
    layers=[copy.deepcopy(l) for l in base['style']['layers'] if l['id'] in ('canvas','bg','accent','label')]
    for l in layers:
        if l['id']=='bg':
            l['color']=value(color);l['cornerRadius']=value(8);l['borderWidth']=value(1);l['borderColor']=value(0x45617a)
        if l['id']=='accent': l['x']=value(10);l['width']=value(80);l['y']=value(5);l['height']=value(3);l['color']=value(0x80eee0)
        if l['id']=='label':
            for k,v in {'text':text,'x':5,'y':14,'width':90,'height':80,'fontsize':22,'fontsizeAllowShrink':True,'color':0xf4f9ff}.items():l[k]=value(v)
    b['style']['layers']=layers
    down=[]
    if command: down.append(action('router',{'command':command,'index':index,'deck':deck}))
    if page:
        navigation=action('set_page',{'surfaceId':'self','page':page},True)
        if isinstance(page,str):navigation['options']['page']['isExpression']=True
        down.append(navigation)
    b['steps']={'0':{'action_sets':{'down':down,'up':[]},'options':{'runWhileHeld':[]}}}
    if group:
        b['feedbacks']=[{'id':uid(),'type':'feedback','definitionId':'router_selected','connectionId':connection,'options':{k:value(v) for k,v in {'deck':deck,'group':group,'index':index}.items()},'upgradeIndex':-1,'isInverted':value(False),'styleOverrides':[{'overrideId':uid(),'elementId':'bg','elementProperty':'color','override':value(teal)}]}]
    return b
def variable(deck,name):return f'$(ndi_machine:{deck}_{name})'
pages={}
def page(number,name,columns,rows):
    pages[str(number)]={'id':uid(),'name':name,'controls':{},'gridSize':{'minColumn':0,'maxColumn':columns-1,'minRow':0,'maxRow':rows-1},'pageVariables':[]}
def put(number,row,col,button):pages[str(number)]['controls'].setdefault(str(row),{})[str(col)]=button
for deck,home,ndi,cloud,more,cols in [('mk',1,37,38,39,5),('xl',21,40,41,42,8)]:
    for number,kind in [(ndi,'ndi'),(cloud,'cloud')]:
        page(number,f'{deck.upper()} | {kind.upper()} Quick 3 + 4',cols,3 if deck=='mk' else 4)
        put(number,0,0,button('HOME',deck=deck,page=home))
        put(number,0,1,button(variable(deck,'selection'),teal if kind=='ndi' else blue,deck))
        for col,dest in [(2,3),(3,4)]:put(number,0,col,button(variable(deck,f'destination_{dest}'),navy,deck,'destination',dest,group='destination'))
        put(number,0,4,button('MORE\nINPUTS 1–8',deck=deck,page=more))
        for i in range(5 if deck=='mk' else 16):put(number,1+i//cols,i%cols,button(variable(deck,f'source_{i}'),navy,deck,'source',i,group='source'))
        if deck=='mk':
            put(number,2,0,button('‹ PREV\n'+variable(deck,'page'),deck=deck,command='prev'))
            put(number,2,1,button('NEXT ›\n'+variable(deck,'page'),deck=deck,command='next'))
            put(number,2,2,button('RETURN\nTO SDI',red,deck,'sdi',page=more))
            put(number,2,3,button(variable(deck,'health'),deck=deck,page=43))
            put(number,2,4,button(variable(deck,'take'),gold,deck,'take'))
        else:
            put(number,0,5,button(variable(deck,'health'),deck=deck))
            put(number,0,6,button('RETURN\nTO SDI',red,deck,'sdi',page=more))
            put(number,0,7,button(variable(deck,'take'),gold,deck,'take'))
            put(number,3,0,button('‹ PREV\n'+variable(deck,'page'),deck=deck,command='prev'))
            put(number,3,1,button('NEXT ›\n'+variable(deck,'page'),deck=deck,command='next'))
            for col,key in enumerate(['video','audio','drops','status','destinations'],2):put(number,3,col,button(variable(deck,key),deck=deck))
            put(number,3,7,button('CLEAR',deck=deck,command='cancel'))
    page(more,f'{deck.upper()} | More Inputs',cols,3 if deck=='mk' else 4)
    put(more,0,0,button('‹ BACK',deck=deck,page=variable(deck,'source_page')))
    put(more,0,1,button(variable(deck,'selection'),deck=deck))
    put(more,0,2,button(variable(deck,'health'),deck=deck,page=43 if deck=='mk' else None))
    put(more,0,3,button('HOME',deck=deck,page=home))
    put(more,0,4,button('RETURN\nTO SDI',red,deck,'sdi'))
    for dest in range(1,9):
        row,col=(1+(dest-1)//5,(dest-1)%5) if deck=='mk' else (1,dest-1)
        put(more,row,col,button(variable(deck,f'destination_{dest}'),navy,deck,'destination',dest,group='destination'))
    if deck=='mk':
        put(more,2,3,button('CLEAR',deck=deck,command='cancel'));put(more,2,4,button(variable(deck,'take'),gold,deck,'take'))
    else:
        put(more,0,5,button(variable(deck,'destinations'),deck=deck));put(more,0,6,button('CLEAR',deck=deck,command='cancel'));put(more,0,7,button(variable(deck,'take'),gold,deck,'take'))
        for col,key in enumerate(['health','video','audio','drops','status','selection','destinations']):put(more,2,col,button(variable(deck,key),deck=deck))
# Optional Mk.2 details. BACK preserves the current source category.
page(43,'MK | Stream Details',8,4)
put(43,0,0,button('‹ BACK',page=variable('mk','source_page')))
put(43,0,1,button(variable('mk','selection')));put(43,0,2,button(variable('mk','destinations')))
put(43,0,3,button('MORE\nINPUTS',page=39));put(43,0,4,button('HOME',page=1))
for col,key in enumerate(['health','video','audio','drops','status']):put(43,1,col,button(variable('mk',key)))
put(43,2,0,button('RETURN\nTO SDI',red,'mk','sdi',page=39));put(43,2,4,button(variable('mk','take'),gold,'mk','take'))
# Home templates below the physical Mk.2 grid; installer copies only six cells.
for offset,deck,ndi,cloud,more in [(0,'mk',37,38,39),(3,'xl',40,41,42)]:
    put(43,3,offset,button(variable(deck,'home_ndi'),teal,deck,'ndi',page=ndi))
    put(43,3,offset+1,button(variable(deck,'home_cloud'),blue,deck,'cloud',page=cloud))
    put(43,3,offset+2,button('RETURN\nTO SDI\nALL INPUTS',red,deck,'sdi',page=more))
# Text-first buttons. Companion font size is a percentage of the text box's
# height, not points: normalize the requested size to the full button here.
# No icons, arrows, chips or camera artwork; the operator requested clear text.
def text_layer(identity,text,x,y,w,h,size=24,color=0xe8f1fc):
    layer=copy.deepcopy(next(l for l in base['style']['layers'] if l['id']=='label'));layer['id']=identity;layer['name']=identity
    for k,v in dict(text=text,x=x,y=y,width=w,height=h,fontsize=round(size*100/h),fontsizeAllowShrink=True,color=color).items():layer[k]=value(v)
    return layer

def decorate(b):
    layers=b['style']['layers'];bg=next(l for l in layers if l['id']=='bg');label=next(l for l in layers if l['id']=='label')
    bg['color']=value(0x0c1420);bg['borderColor']=value(0x314255);bg['cornerRadius']=value(8)
    layers[:]=[l for l in layers if l['id']!='accent']
    label['fontsize']=value(38);label['y']=value(5);label['height']=value(90)
    actions=b['steps']['0']['action_sets']['down'];cmd=next((a['options']['command']['value'] for a in actions if a['definitionId']=='router'),None)
    opts=next((a['options'] for a in actions if a['definitionId']=='router'),{})
    deck=opts.get('deck',value('mk'))['value'];index=opts.get('index',value(0))['value']
    for f in b['feedbacks']:
        f['styleOverrides']=[{'overrideId':uid(),'elementId':'bg','elementProperty':'borderColor','override':value(0x71e4c7)},{'overrideId':uid(),'elementId':'bg','elementProperty':'borderWidth','override':value(4)}]
    if cmd=='destination':
        layers.remove(label)
        layers.extend([text_layer('input',f'IN {index}',5,5,90,47,36),text_layer('feed',variable(deck,f'feed_{index}'),5,53,90,25,22,0xb6c7db)])
        air=text_layer('on-air','ON AIR',5,78,90,19,16,0xff6978);air['opacity']=value(0);layers.append(air)
        b['feedbacks'].append({'id':uid(),'type':'feedback','definitionId':'router_selected','connectionId':connection,'options':{k:value(v) for k,v in {'deck':deck,'group':'program','index':index}.items()},'upgradeIndex':-1,'isInverted':value(False),'styleOverrides':[{'overrideId':uid(),'elementId':'on-air','elementProperty':'opacity','override':value(100)}]})
    elif cmd in ('ndi','cloud'):
        layers.remove(label)
        color=0x88edda if cmd=='ndi' else 0xaac4ff
        layers.append(text_layer('title',cmd.upper(),5,3,90,43,35,color))
        for i,y in [(3,48),(4,73)]:layers.append(text_layer('status-'+str(i),str(i)+': '+variable(deck,f'home_short_{i}'),5,y,90,24,21))
    elif cmd in ('take','sdi'):
        bg['borderColor']=value(0xb88640 if cmd=='take' else 0xa5666c)
        label['color']=value(0xffd18c if cmd=='take' else 0xffc2c9)
        if cmd=='sdi':label['text']=value('RETURN\nSDI');label['fontsize']=value(43)
for p in pages.values():
    for row in p['controls'].values():
        for b in row.values():decorate(b)
bundle={'version':12,'type':'full','companionBuild':config['companionBuild'],'pages':pages,'instances':{connection:config['instances'][connection]}}
output.write_text(json.dumps(bundle,ensure_ascii=False,indent=2),encoding='utf-8')
print('Prepared seven owned router pages and six home templates. No application state changed.')

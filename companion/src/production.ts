import { randomUUID } from 'node:crypto'
import { performance } from 'node:perf_hooks'
import { HttpMedia, type MediaState, type ReadyOutput, type TransitionAudio, type Plan } from './coordinator.js'
import type { HubSnapshot, Router } from './videohub.js'

export type Source={kind:'ndi'|'cloud'|'sdi';id:string;name:string;endpoint?:string;connected?:boolean;linkedInput?:number}
export type AtemState={epoch:string;sources:{id:number;name:string;cloud:boolean;enabled:boolean;connected:boolean;linkedInput:number}[];inputs:{id:number;name:string;port:number;available:number;program:boolean;preview:boolean}[]}
export type ProductionMap={normalInputs:number[];outputs:{device:string;input:number}[]}
export type View={kind:'ndi'|'cloud';page:number;source?:Source;destinations:Set<number>}
export type DesktopRouteRequest={schemaVersion:number;routerInstanceId:string;slot:number;input:number;expectedInstanceId:string;expectedWorkerInstanceId:string;expectedRevision:number;settings?:Record<string,unknown>}
export const capacity=(deck:string):number=>deck==='xl'?16:5
export function visibleNdi(name:string):boolean {
  const base=name.replace(/\s*\((?:chan(?:nel)?)\s*\d+\)\s*$/i,'').replace(/[\s_-]/g,'').toLowerCase()
  return !/^(?:acam[1-3]|sbcam[1-4]|audcam[1-4]|sccam[1-3]|ecam[1-3])$/.test(base)
}
export function validateMap(raw:string):ProductionMap {
  const value=JSON.parse(raw) as ProductionMap
  if(value.normalInputs?.length!==8 || value.normalInputs.some(n=>!Number.isInteger(n)||n<1||n>40) || value.outputs?.length!==4 ||
    value.outputs.some(o=>!/^0x[0-9a-f]+$/i.test(o.device)||!Number.isInteger(o.input)||o.input<1||o.input>40) ||
    new Set(value.outputs.map(o=>o.device.toLowerCase())).size!==4 || new Set(value.outputs.map(o=>o.input)).size!==4)throw new Error('Commission eight normal Videohub inputs and four physical NDI connector mappings')
  return value
}
export function selectWorker(map:ProductionMap,state:MediaState,hub:HubSnapshot,source:Source,destinations:number[]):{output:ReadyOutput;input:number;reuse:boolean} {
  const selected=new Set(destinations.map(d=>d+32))
  const candidates=state.outputs.map(output=>({output,input:map.outputs.find(m=>m.device.toLowerCase()===(output.deviceId ?? output.desiredSettings?.device as string)?.toLowerCase())?.input ?? 0}))
  const same=candidates.find(c=>c.input && c.output.running && c.output.source===source.name && c.output.health==='healthy' && !c.output.fallback && c.output.videoAgeMs<500 && c.output.scheduleAgeMs<500 && c.output.settings?.sourceUrl===source.endpoint)
  if(same)return {...same,reuse:true}
  const free=candidates.find(c=>c.input && ![...hub.routes].some(([destination,input])=>input===c.input && !selected.has(destination)))
  if(!free)throw new Error('All four NDI outputs feed other destinations. Return unused destinations to SDI first, or reuse an already running source.')
  return {...free,reuse:false}
}
const expected=(output:ReadyOutput)=>({expectedWorkerInstanceId:output.workerInstanceId ?? '',expectedRevision:output.configRevision ?? 0})
const meter=(value:number,maximum:number):string=>{const fill=Math.max(0,Math.min(6,Math.round(value/maximum*6)));return '▰'.repeat(fill)+'▱'.repeat(6-fill)}
export class ProductionRouter {
  readonly instanceId=randomUUID()
  readonly views:Record<string,View>={mk:{kind:'ndi',page:0,destinations:new Set()},xl:{kind:'ndi',page:0,destinations:new Set()}}
  sources:Source[]=[];atem?:AtemState;hub?:HubSnapshot;media?:MediaState
  message='Choose a source, then destinations, then TAKE';private lastDiscovery=-Infinity;private at=0;private discovery?:Promise<void>
  private mediaClient:HttpMedia
  private sourceRequest?:Promise<{sources:{name:string;endpoint:string}[]}>
  constructor(private base:string,private atemHost:string,readonly map:ProductionMap,private router:Router,private plan?:Plan){this.mediaClient=new HttpMedia(base)}
  routingState():unknown {
    const fresh=performance.now()-this.at<3000 && !!this.hub && !!this.atem
    return {schemaVersion:1,instanceId:this.instanceId,ready:fresh,
      defaults:this.map.normalInputs.map((source,i)=>({input:i+1,destination:i+33,source,current:fresh?this.hub!.routes.get(i+33):null})),
      connectors:this.map.outputs}
  }
  private checkDesktop(instanceId:string,inputs:number[]):void {
    if(instanceId!==this.instanceId)throw new Error('Routing connection or configuration changed; refresh before applying')
    if(!inputs.length || inputs.length>8 || new Set(inputs).size!==inputs.length || inputs.some(i=>!Number.isInteger(i)||i<1||i>8))throw new Error('Select ATEM inputs 1–8')
  }
  async returnDefaults(instanceId:string,inputs:number[]):Promise<void>{
    this.checkDesktop(instanceId,inputs)
    await this.apply({kind:'sdi',id:'sdi',name:'Default SDI'},inputs)
    this.message=`Default SDI restored to ATEM ${inputs.join(', ')}; receivers keep running`
  }
  // The desktop uses the same module operation lock as TAKE and failover.
  // Prepare on the chosen physical slot; never allocate or substitute a slot.
  async routeOutput(request:DesktopRouteRequest):Promise<void>{
    this.checkDesktop(request.routerInstanceId,[request.input])
    if(request.schemaVersion!==1 || !Number.isInteger(request.slot) || request.slot<1 || request.slot>4 ||
      typeof request.expectedInstanceId!=='string' || !request.expectedInstanceId || typeof request.expectedWorkerInstanceId!=='string' ||
      !Number.isSafeInteger(request.expectedRevision) || request.expectedRevision<0)throw new Error('Invalid output routing request')
    const state=await this.mediaClient.state(),initial=state.outputs.find(o=>o.slot===request.slot)
    if(!initial || state.instanceId!==request.expectedInstanceId || (initial.workerInstanceId ?? '')!==request.expectedWorkerInstanceId ||
      (initial.configRevision ?? 0)!==request.expectedRevision)throw new Error('Output changed before routing; refresh and select again')
    const settings=request.settings
    if(settings && (typeof settings!=='object' || Array.isArray(settings) || typeof settings.source!=='string' || !settings.source.trim() || typeof settings.device!=='string'))throw new Error('Complete output settings required')
    const device=String(settings?.device ?? initial.deviceId ?? initial.desiredSettings?.device ?? '').toLowerCase()
    const physical=this.map.outputs.find(o=>o.device.toLowerCase()===device)
    if(!physical)throw new Error('Selected DeckLink connector has no commissioned Videohub input mapping; output was not changed')
    let hub=await this.router.snapshot(),atem=await this.atemCall()
    const before=hub,destination=request.input+32
    if(hub.locks.get(destination)!=='U' || !hub.routes.has(destination) || physical.input>hub.inputs)throw new Error('Videohub destination is unavailable or locked')
    if(!atem.inputs.some(i=>i.id===request.input))throw new Error('ATEM destination unavailable')
    if(settings){
      const current={...initial.settings,device:initial.deviceId ?? initial.desiredSettings?.device}
      const changed=Object.entries(settings).some(([key,value])=>JSON.stringify(current[key as keyof typeof current])!==JSON.stringify(value))
      const previous=this.map.outputs.find(o=>o.device.toLowerCase()===(initial.deviceId ?? '').toLowerCase())?.input
      if(changed && [...hub.routes].some(([d,s])=>d!==destination && (s===physical.input || s===previous)))throw new Error('This output feeds another destination. Return that destination to default before changing its source.')
    }
    let operation:string|undefined
    if(settings){
      operation=randomUUID()
      await this.api(`outputs/${request.slot}/settings`,{schemaVersion:1,slot:request.slot,expectedInstanceId:state.instanceId,...expected(initial),settings},'PUT',3000,operation)
      for(let attempt=0;;attempt++){
        const result=await this.mediaClient.operation(operation)
        if(result.instanceId!==state.instanceId)throw new Error('Engine replaced during preparation; no Videohub routing')
        if(result.state==='committed')break
        if(['failed','unknown'].includes(result.state))throw new Error(result.error ?? result.state)
        if(attempt===399)throw new Error('Source preparation timed out; no Videohub routing. Check the output before retrying.')
        await new Promise(resolve=>setTimeout(resolve,250))
      }
    }
    const confirmed=await this.mediaClient.state(),output=confirmed.outputs.find(o=>o.slot===request.slot)
    const source=String(settings?.source ?? initial.source),sourceUrl=settings?.sourceUrl ?? initial.settings?.sourceUrl
    const settingsMatch=(value:ReadyOutput)=>!settings || Object.entries(settings).every(([key,target])=>key==='device' ? String(target).toLowerCase()===value.deviceId.toLowerCase() : JSON.stringify(value.settings?.[key])===JSON.stringify(target))
    const healthy=(value:ReadyOutput|undefined)=>value?.backend==='hardware' && value.running && value.health==='healthy' && !value.fallback && value.format==='1920x1080p30000/1001' &&
      Number.isFinite(value.videoAgeMs) && value.videoAgeMs>=0 && value.videoAgeMs<500 && Number.isFinite(value.scheduleAgeMs) && value.scheduleAgeMs>=0 && value.scheduleAgeMs<500 &&
      value.deviceId?.toLowerCase()===device && value.source===source && value.settings?.sourceUrl===sourceUrl &&
      Number.isSafeInteger(value.configRevision) && value.configRevision>0 && !!value.committedOperationId && !!value.workerInstanceId && settingsMatch(value)
    if(confirmed.instanceId!==state.instanceId || !healthy(output) ||
      (!operation && (output!.workerInstanceId!==initial.workerInstanceId || output!.configRevision!==initial.configRevision)))throw new Error('Selected source is not confirmed healthy; Videohub routes preserved')
    hub=await this.router.snapshot()
    if(hub.epoch!==before.epoch || [...before.routes].some(([d,s])=>hub.routes.get(d)!==s || hub.changes.get(d)!==before.changes.get(d)))throw new Error('Videohub changed during preparation; routes preserved')
    const fresh=await this.mediaClient.state(),actual=fresh.outputs.find(o=>o.slot===request.slot)
    if(fresh.instanceId!==confirmed.instanceId || !healthy(actual) || actual!.workerInstanceId!==output!.workerInstanceId || actual!.configRevision!==output!.configRevision)throw new Error('Output changed before TAKE; routes preserved')
    await this.router.route(destination,physical.input,before.routes.get(destination)!,hub.epoch)
    atem=await this.atemCall({method:'input',input:request.input,source:0,expected:atem})
    const final=await this.router.snapshot()
    if(final.epoch!==hub.epoch || final.routes.get(destination)!==physical.input)throw new Error('Videohub route changed after TAKE; outcome unknown, no automatic retry')
    this.hub=final;this.atem=atem;this.media=fresh;this.at=performance.now()
    this.message=`Confirmed: ${source} → ATEM ${request.input} (Videohub ${physical.input} → ${destination})`
  }
  outputMappings():unknown[]{return (this.media?.outputs ?? []).map(output=>{
    const device=output.deviceId ?? output.desiredSettings?.device as string
    const input=this.map.outputs.find(m=>m.device.toLowerCase()===device?.toLowerCase())?.input
    const path=this.plan?.paths.find(p=>p.backupInput===input)
    return {slot:output.slot,atemInput:path?path.destination-32:0,hubInput:input,device,
      destinations:performance.now()-this.at<3000 && this.hub ? Array.from({length:8},(_,i)=>i+1).filter(i=>this.hub!.routes.get(i+32)===input && !this.atem?.sources.some(s=>s.linkedInput===i)):null}
  })}
  private async api(path:string,body?:unknown,method=body?'POST':'GET',timeout=8000,id?:string):Promise<any>{
    const response=await fetch(new URL('/api/v1/'+path,this.base),{method,headers:{'Content-Type':'application/json',...(id?{'Idempotency-Key':id}:{})},body:body?JSON.stringify(body):undefined,signal:AbortSignal.timeout(timeout),cache:'no-store'})
    const data=await response.json() as any;if(!response.ok)throw new Error(data.error ?? `Engine HTTP ${response.status}`);return data
  }
  private async atemCall(body:Record<string,unknown>={}):Promise<AtemState>{return this.api('atem',{host:this.atemHost,method:'snapshot',...body})}
  private discover():Promise<{sources:{name:string;endpoint:string}[]}>{
    // Share this module's background scan with TAKE. Older engines reject a
    // simultaneous GUI scan, so retry only that read-only busy response.
    if(this.sourceRequest)return this.sourceRequest
    this.sourceRequest=(async()=>{
      const deadline=performance.now()+20000
      for(;;){
        try{return await this.api('sources',undefined,'GET',15000)}
        catch(error){if(!(error instanceof Error)||error.message!=='Source discovery is already running'||performance.now()>=deadline)throw error;await new Promise(r=>setTimeout(r,250))}
      }
    })().finally(()=>{this.sourceRequest=undefined})
    return this.sourceRequest
  }
  async refresh(hub?:HubSnapshot,media?:MediaState):Promise<void>{
    this.hub=hub;this.media=media;this.at=performance.now()
    try {this.atem=await this.atemCall();const cloud=this.atem.sources.filter(s=>s.cloud && s.enabled && s.name).map(s=>({kind:'cloud' as const,id:String(s.id),name:s.name,connected:s.connected,linkedInput:s.linkedInput}));this.sources=[...this.sources.filter(s=>s.kind==='ndi'),...cloud]}
    catch {this.atem=undefined}
    if(!this.discovery && performance.now()-this.lastDiscovery>12000){this.lastDiscovery=performance.now();this.discovery=(async()=>{
      try{const result=await this.discover();this.sources=[...result.sources.filter(s=>visibleNdi(s.name)).map(s=>({kind:'ndi' as const,id:s.name,name:s.name,endpoint:s.endpoint})),...this.sources.filter(s=>s.kind==='cloud')]}
      catch{/* Keep names selectable, but TAKE independently revalidates discovery. */}finally{this.discovery=undefined}
    })()}
  }
  list(deck:string):Source[]{const view=this.views[deck];return this.sources.filter(s=>s.kind===view.kind).sort((a,b)=>a.name.localeCompare(b.name)).slice(view.page*capacity(deck),(view.page+1)*capacity(deck))}
  edit(deck:string,command:string,index=0):void {
    const view=this.views[deck];if(!view)throw new Error('Unknown deck')
    if(command==='ndi'||command==='cloud'){view.kind=command;view.page=0;view.source=undefined;view.destinations.clear();this.message='Choose a source, Input 3 or 4, then TAKE'}
    else if(command==='next'){const count=this.sources.filter(s=>s.kind===view.kind).length;view.page=(view.page+1)%Math.max(1,Math.ceil(count/capacity(deck)))}
    else if(command==='prev')view.page=Math.max(0,view.page-1)
    else if(command==='source'){const source=this.list(deck)[index];if(!source)throw new Error('No source in this cell');view.source={...source};view.destinations.clear();this.message='Select ATEM destinations, then TAKE'}
    else if(command==='destination'){if(index<1||index>8)throw new Error('Invalid destination');if(view.destinations.has(index))view.destinations.delete(index);else{if(view.source?.kind==='cloud')view.destinations.clear();view.destinations.add(index)}}
    else if(command==='sdi'){view.source={kind:'sdi',id:'sdi',name:'Normal SDI'};view.destinations=new Set([1,2,3,4,5,6,7,8]);this.message='RETURN SDI staged for all eight inputs. Adjust destinations, then TAKE.'}
    else if(command==='cancel'){view.source=undefined;view.destinations.clear();this.message='Selection cleared; live routes unchanged'}
  }
  selected(deck:string,group:string,index:number):boolean{const view=this.views[deck];if(group==='program')return this.atem?.inputs.find(i=>i.id===index)?.program ?? false;return group==='destination'?view.destinations.has(index):view.source?.id===this.list(deck)[index]?.id && view.source?.kind===view.kind}
  values(deck:string):Record<string,string>{
    const view=this.views[deck],list=this.list(deck),fresh=performance.now()-this.at<3000
    const values:Record<string,string>={};const set=(key:string,text:string)=>values[`${deck}_${key}`]=text
    for(let i=0;i<capacity(deck);i++)set(`source_${i}`,list[i]?list[i].name.replace(/\s*\((?:chan(?:nel)?)\s*\d+\)\s*$/i,'').replace('BIG Stream Router - ',''):'—')
    const selected=[...view.destinations].sort((a,b)=>a-b)
    set('selection',view.source?.name ?? '1 · SELECT SOURCE');set('destinations',selected.length?`ATEM ${selected.join(' · ')}`:'2 · SELECT INPUTS')
    set('page',`${view.kind.toUpperCase()}\n${view.page+1} / ${Math.max(1,Math.ceil(this.sources.filter(s=>s.kind===view.kind).length/capacity(deck)))}`)
    set('source_page',String(deck==='xl'?(view.kind==='ndi'?40:41):(view.kind==='ndi'?37:38)))
    set('take',view.source&&selected.length?`TAKE\n${view.source.kind.toUpperCase()} → ${selected.join(',')}`:'TAKE\nSELECT FIRST')
    set('status',this.message.slice(0,180));set('health','NOT RECEIVING');set('video','VIDEO\n—');set('audio','AUDIO\n—');set('drops','DROPS\n—')
    for(let id=1;id<=8;id++){
      const input=this.atem?.inputs.find(i=>i.id===id),remote=this.atem?.sources.find(s=>s.linkedInput===id)
      const vh=this.hub?.routes.get(id+32);const physical=this.map.outputs.find(o=>o.input===vh);const output=this.media?.outputs.find(o=>o.deviceId===physical?.device)
      const label=!fresh||!this.hub?'UNKNOWN':remote?`CLOUD ${remote.id-4}`:vh===this.map.normalInputs[id-1]?'SDI':output?.running?`NDI ${output.slot}`:`VH ${vh}`
      set(`destination_${id}`,`${view.destinations.has(id)?'✓ ':''}IN ${id}\n${label}${input?.program?'\nON AIR':input?.preview?'\nPREVIEW':''}`)
      set(`feed_${id}`,label);set(`pick_${id}`,view.destinations.has(id)?'✓':'')
    }
    const brief=(id:number):string=>{
      if(!fresh||!this.hub)return `${id}: OFFLINE`
      const cloud=this.atem?.sources.find(s=>s.linkedInput===id)
      if(cloud)return `${id}: CLOUD ${cloud.id-4}${cloud.connected?'':' ?'}`
      const input=this.hub.routes.get(id+32),physical=this.map.outputs.find(o=>o.input===input)
      const output=this.media?.outputs.find(o=>o.deviceId===physical?.device && o.running)
      if(output)return `${id}: ${output.source.replace(/\s*\(.*\)$/,'').slice(0,18)}`
      return `${id}: ${input===this.map.normalInputs[id-1]?'SDI':physical?'NDI IDLE':`VH ${input}`}`
    }
    for(const kind of ['ndi','cloud'])set(`home_${kind}`,`${kind.toUpperCase()} SOURCES\n${brief(3)}\n${brief(4)}`)
    for(const id of [3,4])set(`home_short_${id}`,brief(id).replace(/^\d: /,'').replace('Panasonic AG-2','AG-2').replace('Panasonic','PANA').replace('AUD CAM ','AUD ').slice(0,9))
    if(fresh && view.source?.kind==='cloud'){
      const source=this.atem?.sources.find(s=>String(s.id)===view.source?.id)
      set('health',!source?'CLOUD\nUNAVAILABLE':source.connected?'CLOUD\nCONNECTED':source.linkedInput?'CLOUD\nCONNECTING':'CLOUD\nNOT LINKED')
      set('video',source?.linkedInput?`LINKED\nATEM ${source.linkedInput}`:'LINKED\n—');set('drops','BITRATE\nNOT EXPOSED');set('audio','LATENCY\nNOT EXPOSED')
    } else if(fresh && view.source?.kind==='ndi'){
      const output=this.media?.outputs.find(o=>o.running && o.source===view.source!.name) as any
      if(output){set('health',`${output.health==='healthy'?'● HEALTHY':'! '+output.health.toUpperCase()}\nNDI ${output.slot}`);set('video',`FRAME AGE\n${output.videoAgeMs} ms\n${meter(Math.max(0,500-output.videoAgeMs),500)}`)
        set('drops',`SDI DROPS\n${output.counters?.dropped ?? '—'}\nLATE ${output.counters?.late ?? '—'}`)
        const peak=Math.max(...(output.audio?.peak ?? [0]));set('audio',output.audio?.mode==='silent'?'AUDIO\nSILENT':`AUDIO\n${output.audio?.ageMs ?? '—'} ms\n${meter(Number.isFinite(peak)?peak:0,1)}`)}
    }
    if(this.message.startsWith('CHECK:')){set('health','TAKE FAILED\nVIEW STATUS');set('take','RETRY TAKE\nVIEW STATUS')}
    if(this.message.startsWith('WORKING')){set('health','PREPARING\nPLEASE WAIT');set('take','WORKING\nPLEASE WAIT')}
    return values
  }
  async take(deck:string):Promise<void>{const view=this.views[deck];if(!view?.source || !view.destinations.size)throw new Error('Select source and destinations first');await this.apply({...view.source},[...view.destinations].sort((a,b)=>a-b));this.message=`Confirmed: ${view.source.name} → ATEM ${[...view.destinations].join(', ')}. Physical signal unmeasured.`}
  async returnAll():Promise<void>{await this.apply({kind:'sdi',id:'sdi',name:'Normal SDI'},[1,2,3,4,5,6,7,8]);this.message='All eight inputs returned to normal SDI; receivers remain available until stopped'}
  emergencyAudio(audio:TransitionAudio,inputs:number[]):TransitionAudio {
    const change=async(state:AtemState,links:{id:number;linkedInput:number}[])=>{
      for(const input of inputs)if(state.sources.some(s=>s.linkedInput===input))state=await this.atemCall({method:'input',input,source:0,expected:state})
      for(const link of links)state=await this.atemCall({method:'input',input:link.linkedInput,source:link.id,expected:state})
    }
    return {
      prepare:async target=>{const state=await this.atemCall(),finish=await audio.prepare(target);return async()=>{await change(state,[]);return await finish()}},
      captureUndo:async()=>{
        const saved=await this.atemCall(),links=saved.sources.filter(s=>inputs.includes(s.linkedInput)).map(s=>({id:s.id,linkedInput:s.linkedInput}));const savedAudio=await audio.captureUndo?.()
        return async()=>{const state=await this.atemCall(),finish=await savedAudio?.();return async()=>{await change(state,links);return finish?await finish():'Prior ATEM input links restored'}}
      },
    }
  }
  private async apply(source:Source,destinations:number[]):Promise<void>{
    if(source.kind==='cloud' && destinations.length!==1)throw new Error('Each configured cloud output links to one ATEM input; choose one destination')
    let hub=await this.router.snapshot(),atem=await this.atemCall();const original=new Map(hub.routes)
    for(const id of destinations){if(hub.locks.get(id+32)!=='U')throw new Error(`Videohub output ${id+32} is locked`);if(!atem.inputs.some(i=>i.id===id))throw new Error(`ATEM input ${id} unavailable`)}
    let videoInput=0
    if(source.kind==='ndi'){
      const found=await this.discover();const sender=found.sources.find(s=>s.name===source.name && s.endpoint===source.endpoint)
      if(!sender)throw new Error('Selected NDI sender changed or disappeared; refresh and select again')
      const state=await this.mediaClient.state(),candidate=selectWorker(this.map,state,hub,source,destinations);videoInput=candidate.input
      if(!candidate.reuse){
        const settings={...candidate.output.desiredSettings,...candidate.output.settings,device:candidate.output.deviceId ?? candidate.output.desiredSettings?.device,source:source.name,sourceUrl:source.endpoint,audioMode:'ndi',audioEndpoint:'',audioKey:'',muted:false,gainDb:0,delayMs:0,left:1,right:2,bandwidth:'highest'}
        const id=randomUUID();await this.api(`outputs/${candidate.output.slot}/settings`,{schemaVersion:1,slot:candidate.output.slot,expectedInstanceId:state.instanceId,...expected(candidate.output),settings},'PUT',2000,id)
        for(let i=0;i<160;i++){const op=await this.mediaClient.operation(id);if(op.instanceId!==state.instanceId)throw new Error('Engine replaced during preparation; no routing');if(op.state==='committed')break;if(['failed','unknown'].includes(op.state))throw new Error(op.error ?? op.state);if(i===159)throw new Error('NDI preparation not confirmed; no routing');await new Promise(r=>setTimeout(r,250))}
      }
      const fresh=await this.mediaClient.state(),out=fresh.outputs.find(o=>o.slot===candidate.output.slot)
      if(fresh.instanceId!==state.instanceId || !out?.running || out.source!==source.name || out.health!=='healthy'||out.fallback||out.videoAgeMs>=500||out.scheduleAgeMs>=500)throw new Error('NDI video not ready; routes preserved')
      hub=await this.router.snapshot();if([...original].some(([d,s])=>hub.routes.get(d)!==s))throw new Error('Videohub changed during preparation; no routing')
    }
    for(const id of destinations){
      if(source.kind!=='cloud'){
        hub=await this.router.snapshot();for(const [d,s] of original)if(hub.routes.get(d)!==s)throw new Error('Videohub changed during TAKE; remaining routes preserved')
        const target=source.kind==='ndi'?videoInput:this.map.normalInputs[id-1];await this.router.route(id+32,target,original.get(id+32)!,hub.epoch);original.set(id+32,target)
      }
      // SDK mapping is compared to the previous snapshot immediately before each
      // link/unlink; it never selects program/preview or edits ATEM macros.
      atem=await this.atemCall({method:'input',input:id,source:source.kind==='cloud'?Number(source.id):0,expected:atem})
    }
    const final=await this.router.snapshot()
    if(final.epoch!==hub.epoch || (source.kind!=='cloud' && destinations.some(id=>final.routes.get(id+32)!==(source.kind==='ndi'?videoInput:this.map.normalInputs[id-1]))))throw new Error('Videohub final route readback differs; outcome unknown, no automatic retry')
    this.hub=final;this.atem=atem;this.media=await this.mediaClient.state();this.at=performance.now()
  }
}

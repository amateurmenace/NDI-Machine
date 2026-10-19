import { InstanceBase, InstanceStatus, type SomeCompanionConfigField } from '@companion-module/base'
import { Coordinator, HttpMedia, needsRestore, readiness, routeMode, toggleReadiness, validatePlan, type Plan, type RouteMode } from './coordinator.js'
import { Videohub } from './videohub.js'
import { performance } from 'node:perf_hooks'
import { AtemAudio, CompanionZoom, EmergencyAudio, networkAudioInput } from './audio.js'
import { HoldGesture } from './hold.js'
import { LocalLauncher, primeOnly } from './prime.js'
import { ProductionRouter, capacity, validateMap } from './production.js'
import { createServer, type Server } from 'node:http'

type Config = {host: string; apiUrl: string; plan: string; atemHost: string; companionUrl: string; zoomNetwork: string; zoomFiber: string; productionMap:string}
type Empty = Record<string, never>
type Schema = {
  config: Config; secrets: undefined;
  actions: {router:{options:{command:string;index:number;deck:string}};toggle: {options: Empty}; toggle_down: {options: Empty}; toggle_up: {options: Empty}; undo: {options: Empty}; take_all: {options: Empty}; return_normal: {options: Empty}; prepare: {options: Empty}; check: {options: Empty}};
  feedbacks: {blocked: {type: 'boolean'; options: Empty}; ready: {type: 'boolean'; options: Empty};
    active: {type: 'boolean'; options: Empty}; working: {type: 'boolean'; options: Empty};router_selected:{type:'boolean';options:{deck:string;group:string;index:number}}};
  variables: Record<string,string>
}
export const UpgradeScripts = []
export default class ModuleInstance extends InstanceBase<Schema> {
  private hub?: Videohub
  private media?: HttpMedia
  private coordinator?: Coordinator
  private atemAudio?: AtemAudio
  private audio?: EmergencyAudio
  private plan?: Plan
  private timer?: NodeJS.Timeout
  private pollTask?: Promise<void>
  private performing = false
  private ready = false
  private engineReady = false
  private productionConfigured = false
  private routesConfirmed = false
  private routeMode: RouteMode | 'UNKNOWN' = 'UNKNOWN'
  private observedAt = 0
  private reason = 'Not configured'
  private lastResult = ''
  private outcomeReason = ''
  private operation = ''
  private gesture = new HoldGesture()
  private launcher=new LocalLauncher()
  private needsPrime=true
  private priming=false
  private production?:ProductionRouter
  private productionServer?:Server
  async init(config: Config): Promise<void> {
    this.setVariableDefinitions({
      ...Object.fromEntries(['mk','xl'].flatMap(deck=>[...Array.from({length:capacity(deck)},(_,i)=>`source_${i}`),...Array.from({length:8},(_,i)=>`destination_${i+1}`),...Array.from({length:8},(_,i)=>'feed_'+(i+1)),...Array.from({length:8},(_,i)=>'pick_'+(i+1)),'selection','destinations','page','source_page','home_ndi','home_cloud','home_short_3','home_short_4','take','status','health','video','audio','drops'].map(key=>[`${deck}_${key}`,{name:`${deck.toUpperCase()} router ${key}`}]))),
      status: {name: 'Emergency state'}, reason: {name: 'Readiness block or last result'},
      engine_ready: {name: 'Fresh backup engine readiness'}, routes: {name: 'Videohub crosspoint readback'},
      signal: {name: 'Independently verified downstream SDI signal'}, operation: {name: 'Last operation ID'},
      route_mode: {name: 'Actual route group: FIBER, NETWORK, MIXED or UNKNOWN'}, next_action: {name: 'Next guarded toggle action'},
      audio_profile: {name: 'Observed ATEM input audio profile (Zoom dispatch is separate)'},
      undo_available: {name: 'Hold three seconds and release to restore previous change'},
      button_text: {name:'Failover label including click-to-prime state'},
      network_audio_input: {name:'ATEM input receiving the commissioned audio-bearing NDI output'},
    })
    this.setActionDefinitions({
      router:{name:'Production router: select / TAKE / Return SDI',options:[
        {type:'textinput',id:'deck',label:'Deck: mk or xl',default:'mk'},
        {type:'textinput',id:'command',label:'Command: ndi, cloud, source, destination, next, prev, sdi, cancel, take',default:'source'},
        {type:'number',id:'index',label:'Source cell (0 based) or ATEM input (1–8)',default:0,min:0,max:16},
      ],callback:async event=>{const {deck,command,index}=event.options;if(!this.production)return;if(command==='take')await this.productionCommand(()=>this.production!.take(deck));else if(!this.performing){try{this.production.edit(deck,command,index)}catch(error){this.production.message=String(error)}this.publishProduction()}}},
      toggle: {name: 'FAIL-OVER TOGGLE: Fiber / Network (verified routes)', options: [], callback: async () => this.execute('toggle')},
      toggle_down: {name: 'FAIL-OVER: Button down (start hold)', options: [], callback: event => {
        if (!this.performing) this.gesture.down(`${event.controlId}/${event.surfaceId ?? 'remote'}`,this.needsPrime)
        this.publish()
      }},
      toggle_up: {name: 'FAIL-OVER: Button up (short = toggle, hold 3s = undo)', options: [], callback: async event => {
        const command = this.gesture.up(`${event.controlId}/${event.surfaceId ?? 'remote'}`)
        if (command) await this.execute(command)
        this.publish()
      }},
      undo: {name: 'Revert the last video/audio change (explicit)', options: [], callback: async () => this.execute('undo')},
      take_all: {name: 'FAIL-OVER: Guarded take all configured backups', options: [], callback: async () => this.execute('take')},
      return_normal: {name: 'Return Normal: verified paths only', options: [], callback: async () => this.execute('return')},
      prepare: {name: 'Prepare backups (no Videohub routing)', options: [], callback: async () => {
        if (!this.media || !this.plan || this.performing) return
        try { this.operation = await this.media.prepare(this.plan); this.reason = 'Preparation accepted; waiting for committed readiness' }
        catch (error) { this.reason = `Prepare blocked: ${String(error)}` }
        this.publish()
      }},
      check: {name: 'Check readiness and routes (read only)', options: [], callback: async () => { await this.refresh() }},
    })
    this.setFeedbackDefinitions({
      router_selected:{name:'Production router selection',type:'boolean',options:[{type:'textinput',id:'deck',label:'Deck',default:'mk'},{type:'textinput',id:'group',label:'source or destination',default:'source'},{type:'number',id:'index',label:'Index',default:0,min:0,max:16}],defaultStyle:{bgcolor:0x087f8c,color:0xffffff},callback:event=>this.production?.selected(event.options.deck,event.options.group,event.options.index) ?? false},
      blocked: {name: 'Emergency blocked or state stale', type: 'boolean', options: [], defaultStyle: {bgcolor: 0x7d1010, color: 0xffffff}, callback: () => !this.needsPrime && (!this.fresh() || !this.ready || this.unresolved())},
      ready: {name: 'Network take ready (restores emergency outputs and overwrites routes)', type: 'boolean', options: [], defaultStyle: {bgcolor: 0xc27a00, color: 0xffffff}, callback: () => this.fresh() && this.ready && (this.routeMode !== 'NETWORK' || this.productionConfigured)},
      active: {name: 'Network routes confirmed; return to fiber available (signal Unknown)', type: 'boolean', options: [], defaultStyle: {bgcolor: 0x176434, color: 0xffffff}, callback: () => this.fresh() && this.ready && this.routesConfirmed && !this.unresolved()},
      working: {name: 'Priming needed or operation in progress', type: 'boolean', options: [], defaultStyle: {bgcolor: 0x234b88, color: 0xffffff}, callback: () => this.performing || this.needsPrime},
    })
    this.setPresetDefinitions([{id: 'emergency', name: 'Emergency routing', definitions: ['take', 'prepare', 'return', 'check']}], {
      take: {type: 'simple', name: 'FAIL-OVER TOGGLE (Fiber / Network)', style: {text: '$(self:button_text)', size: 'auto', color: 0xffffff, bgcolor: 0x7d1010},
        steps: [{down: [{actionId: 'toggle_down', options: {}}], up: [{actionId: 'toggle_up', options: {}}]}],
        feedbacks: [{feedbackId: 'ready', options: {}, style: {bgcolor: 0xc27a00}}, {feedbackId: 'active', options: {}, style: {bgcolor: 0x176434}}, {feedbackId: 'blocked', options: {}, style: {bgcolor: 0x7d1010}}, {feedbackId: 'working', options: {}, style: {bgcolor: 0x234b88}}]},
      prepare: {type: 'simple', name: 'Prepare Backups', style: {text: 'PREPARE\nBACKUPS', size: 'auto', color: 0xffffff, bgcolor: 0x234b88}, steps: [{down: [{actionId: 'prepare', options: {}}], up: []}], feedbacks: []},
      return: {type: 'simple', name: 'Return Normal', style: {text: 'RETURN\nNORMAL', size: 'auto', color: 0xffffff, bgcolor: 0x234b88}, steps: [{down: [{actionId: 'return_normal', options: {}}], up: []}], feedbacks: []},
      check: {type: 'simple', name: 'Read-only readiness check', style: {text: 'CHECK\nREADINESS', size: 'auto', color: 0xffffff, bgcolor: 0x444444}, steps: [{down: [{actionId: 'check', options: {}}], up: []}], feedbacks: []},
    })
    await this.configUpdated(config)
  }
  getConfigFields(): SomeCompanionConfigField[] {
    return [
      {type: 'textinput', id: 'host', label: 'Commissioned Videohub IP address', width: 6, default: ''},
      {type: 'textinput', id: 'apiUrl', label: 'Media control API (localhost)', width: 6, default: 'http://127.0.0.1:9860'},
      {type: 'textinput', id: 'plan', label: 'Commissioned emergency plan JSON (one-based physical ports)', width: 12, multiline: true, default: ''},
      {type: 'textinput', id: 'atemHost', label: 'Commissioned ATEM HD8 ISO IP (required for audio)', width: 6, default: ''},
      {type: 'textinput', id: 'companionUrl', label: 'Companion button API (localhost)', width: 6, default: 'http://127.0.0.1:8000'},
      {type: 'textinput', id: 'zoomNetwork', label: 'ZOOM DANTE button (page/row/column)', width: 6, default: '1/1/2'},
      {type: 'textinput', id: 'zoomFiber', label: 'ZOOM MADI button (page/row/column)', width: 6, default: '1/1/1'},
      {type:'textinput',id:'productionMap',label:'Production physical connector map JSON',width:12,multiline:true,default:'',tooltip:'normalInputs: eight normal Videohub inputs; outputs: four {device,input} physical DeckLink → Videohub mappings. Does not use emergency slot order.'},
      {type: 'static-text', id: 'info', label: 'Operation', width: 12,
        value: 'After confirmed video routes: Network presses ZOOM DANTE and enables networkAudioInput from the commissioned plan (or the fixed-audio path atemInput if no explicit destination is supplied). Exactly one path carries fixed audio; others must be silent. This audio destination does not alter video routes. Fiber presses ZOOM MADI and enables only XLR + MADI 1/2 + 3/4. Master/gains remain unchanged. No automatic routing or audio changes.'},
    ]
  }
  async configUpdated(config: Config): Promise<void> {
    await this.destroy()
    this.ready = false; this.engineReady = false; this.routesConfirmed = false; this.observedAt = 0; this.operation = ''; this.lastResult = ''; this.outcomeReason = ''
    this.routeMode = 'UNKNOWN'
    this.needsPrime=true
    this.setVariableValues({network_audio_input:'UNCOMMISSIONED'})
    try {
      if (!config.host?.trim()) throw new Error('Videohub address is required')
      this.plan = validatePlan(JSON.parse(config.plan))
      const networkInput=networkAudioInput(this.plan)
      this.media = new HttpMedia(config.apiUrl)
      const zoom = new CompanionZoom(config.companionUrl || 'http://127.0.0.1:8000', config.zoomNetwork || '1/1/2', config.zoomFiber || '1/1/1')
      this.atemAudio = new AtemAudio(config.atemHost, message => this.log('warn', message))
      this.audio = new EmergencyAudio(this.atemAudio, zoom,networkInput)
      this.setVariableValues({network_audio_input:String(networkInput)})
      this.hub = new Videohub(config.host.trim())
      this.coordinator = new Coordinator(this.plan, this.hub, this.media, this.audio)
      if(config.productionMap?.trim()){
        this.production=new ProductionRouter(config.apiUrl,config.atemHost,validateMap(config.productionMap),this.hub,this.plan)
        this.coordinator=new Coordinator(this.plan,this.hub,this.media,this.production.emergencyAudio(this.audio,this.plan.paths.map(p=>p.destination-32)))
        this.productionServer=createServer(async(request,response)=>{
          response.setHeader('Cache-Control','no-store');response.setHeader('Content-Type','application/json')
          if(!['127.0.0.1:9862','localhost:9862'].includes(request.headers.host ?? '') || (request.headers.origin && !['http://127.0.0.1:9862','http://localhost:9862'].includes(request.headers.origin))){response.writeHead(403).end();return}
          if(request.method==='POST' && request.url==='/api/v1/return-sdi'){
            try{await this.productionCommand(()=>this.production!.returnAll(),true);response.end(JSON.stringify({confirmed:true}))}catch(error){response.writeHead(409).end(JSON.stringify({error:String(error)}))}
          }else if(request.method==='GET' && request.url==='/api/v1/state')response.end(JSON.stringify({message:this.production?.message,busy:this.performing,outputMappings:this.production?.outputMappings(),values:{...this.production?.values('mk'),...this.production?.values('xl')}}))
          else response.writeHead(404).end()
        })
        this.productionServer.on('error',error=>this.log('warn',`Production app control unavailable: ${error.message}`))
        this.productionServer.listen(9862,'127.0.0.1')
      }
      this.reason = 'Awaiting fresh media and router state'
      this.timer = setInterval(() => { void this.refresh() }, 1000)
      await this.refresh()
    } catch (error) { this.reason = String(error); this.updateStatus(InstanceStatus.BadConfig, this.reason); this.publish() }
  }
  async destroy(): Promise<void> {
    this.productionServer?.close();this.productionServer=undefined;this.production=undefined
    this.gesture.reset()
    if (this.timer) clearInterval(this.timer)
    this.timer = undefined; this.hub?.close()
    await this.pollTask
    await this.atemAudio?.close()
    this.atemAudio = undefined; this.audio = undefined
    this.hub = undefined; this.media = undefined; this.plan = undefined; this.coordinator = undefined
  }
  private fresh(): boolean { return this.observedAt > 0 && performance.now() - this.observedAt < 2500 }
  private unresolved(): boolean { return ['PARTIAL', 'UNKNOWN'].includes(this.lastResult) }
  private publish(): void {
    this.publishProduction()
    const fresh = this.fresh()
    const status = this.priming ? 'PRIMING' : this.performing ? 'WORKING' : this.needsPrime ? 'PRIME' : !fresh ? 'BLOCKED' : this.unresolved() ? this.lastResult : this.routeMode === 'MIXED' ? 'MIXED' : !this.ready ? 'BLOCKED' : this.routeMode
    const next = this.performing ? 'PLEASE WAIT' : this.gesture.hint || (this.needsPrime ? 'CLICK TO PRIME' : !fresh || !this.ready ? 'BLOCKED' : this.productionConfigured ? 'RESTORE + TAKE NETWORK' : this.routeMode === 'NETWORK' ? 'RETURN FIBER' : 'TAKE NETWORK')
    this.setVariableValues({status, reason: (this.unresolved() ? `${this.outcomeReason}; current: ${this.reason}` : this.reason).slice(0, 500), engine_ready: fresh && this.engineReady ? 'READY' : 'NOT READY',
      button_text:this.priming ? 'PRIMING\nFAIL-OVER\nPLEASE WAIT' : this.needsPrime && this.unresolved() ? 'PRIME FAILED\nCHECK APP\nRETRY' : this.needsPrime && !this.performing ? 'CLICK TO\nPRIME\nFAIL-OVER' : `${status}\nFAIL-OVER\n${next}`,
      routes: fresh && ['FIBER', 'NETWORK'].includes(this.routeMode) ? `${this.routeMode} ROUTES CONFIRMED` : 'NOT CONFIRMED',
      signal: 'UNKNOWN', operation: this.operation, route_mode: fresh ? this.routeMode : 'UNKNOWN', next_action: next, audio_profile: this.audio?.profile() ?? 'UNKNOWN', undo_available: this.coordinator?.canUndo ? 'HOLD 3s + RELEASE' : 'NO PREVIOUS CHANGE'})
    this.checkFeedbacks('blocked', 'ready', 'active', 'working')
  }
  private publishProduction():void{if(this.production){this.setVariableValues({...this.production.values('mk'),...this.production.values('xl')});this.checkFeedbacks('router_selected')}}
  private async productionCommand(work:()=>Promise<void>,rethrow=false):Promise<void>{
    if(this.performing){if(rethrow)throw new Error('Another routing operation is running');return}
    this.performing=true;await this.pollTask;this.production!.message='WORKING · Preparing and verifying';this.publish()
    try{await work()}catch(error){this.production!.message=`CHECK: ${String(error)}. No automatic rollback.`;if(rethrow)throw error}
    finally{this.performing=false;this.publish()}
  }
  private refresh(): Promise<void> {
    if (this.performing || this.pollTask || !this.plan || !this.hub || !this.media) { this.publish(); return this.pollTask ?? Promise.resolve() }
    this.pollTask = (async () => {
      try {
        const [hub, media, launcher] = await Promise.allSettled([this.hub!.snapshot(), this.media!.state(),this.launcher.state()])
        await this.production?.refresh(hub.status==='fulfilled'?hub.value:undefined,media.status==='fulfilled'?media.value:undefined)
        this.needsPrime=launcher.status!=='fulfilled' || !launcher.value.desktopRunning || !launcher.value.controlRunning || media.status!=='fulfilled' || media.value.outputs.every(o=>!o.running)
        this.engineReady = media.status === 'fulfilled' && !readiness(this.plan!, media.value)
        this.productionConfigured=media.status==='fulfilled' && needsRestore(this.plan!,media.value)
        const engineReason = media.status === 'rejected' ? 'Media control API unavailable; cannot take network backups' : readiness(this.plan!, media.value)
        this.routeMode = hub.status === 'fulfilled' ? routeMode(this.plan!, hub.value) : 'UNKNOWN'
        let block = hub.status === 'fulfilled' ? toggleReadiness(this.plan!, hub.value, this.productionConfigured ? undefined : engineReason) : `Videohub unavailable: ${String(hub.reason)}`
        if (!block) {
          try { if (!this.audio) throw new Error('Fail-over audio is not configured'); await this.audio.check(this.routeMode === 'NETWORK' && !this.productionConfigured ? 'FIBER' : 'NETWORK') }
          catch (error) { block = String(error) }
        }
        this.ready = !block
        this.routesConfirmed = this.routeMode === 'NETWORK' && !this.productionConfigured
        this.reason = block ?? (this.productionConfigured ? 'Production outputs selected. Short press restores emergency NDI, DeckLink and audio settings, then takes Network. Hold 3s + release restores the previous production/video/audio snapshot.' : this.routesConfirmed ? `Network routes confirmed; short press returns to fiber${engineReason ? '; backup engines not ready' : ''}; physical signal Unknown` : 'Short press overwrites protected routes with network backups; physical signal Unknown')
        if(this.needsPrime)this.reason=launcher.status==='rejected' ? 'Click to prime; local launcher unavailable. Start NDI Machine Launcher if this persists.' : 'Click to prime: open NDI Machine, connect and prepare backups. This click does not change routes or ATEM/Zoom audio.'
        this.observedAt = performance.now()
        this.updateStatus(this.ready && hub.status === 'fulfilled' ? InstanceStatus.Ok : InstanceStatus.UnknownWarning, this.reason)
      } catch (error) { this.ready = false; this.engineReady = false; this.routesConfirmed = false; this.routeMode = 'UNKNOWN'; this.reason = String(error); this.observedAt = 0 }
      this.publish()
    })().finally(() => { this.pollTask = undefined })
    return this.pollTask
  }
  private async execute(command: 'take' | 'return' | 'toggle' | 'undo' | 'prime'): Promise<void> {
    if (this.performing || !this.coordinator) return
    if(command==='prime' || command==='toggle' && this.needsPrime){await this.prime();return}
    this.performing = true; this.lastResult = ''; this.outcomeReason = ''; this.publish()
    try {
      await this.pollTask
      if(command==='toggle') {
        let connected=false
        try{const launch=await this.launcher.state();connected=launch.desktopRunning && launch.controlRunning}catch{}
        if(!connected){await this.prime();return}
      }
      const result = command === 'undo' ? await this.coordinator.undo() : command === 'toggle' ? await this.coordinator.toggle() : await this.coordinator.take(command === 'return')
      this.operation = result.id; this.reason = result.reason; this.outcomeReason = result.reason; this.lastResult = result.state.toUpperCase()
      this.log(result.state === 'confirmed' ? 'info' : 'warn', `${command} to ${result.target ?? 'undetermined'} ${result.state}: ${result.reason}; confirmed slots ${result.confirmed.join(',') || 'none'}`)
    } finally { this.performing = false; this.publish(); await this.refresh() }
  }
  private async prime():Promise<void> {
    if(!this.media || !this.plan)return
    this.performing=true;this.priming=true;this.lastResult='';this.outcomeReason='';this.publish()
    try {
      await this.pollTask
      this.operation=await primeOnly(this.launcher,this.media,this.plan)
      let confirmed=false
      for(let attempt=0;attempt<400;attempt++) {
        const operation=await this.media.operation(this.operation)
        if(operation.state==='committed'){confirmed=true;break}
        if(['failed','unknown'].includes(operation.state))throw new Error(operation.error ?? operation.state)
        await new Promise(resolve=>setTimeout(resolve,500))
      }
      if(!confirmed)throw new Error('Preparation confirmation timed out; no routing or automatic replay')
      this.reason='App connected and backups prepared. Press again for the displayed Fiber/Network action.'
      this.log('info',this.reason)
    }catch(error){this.lastResult='UNKNOWN';this.outcomeReason=`Prime failed: ${String(error)}`;this.log('warn',this.outcomeReason)}
    finally{this.priming=false;this.performing=false;await this.refresh()}
  }
}

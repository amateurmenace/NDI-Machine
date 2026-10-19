import { randomUUID } from 'node:crypto'
import { performance } from 'node:perf_hooks'
import type { HubSnapshot, Router } from './videohub.js'

export type Path = {slot: number; destination: number; normalInput: number; backupInput: number; device: string; source: string; audioMode?: 'fixed' | 'silent'; atemInput?:number}
export type Plan = {schemaVersion: 1; name: string; audioEndpointId: string; paths: Path[]; networkAudioInput?:number}
export type ReadyOutput = {
  slot: number; deviceId: string; source: string; backend: string; running: boolean;
  configRevision: number; committedOperationId: string; videoAgeMs: number; scheduleAgeMs: number;
  fallback: boolean; format: string; audio: {endpointId: string; channels: number[]; mode: string; ageMs: number; muted: boolean};
  health: string; emergencyConfigured?:boolean; workerInstanceId?:string; settings?:Record<string,unknown>; desiredSettings?:Record<string,unknown>
}
export type MediaState = {schemaVersion: number; nonce: string; instanceId: string; outputs: ReadyOutput[]; capabilities?:string[];
  audioBinding?:{configuredEndpointId:string;resolvedEndpointId:string;available:boolean;message:string}}
export interface Media { state(): Promise<MediaState>; prepare(plan: Plan): Promise<string>; prepareReady?(plan:Plan,initial:MediaState):Promise<MediaState>; captureUndo?(initial:MediaState):Promise<()=>Promise<void>> }
export function needsRestore(plan:Plan,state:MediaState):boolean {
  return state.capabilities?.includes('emergency-restore')===true && plan.paths.some(path=>state.outputs.find(o=>o.slot===path.slot)?.emergencyConfigured!==true)
}
export function validatePlan(raw: unknown): Plan {
  const plan = raw as Plan
  if (!plan || plan.schemaVersion !== 1 || typeof plan.name !== 'string' || !plan.name ||
      typeof plan.audioEndpointId !== 'string' || !plan.audioEndpointId || !Array.isArray(plan.paths) || !plan.paths.length || plan.paths.length > 4)
    throw new Error('Commission a schemaVersion 1 plan with audioEndpointId and 1-4 paths')
  for (const key of ['slot', 'destination', 'backupInput'] as const)
    if (new Set(plan.paths.map(path => path[key])).size !== plan.paths.length) throw new Error(`Duplicate ${key}`)
  for (const path of plan.paths) {
    if (!Number.isSafeInteger(path.slot) || path.slot < 1 || path.slot > 4 ||
        !/^0x[0-9a-f]+$/i.test(path.device) || typeof path.source !== 'string' || !path.source ||
        [path.destination, path.normalInput, path.backupInput].some(port => !Number.isSafeInteger(port) || port < 1 || port > 4096) ||
        path.normalInput === path.backupInput || (path.audioMode !== undefined && !['fixed', 'silent'].includes(path.audioMode)) ||
        (path.atemInput!==undefined && (!Number.isSafeInteger(path.atemInput) || path.atemInput<1 || path.atemInput>8))) throw new Error('Invalid commissioned path')
  }
  const atemInputs=plan.paths.filter(p=>p.atemInput!==undefined).map(p=>p.atemInput)
  if(new Set(atemInputs).size!==atemInputs.length)throw new Error('Duplicate ATEM input')
  if(plan.networkAudioInput!==undefined && (!Number.isSafeInteger(plan.networkAudioInput) || plan.networkAudioInput<1 || plan.networkAudioInput>8))throw new Error('Invalid network ATEM audio input')
  if (new Set(plan.paths.map(path => path.device.toLowerCase())).size !== plan.paths.length) throw new Error('Duplicate device')
  return structuredClone(plan)
}
function age(value: number): boolean { return Number.isFinite(value) && value >= 0 && value < 500 }
export function readiness(plan: Plan, state: MediaState): string | undefined {
  if (state.schemaVersion !== 1 || !state.instanceId || !Array.isArray(state.outputs)) return 'Invalid media state'
  let endpoint=plan.audioEndpointId
  if(state.capabilities?.includes('audio-endpoint-binding')) {
    const binding=state.audioBinding
    if(!binding || binding.available!==true || binding.configuredEndpointId?.toLowerCase()!==endpoint.toLowerCase() || !binding.resolvedEndpointId)
      return binding?.message || 'Commissioned Dante USB endpoint binding is unavailable'
    endpoint=binding.resolvedEndpointId
  }
  for (const path of plan.paths) {
    const matches = state.outputs.filter(output => output.slot === path.slot)
    const output = matches[0]
    if (matches.length !== 1 || !output) return `Output ${path.slot} missing or ambiguous`
    if (output.emergencyConfigured===false || output.backend !== 'hardware' || !output.running || output.health !== 'healthy' || output.fallback ||
        output.deviceId?.toLowerCase() !== path.device.toLowerCase() || output.source !== path.source ||
        output.format !== '1920x1080p30000/1001' || !age(output.videoAgeMs) || !age(output.scheduleAgeMs) ||
        !Number.isSafeInteger(output.configRevision) || output.configRevision < 1 || !output.committedOperationId)
      return `Output ${path.slot}: exact source/device/format is not freshly committed and healthy`
    const audio = output.audio
    if (path.audioMode === 'silent') {
      if (!audio || audio.mode !== 'silent' || !audio.muted) return `Output ${path.slot}: silent audio policy is not committed`
      continue
    }
    if (!audio || audio.mode !== 'fixed' || audio.endpointId?.toLowerCase() !== endpoint.toLowerCase() ||
        JSON.stringify(audio.channels) !== '[1,2]' || !age(audio.ageMs) || audio.muted)
      return `Output ${path.slot}: Dante USB receive 1/2 audio is not freshly ready`
  }
  return undefined
}
function revisions(plan: Plan, state: MediaState): string {
  return JSON.stringify([state.instanceId, ...plan.paths.map(path => {
    const output = state.outputs.find(output => output.slot === path.slot)!
    return [output.slot, output.configRevision, output.committedOperationId]
  })])
}
export function takeReadiness(plan: Plan, snapshot: HubSnapshot, overwrite = false): string | undefined {
  if (performance.now() - snapshot.sampledAt > 1500) return 'Videohub snapshot stale'
  for (const path of plan.paths) {
    if (path.destination > snapshot.outputs || Math.max(path.normalInput, path.backupInput) > snapshot.inputs) return 'Plan exceeds connected router port count'
    if (snapshot.locks.get(path.destination) !== 'U') return `Destination ${path.destination} locked or lock state unknown`
    const current = snapshot.routes.get(path.destination)
    if (!Number.isSafeInteger(current) || current! < 1 || current! > snapshot.inputs) return `Destination ${path.destination}: unknown input`
    if (!overwrite && current !== path.normalInput && current !== path.backupInput) return `Destination ${path.destination}: intervening manual route ${current}`
  }
  return undefined
}
export type RouteMode = 'FIBER' | 'NETWORK' | 'MIXED'
export function routeMode(plan: Plan, snapshot: HubSnapshot): RouteMode {
  if (plan.paths.every(path => snapshot.routes.get(path.destination) === path.normalInput)) return 'FIBER'
  if (plan.paths.every(path => snapshot.routes.get(path.destination) === path.backupInput)) return 'NETWORK'
  return 'MIXED'
}
export function toggleReadiness(plan: Plan, snapshot: HubSnapshot, engineReason?: string): string | undefined {
  const reason = takeReadiness(plan, snapshot, true)
  if (reason) return reason
  const mode = routeMode(plan, snapshot)
  // A deliberate return to the commissioned fiber inputs must remain possible
  // if the backup media PC is unavailable. Network take still requires media.
  return mode !== 'NETWORK' ? engineReason : undefined
}
export class HttpMedia implements Media {
  constructor(private base: string) {
    const url = new URL(base)
    if (!['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname) || url.protocol !== 'http:' || url.username || url.password)
      throw new Error('This commissioned module build supports localhost HTTP only; authenticated LAN is not implemented')
  }
  async state(): Promise<MediaState> {
    const nonce = randomUUID()
    const response = await fetch(new URL(`/api/v1/state?nonce=${nonce}`, this.base), {cache: 'no-store', signal: AbortSignal.timeout(1200)})
    if (!response.ok) throw new Error(`Media API returned HTTP ${response.status}`)
    const state = await response.json() as MediaState
    if (state.nonce !== nonce) throw new Error('Media API did not return a fresh challenge response')
    return state
  }
  async prepare(plan: Plan): Promise<string> {
    const id = randomUUID()
    const response = await fetch(new URL('/api/v1/emergency/prepare', this.base), {method: 'POST',
      headers: {'Content-Type': 'application/json', 'Idempotency-Key': id}, body: JSON.stringify({schemaVersion: 1, plan}), signal: AbortSignal.timeout(1500)})
    if (!response.ok) throw new Error(`Prepare rejected: HTTP ${response.status}`)
    const result = await response.json() as {operationId?: string}
    if (!result.operationId) throw new Error('Prepare response lacks operation ID')
    return result.operationId
  }
  async operation(id:string):Promise<{state:string;error?:string;instanceId?:string}> {
    const response=await fetch(new URL(`/api/v1/operations/${encodeURIComponent(id)}`,this.base),{signal:AbortSignal.timeout(1500),cache:'no-store'})
    if(!response.ok)throw new Error('Preparation outcome unavailable; no replay')
    return await response.json() as {state:string;error?:string;instanceId?:string}
  }
  private async wait(id:string,host:string):Promise<void> {
    for(let attempt=0;attempt<400;attempt++) {
      const operation=await this.operation(id)
      if(operation.instanceId!==host)throw new Error('Media host changed; preparation outcome unknown, no replay')
      if(operation.state==='committed')return
      if(['failed','unknown'].includes(operation.state))throw new Error(operation.error ?? operation.state)
      await new Promise(resolve=>setTimeout(resolve,500))
    }
    throw new Error('Media preparation timed out; no routing or automatic replay')
  }
  async prepareReady(plan:Plan,initial:MediaState):Promise<MediaState> {
    const id=await this.prepare(plan)
    await this.wait(id,initial.instanceId)
    const state=await this.state()
    if(state.instanceId!==initial.instanceId)throw new Error('Media host changed during emergency preparation')
    const reason=readiness(plan,state)
    if(reason)throw new Error(reason)
    return state
  }
  async captureUndo(initial:MediaState):Promise<()=>Promise<void>> {
    const outputs=Object.fromEntries(initial.outputs.map(output=>{
      const settings=output.settings ?? output.desiredSettings
      if(!settings)throw new Error(`Output ${output.slot}: cannot capture production settings`)
      return [String(output.slot),{...structuredClone(settings),device:output.deviceId ?? settings.device}]
    }))
    return async()=>{
      const current=await this.state()
      if(current.instanceId!==initial.instanceId)throw new Error('Media host changed since the production snapshot; no automatic restore')
      const expectedWorkers=Object.fromEntries(current.outputs.map(o=>[String(o.slot),{expectedWorkerInstanceId:o.workerInstanceId ?? '',expectedRevision:o.configRevision ?? 0}]))
      const id=randomUUID()
      const response=await fetch(new URL('/api/v1/outputs/restore',this.base),{method:'POST',headers:{'Content-Type':'application/json','Idempotency-Key':id},body:JSON.stringify({schemaVersion:1,expectedInstanceId:current.instanceId,outputs,expectedWorkers}),signal:AbortSignal.timeout(1500)})
      if(!response.ok)throw new Error(`Production restore rejected: HTTP ${response.status}`)
      await this.wait(id,current.instanceId)
    }
  }
}
export type Outcome = {id: string; state: 'confirmed' | 'blocked' | 'partial' | 'unknown'; confirmed: number[]; reason: string; target?: 'FIBER' | 'NETWORK'}
export type PrepareUndoAudio = () => Promise<() => Promise<string>>
export interface TransitionAudio { prepare(target: 'FIBER' | 'NETWORK'): Promise<() => Promise<string>>; captureUndo?(): Promise<PrepareUndoAudio> }
export class Coordinator {
  busy = false
  private ledger = new Map<number, {epoch: number; change: number}>()
  private undoState?: {routes: Map<number, number>; audio?: PrepareUndoAudio; media?:()=>Promise<void>}
  get canUndo(): boolean { return this.undoState !== undefined }
  constructor(readonly plan: Plan, private hub: Router, private media: Media, private audio?: TransitionAudio) {}
  private preflight(snapshot: HubSnapshot, returning: boolean, observedToggle = false): void {
    if (performance.now() - snapshot.sampledAt > 1500) throw new Error('Videohub snapshot stale')
    for (const path of this.plan.paths) {
      if (path.destination > snapshot.outputs || Math.max(path.normalInput, path.backupInput) > snapshot.inputs) throw new Error('Plan exceeds connected router port count')
      if (snapshot.locks.get(path.destination) !== 'U') throw new Error(`Destination ${path.destination} locked or lock state unknown`)
      const current = snapshot.routes.get(path.destination)
      if (returning && observedToggle) {
        if (current !== path.backupInput) throw new Error(`Destination ${path.destination}: backup route no longer selected`)
      } else if (returning) {
        const ledger = this.ledger.get(path.destination)
        if (!ledger || ledger.epoch !== snapshot.epoch || ledger.change !== snapshot.changes.get(path.destination) || current !== path.backupInput)
          throw new Error(`Destination ${path.destination}: no unchanged confirmed take; use independent manual controls`)
      } else if (!observedToggle && current !== path.normalInput && current !== path.backupInput) throw new Error(`Destination ${path.destination}: intervening manual route ${current}`)
      if (!Number.isSafeInteger(current) || current! < 1 || current! > snapshot.inputs) throw new Error('Current route is unknown')
    }
  }
  take(returning = false): Promise<Outcome> { return this.run(returning ? 'return' : 'take') }
  toggle(): Promise<Outcome> { return this.run('toggle') }
  private async run(command: 'take' | 'return' | 'toggle'): Promise<Outcome> {
    const result: Outcome = {id: randomUUID(), state: 'blocked', confirmed: [], reason: ''}
    if (this.busy) return {...result, reason: 'An emergency operation is already running'}
    this.busy = true
    let sent = false
    let completedRouting = false
    let mediaChanged = false
    try {
      const initial = await this.hub.snapshot()
      const mode = routeMode(this.plan, initial)
      let mediaState:MediaState|undefined
      // Older media adapters can still return to Fiber while offline. New hosts
      // distinguish a production assignment from a prepared emergency output.
      if(command==='toggle' && mode==='NETWORK' && this.media.prepareReady) {
        try{mediaState=await this.media.state()}catch{}
      }
      const restore=mediaState ? needsRestore(this.plan,mediaState) : false
      const returning = command === 'return' || (command === 'toggle' && mode === 'NETWORK' && !restore)
      result.target = returning ? 'FIBER' : 'NETWORK'
      // The toggle is an explicit request to invert the currently read-back
      // group. A deliberate short press overwrites any valid unlocked current
      // routes: NETWORK -> FIBER; every other arrangement -> NETWORK.
      this.preflight(initial, returning, command === 'toggle')
      let expectedRevision = ''
      const completeAudio = await this.audio?.prepare(result.target)
      if(!returning)mediaState ??= await this.media.state()
      const restoring=!returning && needsRestore(this.plan,mediaState!) && !!this.media.prepareReady
      const undo = {routes: new Map(initial.routes), audio: await this.audio?.captureUndo?.(),media:restoring ? await this.media.captureUndo?.(mediaState!) : undefined}
      // Retain one pre-change snapshot even when the ensuing operation is partial.
      // Restoring it always requires a new deliberate three-second hold/release.
      if (!returning) {
        if(restoring) {
          this.undoState=undo
          mediaChanged=true
          mediaState=await this.media.prepareReady!(this.plan,mediaState!)
        }
        const reason=readiness(this.plan,mediaState!)
        if(reason)throw new Error(reason)
        expectedRevision=revisions(this.plan,mediaState!)
      }
      this.undoState=undo
      for (const path of this.plan.paths) {
        if (!returning) {
          const state = await this.media.state()
          const reason = readiness(this.plan, state)
          if (reason) throw new Error(reason)
          if (revisions(this.plan, state) !== expectedRevision) throw new Error('Media configuration changed during emergency take')
        }
        const fresh = await this.hub.snapshot()
        if (fresh.epoch !== initial.epoch) throw new Error('Videohub reconnected during operation')
        // Recheck not-yet-taken paths. No rollback overwrites a concurrent operator.
        for (const other of this.plan.paths.filter(other => !result.confirmed.includes(other.slot)))
          if (fresh.changes.get(other.destination) !== initial.changes.get(other.destination) || fresh.locks.get(other.destination) !== 'U')
            throw new Error(`Destination ${other.destination} changed or locked during operation`)
        const target = returning ? path.normalInput : path.backupInput
        sent = fresh.routes.get(path.destination) !== target
        await this.hub.route(path.destination, target, fresh.routes.get(path.destination)!, fresh.epoch)
        const after = await this.hub.snapshot()
        if (after.epoch !== fresh.epoch || after.routes.get(path.destination) !== target) throw new Error('Requested crosspoint is not confirmed')
        result.confirmed.push(path.slot)
        if (returning) this.ledger.delete(path.destination)
        else this.ledger.set(path.destination, {epoch: after.epoch, change: after.changes.get(path.destination)!})
        sent = false
      }
      // Re-read every destination to catch manual changes during the group.
      const last = await this.hub.snapshot()
      if (last.epoch !== initial.epoch) throw new Error('Connection changed after take; outcome unknown')
      result.confirmed = this.plan.paths.filter(path => last.routes.get(path.destination) === (returning ? path.normalInput : path.backupInput)).map(path => path.slot)
      result.state = result.confirmed.length === this.plan.paths.length ? 'confirmed' : 'partial'
      result.reason = result.state === 'confirmed' ? `${result.target} crosspoints confirmed; downstream SDI signal Unknown` : 'Some crosspoints changed during verification; no rollback attempted'
      if (result.state === 'confirmed' && completeAudio) {
        completedRouting = true
        const audioResult = await completeAudio()
        const final = await this.hub.snapshot()
        sent = final.epoch !== initial.epoch
        result.confirmed = sent ? [] : this.plan.paths.filter(path => final.routes.get(path.destination) === (returning ? path.normalInput : path.backupInput)).map(path => path.slot)
        if (sent || result.confirmed.length !== this.plan.paths.length)
          throw new Error('Video routes changed during audio switching; audio was applied but combined outcome is partial')
        result.reason += `; ${audioResult}`
      }
    } catch (error) {
      result.state = sent || mediaChanged && !result.confirmed.length ? 'unknown' : completedRouting || result.confirmed.length ? 'partial' : 'blocked'
      result.reason = error instanceof Error ? error.message : String(error)
    } finally { this.busy = false }
    return result
  }
  async undo(): Promise<Outcome> {
    const result: Outcome = {id: randomUUID(), state: 'blocked', confirmed: [], reason: ''}
    if (this.busy) return {...result, reason: 'An emergency operation is already running'}
    const saved = this.undoState
    if (!saved) return {...result, reason: 'No previous change to undo in this module session'}
    this.busy = true
    let sent = false
    let mediaRestored = false
    try {
      const initial = await this.hub.snapshot()
      const blocked = takeReadiness(this.plan, initial, true)
      if (blocked) throw new Error(blocked)
      for (const path of this.plan.paths) {
        const target = saved.routes.get(path.destination)
        if (!target || target > initial.inputs) throw new Error('Saved undo route is outside the connected router')
      }
      const audio = await saved.audio?.()
      if(saved.media){sent=true;await saved.media();sent=false;mediaRestored=true;}
      for (const path of this.plan.paths) {
        const fresh = await this.hub.snapshot()
        if (fresh.epoch !== initial.epoch) throw new Error('Videohub reconnected during undo')
        for (const other of this.plan.paths.filter(other => !result.confirmed.includes(other.slot)))
          if (fresh.changes.get(other.destination) !== initial.changes.get(other.destination) || fresh.locks.get(other.destination) !== 'U')
            throw new Error(`Destination ${other.destination} changed or locked during undo`)
        const target = saved.routes.get(path.destination)!
        sent = fresh.routes.get(path.destination) !== target
        await this.hub.route(path.destination, target, fresh.routes.get(path.destination)!, fresh.epoch)
        const after = await this.hub.snapshot()
        if (after.epoch !== initial.epoch || after.routes.get(path.destination) !== target) throw new Error('Undo crosspoint not confirmed')
        result.confirmed.push(path.slot); sent = false
      }
      const beforeAudio = await this.hub.snapshot()
      if (beforeAudio.epoch !== initial.epoch || this.plan.paths.some(p => beforeAudio.routes.get(p.destination) !== saved.routes.get(p.destination)))
        throw new Error('Routes changed before undo audio; no audio written')
      const audioResult = audio ? await audio() : ''
      const last = await this.hub.snapshot()
      if (last.epoch !== initial.epoch) {sent = true; throw new Error('Videohub reconnected after undo; outcome unknown')}
      result.confirmed = this.plan.paths.filter(p => last.routes.get(p.destination) === saved.routes.get(p.destination)).map(p => p.slot)
      if (result.confirmed.length !== this.plan.paths.length) throw new Error('Routes changed during undo audio')
      result.state = 'confirmed'; result.reason = `Previous crosspoints${saved.media ? ', production outputs' : ''} restored; ${audioResult}; physical signal Unknown`
      this.undoState = undefined; this.ledger.clear()
    } catch (error) {
      result.state = sent || mediaRestored && !result.confirmed.length ? 'unknown' : result.confirmed.length ? 'partial' : 'blocked'
      result.reason = error instanceof Error ? error.message : String(error)
    } finally {this.busy = false}
    return result
  }
}

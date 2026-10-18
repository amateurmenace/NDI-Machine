import { randomUUID } from 'node:crypto'
import { performance } from 'node:perf_hooks'
import type { HubSnapshot, Router } from './videohub.js'

export type Path = {slot: number; destination: number; normalInput: number; backupInput: number; device: string; source: string}
export type Plan = {schemaVersion: 1; name: string; audioEndpointId: string; paths: Path[]}
export type ReadyOutput = {
  slot: number; deviceId: string; source: string; backend: string; running: boolean;
  configRevision: number; committedOperationId: string; videoAgeMs: number; scheduleAgeMs: number;
  fallback: boolean; format: string; audio: {endpointId: string; channels: number[]; mode: string; ageMs: number; muted: boolean};
  health: string
}
export type MediaState = {schemaVersion: number; nonce: string; instanceId: string; outputs: ReadyOutput[]}
export interface Media { state(): Promise<MediaState>; prepare(plan: Plan): Promise<string> }
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
        path.normalInput === path.backupInput) throw new Error('Invalid commissioned path')
  }
  if (new Set(plan.paths.map(path => path.device.toLowerCase())).size !== plan.paths.length) throw new Error('Duplicate device')
  return structuredClone(plan)
}
function age(value: number): boolean { return Number.isFinite(value) && value >= 0 && value < 500 }
export function readiness(plan: Plan, state: MediaState): string | undefined {
  if (state.schemaVersion !== 1 || !state.instanceId || !Array.isArray(state.outputs)) return 'Invalid media state'
  for (const path of plan.paths) {
    const matches = state.outputs.filter(output => output.slot === path.slot)
    const output = matches[0]
    if (matches.length !== 1 || !output) return `Output ${path.slot} missing or ambiguous`
    if (output.backend !== 'hardware' || !output.running || output.health !== 'healthy' || output.fallback ||
        output.deviceId?.toLowerCase() !== path.device.toLowerCase() || output.source !== path.source ||
        output.format !== '1920x1080p30000/1001' || !age(output.videoAgeMs) || !age(output.scheduleAgeMs) ||
        !Number.isSafeInteger(output.configRevision) || output.configRevision < 1 || !output.committedOperationId)
      return `Output ${path.slot}: exact source/device/format is not freshly committed and healthy`
    const audio = output.audio
    if (!audio || audio.mode !== 'fixed' || audio.endpointId?.toLowerCase() !== plan.audioEndpointId.toLowerCase() ||
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
export function takeReadiness(plan: Plan, snapshot: HubSnapshot): string | undefined {
  if (performance.now() - snapshot.sampledAt > 1500) return 'Videohub snapshot stale'
  for (const path of plan.paths) {
    if (path.destination > snapshot.outputs || Math.max(path.normalInput, path.backupInput) > snapshot.inputs) return 'Plan exceeds connected router port count'
    if (snapshot.locks.get(path.destination) !== 'U') return `Destination ${path.destination} locked or lock state unknown`
    const current = snapshot.routes.get(path.destination)
    if (current !== path.normalInput && current !== path.backupInput) return `Destination ${path.destination}: intervening manual route ${current}`
  }
  return undefined
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
}
export type Outcome = {id: string; state: 'confirmed' | 'blocked' | 'partial' | 'unknown'; confirmed: number[]; reason: string}
export class Coordinator {
  busy = false
  private ledger = new Map<number, {epoch: number; change: number}>()
  constructor(readonly plan: Plan, private hub: Router, private media: Media) {}
  private preflight(snapshot: HubSnapshot, returning: boolean): void {
    if (performance.now() - snapshot.sampledAt > 1500) throw new Error('Videohub snapshot stale')
    for (const path of this.plan.paths) {
      if (path.destination > snapshot.outputs || Math.max(path.normalInput, path.backupInput) > snapshot.inputs) throw new Error('Plan exceeds connected router port count')
      if (snapshot.locks.get(path.destination) !== 'U') throw new Error(`Destination ${path.destination} locked or lock state unknown`)
      const current = snapshot.routes.get(path.destination)
      if (returning) {
        const ledger = this.ledger.get(path.destination)
        if (!ledger || ledger.epoch !== snapshot.epoch || ledger.change !== snapshot.changes.get(path.destination) || current !== path.backupInput)
          throw new Error(`Destination ${path.destination}: no unchanged confirmed take; use independent manual controls`)
      } else if (current !== path.normalInput && current !== path.backupInput) throw new Error(`Destination ${path.destination}: intervening manual route ${current}`)
    }
  }
  async take(returning = false): Promise<Outcome> {
    const result: Outcome = {id: randomUUID(), state: 'blocked', confirmed: [], reason: ''}
    if (this.busy) return {...result, reason: 'An emergency operation is already running'}
    this.busy = true
    let sent = false
    try {
      const initial = await this.hub.snapshot()
      this.preflight(initial, returning)
      let expectedRevision = ''
      if (!returning) {
        const state = await this.media.state()
        const reason = readiness(this.plan, state)
        if (reason) throw new Error(reason)
        expectedRevision = revisions(this.plan, state)
      }
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
      result.reason = result.state === 'confirmed' ? 'Crosspoints confirmed; downstream SDI signal Unknown' : 'Some crosspoints changed during verification; no rollback attempted'
    } catch (error) {
      result.state = sent ? 'unknown' : result.confirmed.length ? 'partial' : 'blocked'
      result.reason = error instanceof Error ? error.message : String(error)
    } finally { this.busy = false }
    return result
  }
}

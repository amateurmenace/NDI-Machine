import { Atem, Enums } from 'atem-connection'
import { setTimeout as delay } from 'node:timers/promises'
import type { Plan, TransitionAudio } from './coordinator.js'

export type AudioTarget = 'FIBER' | 'NETWORK'
export type AudioSource = {input: number; source: string; port: number; mix: number; supported: number[]}
export type AudioSnapshot = {epoch: number; connected: boolean; model: number; sources: AudioSource[]}
export interface AudioMixer { snapshot(): AudioSnapshot; setMix(input: number, source: string, mix: number): Promise<void> }
export interface ZoomButtons { check(): Promise<void>; press(target: AudioTarget): Promise<void>; selected?(): Promise<AudioTarget> }
export const OFF = Enums.FairlightAudioMixOption.Off
export const ON = Enums.FairlightAudioMixOption.On
const fiberEnabled = new Set([1001, 1501, 1502])
const key = (s: AudioSource): string => `${s.input}/${s.source}`
const topology = (s: AudioSnapshot): string => JSON.stringify(s.sources.map(x => [x.input, x.source, x.port, x.supported]).sort())

export function networkAudioInput(plan:Plan):number {
  const carrying=plan.paths.filter(p=>p.audioMode==='fixed')
  if(carrying.length!==1 || plan.paths.some(p=>!['fixed','silent'].includes(p.audioMode ?? '')))
    throw new Error('Commission exactly one audio-bearing backup path; all other paths must be silent')
  const input=carrying[0].atemInput
  if(!Number.isSafeInteger(input) || input!<1 || input!>8)throw new Error('Commission the physical ATEM input for the audio-bearing backup path')
  return input!
}
export function audioPlan(snapshot: AudioSnapshot, target: AudioTarget, networkInput=1): AudioSource[] {
  if(!Number.isSafeInteger(networkInput) || networkInput<1 || networkInput>8)throw new Error('Invalid commissioned network audio input')
  if (!snapshot.connected) throw new Error('ATEM audio connection unavailable')
  if (snapshot.model !== 27) throw new Error('ATEM is not the commissioned Television Studio HD8 ISO')
  // Check port identities as well as numeric input IDs. Never treat an arbitrary
  // similarly numbered device as the commissioned SDI/XLR/MADI mixer.
  const required = [
    ...Array.from({length: 8}, (_, i) => [i + 1, 1]), [1001, 32], [1201, 128], [1301, 512],
    ...Array.from({length: 16}, (_, i) => [1501 + i, 1024]), [2001, 256], [2002, 256],
  ]
  for (const [input, port] of required)
    if (!snapshot.sources.some(s => s.input === input && s.port === port)) throw new Error(`ATEM audio input ${input} missing or wrong port type`)
  if (new Set(snapshot.sources.map(key)).size !== snapshot.sources.length) throw new Error('Duplicate ATEM audio source')
  return snapshot.sources.map(source => {
    const mix = (target==='NETWORK' ? source.input===networkInput : fiberEnabled.has(source.input)) ? ON : OFF
    if (!source.supported.includes(mix) || !Number.isFinite(source.mix)) throw new Error(`ATEM audio ${key(source)} cannot apply ${target}`)
    return {...source, mix}
  })
}

function unchanged(initial: AudioSnapshot, current: AudioSnapshot, expected: Map<string, number>): void {
  if (!current.connected || current.epoch !== initial.epoch) throw new Error('ATEM disconnected or reconnected during audio operation')
  if (current.model !== initial.model || topology(current) !== topology(initial)) throw new Error('ATEM audio source configuration changed')
  for (const source of current.sources)
    if (source.mix !== expected.get(key(source))) throw new Error(`ATEM audio ${key(source)} changed during operation`)
}

export class EmergencyAudio implements TransitionAudio {
  constructor(private mixer: AudioMixer, private zoom: ZoomButtons, private networkInput=1) {}
  profile(): AudioTarget | 'MIXED' | 'UNKNOWN' {
    try {
      const snapshot = this.mixer.snapshot()
      for (const target of ['NETWORK', 'FIBER'] as const)
        if (audioPlan(snapshot, target,this.networkInput).every(s => snapshot.sources.find(x => key(x) === key(s))!.mix === s.mix)) return target
      return 'MIXED'
    } catch { return 'UNKNOWN' }
  }
  async check(target: AudioTarget): Promise<void> { audioPlan(this.mixer.snapshot(), target,this.networkInput); await this.zoom.check() }
  async captureUndo(): Promise<() => Promise<() => Promise<string>>> {
    const saved = this.mixer.snapshot()
    audioPlan(saved, 'NETWORK',this.networkInput)
    if (!this.zoom.selected) throw new Error('Cannot capture previous Zoom selection for undo')
    const previousZoom = await this.zoom.selected()
    return async () => {
      const current = this.mixer.snapshot()
      audioPlan(current, 'NETWORK',this.networkInput); await this.zoom.check()
      if (topology(current) !== topology(saved)) throw new Error('ATEM source layout differs from saved undo')
      for (const source of saved.sources)
        if (!source.supported.includes(source.mix)) throw new Error('Saved ATEM mix option is unsupported')
      return this.prepared(current, saved.sources, previousZoom, 'previous')
    }
  }
  async prepare(target: AudioTarget): Promise<() => Promise<string>> {
    const initial = this.mixer.snapshot()
    const plan = audioPlan(initial, target,this.networkInput)
    await this.zoom.check()
    return this.prepared(initial, plan, target, target)
  }
  private prepared(initial: AudioSnapshot, plan: AudioSource[], target: AudioTarget, description: string): () => Promise<string> {
    const expected = new Map(initial.sources.map(s => [key(s), s.mix]))
    let used = false
    return async () => {
      if (used) throw new Error('Audio operation cannot be replayed')
      used = true
      unchanged(initial, this.mixer.snapshot(), expected)
      await this.zoom.check()
      // Invoke the existing buttons, including their X32 and Zoom Mac actions.
      // HTTP success is dispatch confirmation, not proof of Zoom device state.
      await this.zoom.press(target)
      // Silence unwanted sources before enabling the requested ones. Change only
      // mixOption; master output, gains, processing, routing and channel splits stay intact.
      for (const source of [...plan].sort((a, b) => a.mix - b.mix)) {
        unchanged(initial, this.mixer.snapshot(), expected)
        if (expected.get(key(source)) === source.mix) continue
        await this.mixer.setMix(source.input, source.source, source.mix)
        expected.set(key(source), source.mix)
        unchanged(initial, this.mixer.snapshot(), expected)
      }
      unchanged(initial, this.mixer.snapshot(), expected)
      return `ATEM ${description} audio confirmed; ZOOM ${target === 'NETWORK' ? 'DANTE' : 'MADI'} button dispatched`
    }
  }
}

export class CompanionZoom implements ZoomButtons {
  private base: URL
  constructor(base: string, private network: string, private fiber: string) {
    this.base = new URL(base)
    if (this.base.protocol !== 'http:' || !['127.0.0.1', 'localhost', '[::1]'].includes(this.base.hostname) || this.base.username || this.base.password)
      throw new Error('Companion button API must be localhost HTTP')
    for (const location of [network, fiber])
      if (!/^[1-9]\d*\/\d+\/\d+$/.test(location)) throw new Error('ZOOM button must be page/row/column')
    if (network === fiber || [network, fiber].some(location => ['1/1/0', '21/1/1'].includes(location))) throw new Error('ZOOM buttons must be distinct and cannot target a commissioned fail-over button')
  }
  async check(): Promise<void> {
    const response = await fetch(new URL('/api/connections', this.base), {cache: 'no-store', signal: AbortSignal.timeout(1500)})
    if (!response.ok) throw new Error(`Companion connections unavailable: HTTP ${response.status}`)
    const connections = await response.json() as {label: string; enabled: boolean; status?: {level?: string}}[]
    for (const label of ['x32', 'zoomswitch']) {
      const matches = connections.filter(c => c.label === label)
      if (matches.length !== 1 || !matches[0].enabled || matches[0].status?.level !== 'ok') throw new Error(`Companion ${label} connection is not ready`)
    }
  }
  async press(target: AudioTarget): Promise<void> {
    const location = target === 'NETWORK' ? this.network : this.fiber
    const response = await fetch(new URL(`/api/location/${location}/press`, this.base), {method: 'POST', signal: AbortSignal.timeout(1500)})
    if (!response.ok) throw new Error(`ZOOM ${target} button dispatch failed: HTTP ${response.status}; not retried`)
  }
  async selected(): Promise<AudioTarget> {
    const response = await fetch(new URL('/api/custom-variable/zoom_route/value', this.base), {cache: 'no-store', signal: AbortSignal.timeout(1500)})
    if (!response.ok) throw new Error('Cannot capture previous Zoom button selection')
    const value = (await response.text()).trim().replace(/^"|"$/g, '')
    if (value === 'DANTE') return 'NETWORK'
    if (value === 'MADI') return 'FIBER'
    throw new Error('Previous Zoom selection is unknown; set ZOOM DANTE or ZOOM MADI before fail-over')
  }
}

export class AtemAudio implements AudioMixer {
  private atem = new Atem({disableMultithreaded: true})
  private connected = false
  private epoch = 0
  private closed = false
  constructor(private host: string, log: (message: string) => void) {
    if (!host?.trim()) throw new Error('Commissioned ATEM IP is required for fail-over audio')
    this.atem.on('error', error => log(`ATEM audio: ${error}`))
    this.atem.on('connected', () => { this.epoch++; this.connected = true })
    this.atem.on('disconnected', () => { this.epoch++; this.connected = false })
    void this.atem.connect(host.trim()).catch(error => log(`ATEM audio connection: ${String(error)}`))
  }
  snapshot(): AudioSnapshot {
    const state = this.atem.state
    const sources: AudioSource[] = []
    for (const [input, value] of Object.entries(state?.fairlight?.inputs ?? {})) {
      if (!value?.properties || !Object.keys(value.sources ?? {}).length) throw new Error(`ATEM input ${input} lacks audio state`)
      for (const [source, value2] of Object.entries(value.sources)) {
        if (!value2?.properties) throw new Error(`ATEM source ${input}/${source} lacks audio state`)
        sources.push({input: Number(input), source, port: value.properties.externalPortType, mix: value2.properties.mixOption, supported: [...value2.properties.supportedMixOptions]})
      }
    }
    return {epoch: this.epoch, connected: this.connected, model: state?.info.model ?? 0, sources}
  }
  async setMix(input: number, source: string, mix: number): Promise<void> {
    const epoch = this.epoch
    let timer: NodeJS.Timeout | undefined
    try {
      await Promise.race([
        this.atem.setFairlightAudioMixerSourceProps(input, source, {mixOption: mix}),
        new Promise<never>((_, reject) => { timer = setTimeout(() => reject(new Error('ATEM audio ACK timed out; outcome unknown')), 2000) }),
      ])
      const end = performance.now() + 1200
      while (performance.now() < end) {
        if (!this.connected || this.epoch !== epoch) throw new Error('ATEM connection changed while applying audio')
        if (this.snapshot().sources.find(s => s.input === input && s.source === source)?.mix === mix) return
        await delay(20)
      }
      throw new Error(`ATEM audio ${input}/${source} readback did not match requested mix`)
    } catch (error) {
      // Stop any outstanding transport retries on uncertain commands. Reconnect
      // restores observation only; nothing replays the requested audio profile.
      this.connected = false
      await this.atem.disconnect().catch(() => undefined)
      if (!this.closed) void this.atem.connect(this.host).catch(() => undefined)
      throw error
    } finally { if (timer) clearTimeout(timer) }
  }
  async close(): Promise<void> {
    this.closed = true
    this.connected = false
    await this.atem.disconnect()
    // The library may have a pending UDP ACK timer during disconnect.
    await delay(30)
    await this.atem.destroy()
  }
}

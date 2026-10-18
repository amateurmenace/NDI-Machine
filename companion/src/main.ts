import { InstanceBase, InstanceStatus, type SomeCompanionConfigField } from '@companion-module/base'
import { Coordinator, HttpMedia, readiness, takeReadiness, validatePlan, type Plan } from './coordinator.js'
import { Videohub } from './videohub.js'
import { performance } from 'node:perf_hooks'

type Config = {host: string; apiUrl: string; plan: string}
type Empty = Record<string, never>
type Schema = {
  config: Config; secrets: undefined;
  actions: {take_all: {options: Empty}; return_normal: {options: Empty}; prepare: {options: Empty}; check: {options: Empty}};
  feedbacks: {blocked: {type: 'boolean'; options: Empty}; ready: {type: 'boolean'; options: Empty};
    active: {type: 'boolean'; options: Empty}; working: {type: 'boolean'; options: Empty}};
  variables: {status: string; reason: string; engine_ready: string; routes: string; signal: string; operation: string}
}
export const UpgradeScripts = []
export default class ModuleInstance extends InstanceBase<Schema> {
  private hub?: Videohub
  private media?: HttpMedia
  private coordinator?: Coordinator
  private plan?: Plan
  private timer?: NodeJS.Timeout
  private pollTask?: Promise<void>
  private performing = false
  private ready = false
  private engineReady = false
  private routesConfirmed = false
  private observedAt = 0
  private reason = 'Not configured'
  private lastResult = ''
  private outcomeReason = ''
  private operation = ''
  async init(config: Config): Promise<void> {
    this.setVariableDefinitions({
      status: {name: 'Emergency state'}, reason: {name: 'Readiness block or last result'},
      engine_ready: {name: 'Fresh backup engine readiness'}, routes: {name: 'Videohub crosspoint readback'},
      signal: {name: 'Independently verified downstream SDI signal'}, operation: {name: 'Last operation ID'},
    })
    this.setActionDefinitions({
      take_all: {name: 'FAIL-OVER: Guarded take all configured backups', options: [], callback: async () => this.execute(false)},
      return_normal: {name: 'Return Normal: verified paths only', options: [], callback: async () => this.execute(true)},
      prepare: {name: 'Prepare backups (no Videohub routing)', options: [], callback: async () => {
        if (!this.media || !this.plan || this.performing) return
        try { this.operation = await this.media.prepare(this.plan); this.reason = 'Preparation accepted; waiting for committed readiness' }
        catch (error) { this.reason = `Prepare blocked: ${String(error)}` }
        this.publish()
      }},
      check: {name: 'Check readiness and routes (read only)', options: [], callback: async () => { await this.refresh() }},
    })
    this.setFeedbackDefinitions({
      blocked: {name: 'Emergency blocked or state stale', type: 'boolean', options: [], defaultStyle: {bgcolor: 0x7d1010, color: 0xffffff}, callback: () => !this.fresh() || !this.ready || this.unresolved()},
      ready: {name: 'Exact backup engines freshly ready', type: 'boolean', options: [], defaultStyle: {bgcolor: 0xc27a00, color: 0xffffff}, callback: () => this.fresh() && this.ready},
      active: {name: 'Engines ready and all backup routes confirmed (signal Unknown)', type: 'boolean', options: [], defaultStyle: {bgcolor: 0x176434, color: 0xffffff}, callback: () => this.fresh() && this.ready && this.routesConfirmed && !this.unresolved()},
      working: {name: 'Emergency operation in progress', type: 'boolean', options: [], defaultStyle: {bgcolor: 0x234b88, color: 0xffffff}, callback: () => this.performing},
    })
    this.setPresetDefinitions([{id: 'emergency', name: 'Emergency routing', definitions: ['take', 'prepare', 'return', 'check']}], {
      take: {type: 'simple', name: 'FAIL-OVER (Fiber to Network)', style: {text: 'FAIL-OVER\nFiber to Network\n$(self:status)', size: 'auto', color: 0xffffff, bgcolor: 0x7d1010},
        steps: [{down: [{actionId: 'take_all', options: {}}], up: []}],
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
      {type: 'static-text', id: 'info', label: 'Operation', width: 12,
        value: 'Take checks exact fresh hardware source, format, USB audio and committed revision; preflights locks and routes; then verifies each crosspoint. No automatic take or return. The native production control API must be running. SDI signal verification remains Unknown.'},
    ]
  }
  async configUpdated(config: Config): Promise<void> {
    await this.destroy()
    this.ready = false; this.engineReady = false; this.routesConfirmed = false; this.observedAt = 0; this.operation = ''; this.lastResult = ''; this.outcomeReason = ''
    try {
      if (!config.host?.trim()) throw new Error('Videohub address is required')
      this.plan = validatePlan(JSON.parse(config.plan))
      this.media = new HttpMedia(config.apiUrl)
      this.hub = new Videohub(config.host.trim())
      this.coordinator = new Coordinator(this.plan, this.hub, this.media)
      this.reason = 'Awaiting fresh media and router state'
      this.timer = setInterval(() => { void this.refresh() }, 1000)
      await this.refresh()
    } catch (error) { this.reason = String(error); this.updateStatus(InstanceStatus.BadConfig, this.reason); this.publish() }
  }
  async destroy(): Promise<void> {
    if (this.timer) clearInterval(this.timer)
    this.timer = undefined; this.hub?.close()
    await this.pollTask
    this.hub = undefined; this.media = undefined; this.plan = undefined; this.coordinator = undefined
  }
  private fresh(): boolean { return this.observedAt > 0 && performance.now() - this.observedAt < 2500 }
  private unresolved(): boolean { return ['PARTIAL', 'UNKNOWN'].includes(this.lastResult) }
  private publish(): void {
    const fresh = this.fresh()
    const status = this.performing ? 'WORKING' : !fresh || !this.ready ? 'BLOCKED' : ['PARTIAL', 'UNKNOWN'].includes(this.lastResult) ? this.lastResult : this.routesConfirmed ? 'BACKUP' : 'READY'
    this.setVariableValues({status, reason: (this.unresolved() ? `${this.outcomeReason}; current: ${this.reason}` : this.reason).slice(0, 500), engine_ready: fresh && this.engineReady ? 'READY' : 'NOT READY',
      routes: fresh && this.routesConfirmed ? 'BACKUP ROUTES CONFIRMED' : 'NOT CONFIRMED', signal: 'UNKNOWN', operation: this.operation})
    this.checkFeedbacks('blocked', 'ready', 'active', 'working')
  }
  private refresh(): Promise<void> {
    if (this.performing || this.pollTask || !this.plan || !this.hub || !this.media) { this.publish(); return this.pollTask ?? Promise.resolve() }
    this.pollTask = (async () => {
      try {
        const [hub, media] = await Promise.allSettled([this.hub!.snapshot(), this.media!.state()])
        this.engineReady = media.status === 'fulfilled' && !readiness(this.plan!, media.value)
        const routerReason = hub.status === 'fulfilled' ? takeReadiness(this.plan!, hub.value) : `Videohub unavailable: ${String(hub.reason)}`
        this.ready = this.engineReady && !routerReason
        this.routesConfirmed = hub.status === 'fulfilled' && this.plan!.paths.every(path => hub.value.routes.get(path.destination) === path.backupInput)
        this.reason = routerReason ?? (
          media.status === 'rejected' ? 'BLOCKED: media control API unavailable; backups cannot be verified' : readiness(this.plan!, media.value) ?? 'Engine ready; downstream SDI signal Unknown'
        )
        this.observedAt = performance.now()
        this.updateStatus(this.ready && hub.status === 'fulfilled' ? InstanceStatus.Ok : InstanceStatus.UnknownWarning, this.reason)
      } catch (error) { this.ready = false; this.engineReady = false; this.routesConfirmed = false; this.reason = String(error); this.observedAt = 0 }
      this.publish()
    })().finally(() => { this.pollTask = undefined })
    return this.pollTask
  }
  private async execute(returning: boolean): Promise<void> {
    if (this.performing || !this.coordinator) return
    this.performing = true; this.lastResult = ''; this.outcomeReason = ''; this.publish()
    try {
      await this.pollTask
      const result = await this.coordinator.take(returning)
      this.operation = result.id; this.reason = result.reason; this.outcomeReason = result.reason; this.lastResult = result.state.toUpperCase()
      this.log(result.state === 'confirmed' ? 'info' : 'warn', `${returning ? 'Return Normal' : 'FAIL-OVER'} ${result.state}: ${result.reason}; confirmed slots ${result.confirmed.join(',') || 'none'}`)
    } finally { this.performing = false; this.publish(); await this.refresh() }
  }
}

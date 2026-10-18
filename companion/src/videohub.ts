import { Socket } from 'node:net'
import { EventEmitter } from 'node:events'
import { performance } from 'node:perf_hooks'

export type HubSnapshot = {
  epoch: number; routes: Map<number, number>; locks: Map<number, string>;
  changes: Map<number, number>; inputs: number; outputs: number; sampledAt: number
}
export interface Router {
  snapshot(): Promise<HubSnapshot>
  route(destination: number, source: number, expected: number, epoch: number): Promise<void>
}
// Stream parser accepts fragmented LF/CRLF blocks and bounds incomplete input.
export class Blocks {
  private text = ''
  feed(chunk: string): string[][] {
    this.text += chunk
    if (this.text.length > 262144) throw new Error('Videohub protocol buffer exceeded limit')
    const result: string[][] = []
    for (;;) {
      const delimiter = /\r?\n\r?\n/.exec(this.text)
      if (!delimiter) break
      const block = this.text.slice(0, delimiter.index).split(/\r?\n/)
      this.text = this.text.slice(delimiter.index + delimiter[0].length)
      if (block[0]) result.push(block)
    }
    return result
  }
}
export class Videohub extends EventEmitter implements Router {
  private socket?: Socket
  private parser = new Blocks()
  private routes = new Map<number, number>()
  private locks = new Map<number, string>()
  private changes = new Map<number, number>()
  private versions = new Map<string, number>()
  private inputs = 0
  private outputs = 0
  private version = ''
  private present = false
  private prelude = false
  private connected = false
  private busy = false
  private epoch = 0
  private nak = 0
  private sampledAt = 0
  constructor(private host: string, private port = 9990, private timeoutMs = 1800) { super() }
  close(): void { this.socket?.destroy(); this.socket = undefined; this.connected = false; this.sampledAt = 0 }
  private accept(lines: string[]): void {
    const [header, ...body] = lines
    this.versions.set(header, (this.versions.get(header) ?? 0) + 1)
    if (header === 'NAK') ++this.nak
    if (header === 'PROTOCOL PREAMBLE:') this.version = body.find(l => l.startsWith('Version: '))?.slice(9) ?? ''
    if (header === 'END PRELUDE:') this.prelude = true
    if (header === 'VIDEOHUB DEVICE:') {
      for (const line of body) {
        const [key, value] = line.split(': ')
        if (key === 'Device present') this.present = value === 'true'
        if (key === 'Video inputs') this.inputs = Number(value)
        if (key === 'Video outputs') this.outputs = Number(value)
      }
    }
    if (header === 'VIDEO OUTPUT ROUTING:' || header === 'VIDEO OUTPUT LOCKS:') {
      for (const line of body) {
        const match = /^(\d+) (\d+|U|O|L)$/.exec(line)
        if (!match) continue
        const destination = Number(match[1]) + 1 // sole wire-to-physical conversion
        if (destination < 1 || destination > 4096) continue
        if (header === 'VIDEO OUTPUT ROUTING:') {
          if (!/^\d+$/.test(match[2])) continue
          const source = Number(match[2]) + 1
          if (this.routes.get(destination) !== source) this.changes.set(destination, (this.changes.get(destination) ?? 0) + 1)
          this.routes.set(destination, source)
        } else if (/^[UOL]$/.test(match[2])) this.locks.set(destination, match[2])
      }
    }
    // Unknown compatible-minor blocks (including front-panel TAKE MODE) are
    // ignored. We never alter Take Mode, labels, profiles, or locks.
    this.emit('update')
  }
  private waitUntil(predicate: () => boolean, context: string): Promise<void> {
    if (predicate()) return Promise.resolve()
    return new Promise((resolve, reject) => {
      const finish = (error?: Error): void => {
        clearTimeout(timer); this.off('update', changed); this.off('disconnected', disconnected)
        error ? reject(error) : resolve()
      }
      const changed = (): void => { if (predicate()) finish() }
      const disconnected = (): void => finish(new Error(`${context}: connection lost; outcome unknown`))
      const timer = setTimeout(() => finish(new Error(`${context}: timed out; outcome unknown`)), this.timeoutMs)
      this.on('update', changed); this.on('disconnected', disconnected)
      changed()
    })
  }
  private async connect(): Promise<void> {
    if (this.connected && this.prelude) return
    this.close()
    this.parser = new Blocks(); this.routes.clear(); this.locks.clear(); this.changes.clear(); this.versions.clear()
    this.prelude = false; this.present = false; this.version = ''; this.inputs = 0; this.outputs = 0; ++this.epoch
    const socket = this.socket = new Socket()
    socket.setNoDelay(true)
    socket.on('connect', () => { this.connected = true; this.emit('update') })
    socket.on('error', () => socket.destroy())
    socket.on('close', () => {
      if (this.socket === socket) { this.connected = false; this.sampledAt = 0; this.emit('disconnected') }
    })
    socket.on('data', data => {
      try { for (const lines of this.parser.feed(data.toString('utf8'))) this.accept(lines) }
      catch { socket.destroy() }
    })
    socket.connect(this.port, this.host)
    try {
      await this.waitUntil(() => this.connected && this.prelude, 'Videohub initial state')
      if (!this.version.startsWith('2.') || !this.present || this.inputs < 1 || this.outputs < 1)
        throw new Error('Videohub missing, invalid, or incompatible protocol major version')
    } catch (error) { this.close(); throw error }
  }
  private async command(text: string, confirm: () => boolean): Promise<void> {
    if (!this.connected || !this.socket) throw new Error('Videohub disconnected')
    const nak = this.nak
    const ack = this.versions.get('ACK') ?? 0
    const pending = this.waitUntil(() => this.nak !== nak || ((this.versions.get('ACK') ?? 0) > ack && confirm()), 'Videohub readback')
    this.socket.write(text)
    try {
      await pending
      if (this.nak !== nak) throw new Error('Videohub rejected command')
    } catch (error) { this.close(); throw error }
  }
  private async exclusive<T>(task: () => Promise<T>): Promise<T> {
    if (this.busy) throw new Error('Videohub command already in progress')
    this.busy = true
    try { return await task() } finally { this.busy = false }
  }
  async snapshot(): Promise<HubSnapshot> {
    return this.exclusive(async () => {
      await this.connect()
      for (const header of ['VIDEO OUTPUT ROUTING:', 'VIDEO OUTPUT LOCKS:']) {
        const before = this.versions.get(header) ?? 0
        await this.command(`${header}\n\n`, () => (this.versions.get(header) ?? 0) > before)
      }
      if (this.routes.size !== this.outputs || this.locks.size !== this.outputs)
        throw new Error('Videohub state is incomplete')
      this.sampledAt = performance.now()
      return {epoch: this.epoch, routes: new Map(this.routes), locks: new Map(this.locks), changes: new Map(this.changes),
        inputs: this.inputs, outputs: this.outputs, sampledAt: this.sampledAt}
    })
  }
  async route(destination: number, source: number, expected: number, epoch: number): Promise<void> {
    return this.exclusive(async () => {
      if (!this.connected || epoch !== this.epoch || performance.now() - this.sampledAt > 1500)
        throw new Error('Videohub state expired or connection changed')
      if (!Number.isSafeInteger(destination) || !Number.isSafeInteger(source) || destination < 1 || destination > this.outputs || source < 1 || source > this.inputs)
        throw new Error('Uncommissioned physical port')
      if (this.locks.get(destination) !== 'U') throw new Error(`Destination ${destination} is locked`)
      if (this.routes.get(destination) !== expected) throw new Error(`Destination ${destination} changed before take`)
      if (expected === source) return
      const before = this.versions.get('VIDEO OUTPUT ROUTING:') ?? 0
      await this.command(`VIDEO OUTPUT ROUTING:\n${destination - 1} ${source - 1}\n\n`,
        () => (this.versions.get('VIDEO OUTPUT ROUTING:') ?? 0) > before && this.routes.get(destination) === source)
    })
  }
}

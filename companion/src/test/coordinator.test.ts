import { test } from 'node:test'
import assert from 'node:assert/strict'
import { performance } from 'node:perf_hooks'
import { Coordinator, readiness, takeReadiness, validatePlan, type Media, type MediaState, type Plan } from '../coordinator.js'
import type { HubSnapshot, Router } from '../videohub.js'
const plan: Plan = {schemaVersion: 1, name: 'test plan', audioEndpointId: 'usb-test', paths: [
  {slot: 1, destination: 33, normalInput: 9, backupInput: 8, device: '0xa1', source: 'Cam 1'},
  {slot: 2, destination: 34, normalInput: 10, backupInput: 6, device: '0xa2', source: 'Cam 2'},
]}
function readyState(): MediaState {
  return {schemaVersion: 1, instanceId: 'engine-a', nonce: 'test', outputs: plan.paths.map(path => ({
    slot: path.slot, deviceId: path.device, source: path.source, backend: 'hardware', running: true,
    configRevision: 1, committedOperationId: 'operation-1', videoAgeMs: 25, scheduleAgeMs: 30,
    fallback: false, format: '1920x1080p30000/1001', health: 'healthy',
    audio: {endpointId: plan.audioEndpointId, channels: [1, 2], mode: 'fixed', ageMs: 10, muted: false},
  }))}
}
class FakeHub implements Router {
  routes = new Map([[33, 9], [34, 10]])
  locks = new Map([[33, 'U'], [34, 'U']])
  changes = new Map([[33, 1], [34, 1]])
  writes: [number, number][] = []
  epoch = 1
  afterWrite?: () => void
  async snapshot(): Promise<HubSnapshot> { return {epoch: this.epoch, routes: new Map(this.routes), locks: new Map(this.locks), changes: new Map(this.changes), inputs: 40, outputs: 40, sampledAt: performance.now()} }
  async route(destination: number, source: number, expected: number, epoch: number): Promise<void> {
    assert.equal(epoch, this.epoch); assert.equal(this.routes.get(destination), expected)
    if (expected !== source) { this.writes.push([destination, source]); this.manual(destination, source) }
    this.afterWrite?.()
  }
  manual(destination: number, source: number): void { this.routes.set(destination, source); this.changes.set(destination, this.changes.get(destination)! + 1) }
}
function media(get: () => MediaState = readyState): Media { return {state: async () => get(), prepare: async () => 'pending-op'} }
test('button readiness rejects locked, manually rerouted, stale and wrong-size routers', async () => {
  const hub = new FakeHub()
  assert.equal(takeReadiness(plan, await hub.snapshot()), undefined)
  hub.locks.set(34, 'L'); assert.match(takeReadiness(plan, await hub.snapshot())!, /locked/)
  hub.locks.set(34, 'U'); hub.manual(33, 20); assert.match(takeReadiness(plan, await hub.snapshot())!, /manual/)
  hub.manual(33, 9); const snapshot = await hub.snapshot()
  snapshot.sampledAt -= 2000; assert.match(takeReadiness(plan, snapshot)!, /stale/)
  snapshot.sampledAt = performance.now(); snapshot.outputs = 32; assert.match(takeReadiness(plan, snapshot)!, /port count/)
})
test('validated physical plan rejects duplicate output and malformed ports', () => {
  assert.deepEqual(validatePlan(plan), plan)
  const invalid = structuredClone(plan); invalid.paths[1].slot = 1
  assert.throws(() => validatePlan(invalid), /Duplicate/)
  invalid.paths[1].slot = 2; invalid.paths[1].destination = 0
  assert.throws(() => validatePlan(invalid), /Invalid/)
})
test('all required media evidence is exact; simulation and stale media cannot satisfy readiness', () => {
  assert.equal(readiness(plan, readyState()), undefined)
  for (const mutate of [
    (s: MediaState) => s.outputs[0].backend = 'simulation',
    (s: MediaState) => s.outputs[0].videoAgeMs = 501,
    (s: MediaState) => s.outputs[0].scheduleAgeMs = NaN,
    (s: MediaState) => s.outputs[0].source = 'Other camera',
    (s: MediaState) => s.outputs[0].fallback = true,
    (s: MediaState) => s.outputs[0].configRevision = 0,
    (s: MediaState) => s.outputs[0].audio.endpointId = 'DVS',
    (s: MediaState) => s.outputs[0].audio.ageMs = 501,
    (s: MediaState) => s.outputs[0].audio.muted = true,
    (s: MediaState) => s.outputs[0].audio.channels = [3, 4],
  ]) { const state = readyState(); mutate(state); assert.ok(readiness(plan, state)) }
})
test('absent media API and locked destination preserve all current routes', async () => {
  const hub = new FakeHub()
  const offline: Media = {state: async () => {throw new Error('offline')}, prepare: async () => ''}
  assert.equal((await new Coordinator(plan, hub, offline).take()).state, 'blocked')
  assert.deepEqual(hub.writes, [])
  hub.locks.set(34, 'L')
  assert.equal((await new Coordinator(plan, hub, media()).take()).state, 'blocked')
  assert.deepEqual(hub.writes, [])
})
test('successful take confirms only commissioned destinations and deliberate return restores only those', async () => {
  const hub = new FakeHub(); const coordinator = new Coordinator(plan, hub, media())
  assert.equal((await coordinator.take()).state, 'confirmed')
  assert.deepEqual(hub.writes, [[33, 8], [34, 6]])
  assert.equal((await coordinator.take()).state, 'confirmed') // repeated take is not a toggle
  assert.equal(hub.writes.length, 2)
  assert.equal((await coordinator.take(true)).state, 'confirmed')
  assert.deepEqual(hub.writes, [[33, 8], [34, 6], [33, 9], [34, 10]])
})
test('changed operation revision prevents any writes', async () => {
  const hub = new FakeHub(); let calls = 0
  const changing = media(() => {const state = readyState(); state.outputs[0].configRevision = ++calls; return state})
  assert.equal((await new Coordinator(plan, hub, changing).take()).state, 'blocked')
  assert.equal(hub.writes.length, 0)
})
test('group interruption reports partial and never rolls back completed paths', async () => {
  const hub = new FakeHub(); hub.afterWrite = () => hub.locks.set(34, 'L')
  const result = await new Coordinator(plan, hub, media()).take()
  assert.equal(result.state, 'partial'); assert.deepEqual(result.confirmed, [1])
  assert.deepEqual(hub.writes, [[33, 8]]); assert.equal(hub.routes.get(33), 8)
})
test('return refuses intervening manual routes, even if an operator later routes back', async () => {
  const hub = new FakeHub(); const coordinator = new Coordinator(plan, hub, media())
  await coordinator.take(); hub.manual(33, 20); hub.manual(33, 8)
  const result = await coordinator.take(true)
  assert.equal(result.state, 'blocked'); assert.equal(hub.writes.length, 2)
})
test('return refuses connection-epoch changes and startup has no return ownership', async () => {
  const hub = new FakeHub(); const coordinator = new Coordinator(plan, hub, media())
  assert.equal((await coordinator.take(true)).state, 'blocked')
  await coordinator.take(); ++hub.epoch
  assert.equal((await coordinator.take(true)).state, 'blocked')
  assert.equal(hub.writes.length, 2)
})
test('connection loss after write is unknown, never a fabricated success', async () => {
  const hub = new FakeHub(); hub.route = async () => {throw new Error('connection lost')}
  const result = await new Coordinator(plan, hub, media()).take()
  assert.equal(result.state, 'unknown'); assert.deepEqual(result.confirmed, [])
})

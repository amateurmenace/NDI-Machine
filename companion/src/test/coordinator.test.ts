import { test } from 'node:test'
import assert from 'node:assert/strict'
import { performance } from 'node:perf_hooks'
import { Coordinator, readiness, routeMode, takeReadiness, toggleReadiness, validatePlan, type Media, type MediaState, type Plan } from '../coordinator.js'
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
test('audio preparation blocks video writes; audio executes only after complete video confirmation', async () => {
  const hub = new FakeHub(); const calls: string[] = []
  const audio = {prepare: async (target: string) => {calls.push(`prepare:${target}`); return async () => {
    assert.equal(routeMode(plan, await hub.snapshot()), target)
    calls.push(`apply:${target}`); return 'audio confirmed'
  }}}
  const coordinator = new Coordinator(plan, hub, media(), audio)
  assert.equal((await coordinator.toggle()).state, 'confirmed')
  assert.equal((await coordinator.toggle()).state, 'confirmed')
  assert.deepEqual(calls, ['prepare:NETWORK','apply:NETWORK','prepare:FIBER','apply:FIBER'])
  const blocked = new FakeHub()
  assert.equal((await new Coordinator(plan, blocked, media(), {prepare: async () => {throw new Error('ATEM offline')}}).toggle()).state, 'blocked')
  assert.equal(blocked.writes.length, 0)
})
test('partial video never runs audio; audio failure reports partial without reversing video', async () => {
  let applied = 0
  const audio = {prepare: async () => async () => {applied++; throw new Error('audio failed')}}
  const partial = new FakeHub(); partial.afterWrite = () => partial.locks.set(34, 'L')
  assert.equal((await new Coordinator(plan, partial, media(), audio).toggle()).state, 'partial')
  assert.equal(applied, 0)
  const hub = new FakeHub(); const result = await new Coordinator(plan, hub, media(), audio).toggle()
  assert.equal(result.state, 'partial'); assert.match(result.reason, /audio failed/)
  assert.equal(applied, 1); assert.deepEqual(hub.writes, [[33,8],[34,6]])
})
test('busy lock covers audio execution and final route verification catches intervening video changes', async () => {
  const hub = new FakeHub(); let concurrent: string | undefined
  const coordinator = new Coordinator(plan, hub, media(), {prepare: async () => async () => {
    concurrent = (await coordinator.toggle()).state
    hub.manual(33, 9)
    return 'audio applied'
  }})
  const result = await coordinator.toggle()
  assert.equal(concurrent, 'blocked'); assert.equal(result.state, 'partial'); assert.match(result.reason, /routes changed/)
  assert.deepEqual(result.confirmed, [2])
})
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

test('toggle takes network, returns fiber, then takes network using actual route readback', async () => {
  const hub = new FakeHub(); const coordinator = new Coordinator(plan, hub, media())
  assert.equal(routeMode(plan, await hub.snapshot()), 'FIBER')
  for (const target of ['NETWORK', 'FIBER', 'NETWORK']) {
    const result = await coordinator.toggle()
    assert.equal(result.state, 'confirmed'); assert.equal(result.target, target)
    assert.equal(routeMode(plan, await hub.snapshot()), target)
  }
  assert.deepEqual(hub.writes, [[33, 8], [34, 6], [33, 9], [34, 10], [33, 8], [34, 6]])
})

test('toggle starts from already-selected network and returns fiber without media API or prior take ownership', async () => {
  const hub = new FakeHub(); hub.manual(33, 8); hub.manual(34, 6)
  let mediaCalls = 0
  const offline: Media = {state: async () => {++mediaCalls; throw new Error('offline')}, prepare: async () => ''}
  assert.equal(toggleReadiness(plan, await hub.snapshot(), 'offline'), undefined)
  const result = await new Coordinator(plan, hub, offline).toggle()
  assert.equal(result.state, 'confirmed'); assert.equal(result.target, 'FIBER'); assert.equal(mediaCalls, 0)
  assert.deepEqual(hub.writes, [[33, 9], [34, 10]])
  assert.equal(toggleReadiness(plan, await hub.snapshot(), 'offline'), 'offline')
  assert.equal((await new Coordinator(plan, hub, offline).toggle()).state, 'blocked')
  assert.equal(hub.writes.length, 2)
})

test('mixed, unrelated, locked and stale routes block toggle without advancing anything', async () => {
  const hub = new FakeHub(); const coordinator = new Coordinator(plan, hub, media())
  hub.manual(33, 8)
  assert.match(toggleReadiness(plan, await hub.snapshot())!, /Mixed/)
  assert.equal((await coordinator.toggle()).state, 'blocked')
  hub.manual(33, 20); assert.equal((await coordinator.toggle()).state, 'blocked')
  hub.manual(33, 9); hub.locks.set(34, 'L'); assert.equal((await coordinator.toggle()).state, 'blocked')
  hub.locks.set(34, 'U'); const fresh = hub.snapshot.bind(hub)
  hub.snapshot = async () => ({...await fresh(), sampledAt: performance.now() - 2000})
  assert.equal((await coordinator.toggle()).state, 'blocked'); assert.deepEqual(hub.writes, [])
})

test('failed network readiness keeps fiber and next valid press still takes network', async () => {
  const hub = new FakeHub(); const state = readyState(); state.outputs[0].fallback = true
  const coordinator = new Coordinator(plan, hub, media(() => state))
  assert.equal((await coordinator.toggle()).state, 'blocked'); assert.deepEqual(hub.writes, [])
  state.outputs[0].fallback = false
  assert.equal((await coordinator.toggle()).target, 'NETWORK')
  assert.deepEqual(hub.writes, [[33, 8], [34, 6]])
})

test('manual change during toggle return produces partial outcome and no automatic rollback or next-step flip', async () => {
  const hub = new FakeHub(); hub.manual(33, 8); hub.manual(34, 6)
  hub.afterWrite = () => hub.manual(34, 20)
  const coordinator = new Coordinator(plan, hub, media())
  const result = await coordinator.toggle()
  assert.equal(result.state, 'partial'); assert.equal(result.target, 'FIBER')
  assert.deepEqual(hub.writes, [[33, 9]])
  assert.equal((await coordinator.toggle()).state, 'blocked'); assert.equal(hub.writes.length, 1)
})

test('overlapping toggle presses cannot execute an opposite operation in flight', async () => {
  const hub = new FakeHub(); const coordinator = new Coordinator(plan, hub, media())
  const first = coordinator.toggle(); const second = await coordinator.toggle()
  assert.equal(second.state, 'blocked'); assert.match(second.reason, /already running/)
  assert.equal((await first).target, 'NETWORK'); assert.deepEqual(hub.writes, [[33, 8], [34, 6]])
})

test('toggle return blocks a reconnect between decision and write', async () => {
  const hub = new FakeHub(); hub.manual(33, 8); hub.manual(34, 6)
  const snapshot = hub.snapshot.bind(hub); let calls = 0
  hub.snapshot = async () => { if (++calls === 2) ++hub.epoch; return snapshot() }
  const result = await new Coordinator(plan, hub, media()).toggle()
  assert.equal(result.state, 'blocked'); assert.deepEqual(hub.writes, [])
})

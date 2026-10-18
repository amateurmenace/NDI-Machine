import { test } from 'node:test'
import assert from 'node:assert/strict'
import { createServer } from 'node:http'
import { audioPlan, CompanionZoom, EmergencyAudio, OFF, ON, type AudioMixer, type AudioSnapshot, type AudioTarget, type ZoomButtons } from '../audio.js'

function snapshot(): AudioSnapshot {
  const inputs = [
    ...Array.from({length: 8}, (_, i) => [i + 1, 1]), [1001, 32], [1201, 128], [1301, 512],
    ...Array.from({length: 16}, (_, i) => [1501 + i, 1024]), [2001, 256], [2002, 256],
  ]
  return {connected: true, epoch: 1, model: 27, sources: inputs.map(([input, port]) => ({input, port, source: input === 1301 ? '-256' : '-65280', mix: ON, supported: [OFF, ON, 4]}))}
}
class Mixer implements AudioMixer {
  state = snapshot()
  writes: {input: number; source: string; mix: number}[] = []
  afterWrite?: () => void
  snapshot(): AudioSnapshot { return structuredClone(this.state) }
  async setMix(input: number, source: string, mix: number): Promise<void> {
    this.writes.push({input, source, mix})
    this.state.sources.find(s => s.input === input && s.source === source)!.mix = mix
    this.afterWrite?.()
  }
}
class Buttons implements ZoomButtons {
  presses: AudioTarget[] = []
  online = true
  async check(): Promise<void> { if (!this.online) throw new Error('Zoom offline') }
  async press(target: AudioTarget): Promise<void> { this.presses.push(target) }
  async selected(): Promise<AudioTarget> { return this.presses.at(-1) ?? 'FIBER' }
}
test('network enables only Input 1; fiber only XLR and MADI 1/2 + 3/4 across all 29 inputs', () => {
  const state = snapshot()
  assert.equal(state.sources.length, 29)
  assert.deepEqual(audioPlan(state, 'NETWORK').filter(s => s.mix === ON).map(s => s.input), [1])
  assert.deepEqual(audioPlan(state, 'FIBER').filter(s => s.mix === ON).map(s => s.input), [1001, 1501, 1502])
  assert.ok(audioPlan(state, 'FIBER').filter(s => ![1001, 1501, 1502].includes(s.input)).every(s => s.mix === OFF))
})

test('audio undo restores exact prior mixed On/Off/AFV options and previous Zoom selection', async () => {
  const mixer = new Mixer(); const buttons = new Buttons(); const audio = new EmergencyAudio(mixer, buttons)
  mixer.state.sources[3].mix = 4; mixer.state.sources[5].mix = OFF
  const original = mixer.snapshot(); const undo = await audio.captureUndo()
  await (await audio.prepare('NETWORK'))()
  const restore = await undo(); await restore()
  assert.deepEqual(mixer.state.sources, original.sources)
  assert.deepEqual(buttons.presses, ['NETWORK','FIBER'])
  await assert.rejects(restore(), /replayed/)
})
test('wrong model, disconnected, missing, mismatched port and unsupported audio block before dispatch', async () => {
  for (const mutate of [
    (s: AudioSnapshot) => {s.model = 1}, (s: AudioSnapshot) => {s.connected = false},
    (s: AudioSnapshot) => {s.sources = s.sources.filter(x => x.input !== 1516)},
    (s: AudioSnapshot) => {s.sources.find(x => x.input === 1501)!.port = 32},
    (s: AudioSnapshot) => {s.sources[0].supported = [OFF]},
  ]) {
    const mixer = new Mixer(); const buttons = new Buttons(); mutate(mixer.state)
    await assert.rejects(new EmergencyAudio(mixer, buttons).prepare('NETWORK'))
    assert.equal(mixer.writes.length, 0); assert.equal(buttons.presses.length, 0)
  }
})
test('prepared audio has no side effects; network and fiber invoke the corresponding Zoom button once', async () => {
  const mixer = new Mixer(); const buttons = new Buttons(); const audio = new EmergencyAudio(mixer, buttons)
  const network = await audio.prepare('NETWORK')
  assert.equal(mixer.writes.length, 0); assert.equal(buttons.presses.length, 0)
  assert.match(await network(), /ATEM NETWORK audio confirmed/)
  assert.equal(audio.profile(), 'NETWORK')
  const fiber = await audio.prepare('FIBER'); mixer.writes = []
  await fiber(); assert.equal(audio.profile(), 'FIBER')
  assert.deepEqual(buttons.presses, ['NETWORK', 'FIBER'])
  assert.deepEqual(mixer.writes.map(w => [w.input, w.mix]), [[1, OFF], [1001, ON], [1501, ON], [1502, ON]])
  await assert.rejects(fiber(), /replayed/)
  assert.equal(buttons.presses.length, 2)
})
test('split mono sources are all covered without changing channel configuration', async () => {
  const mixer = new Mixer(); mixer.state.sources = mixer.state.sources.flatMap(s => s.input === 1 ? [{...s, source: '-256'}, {...s, source: '-255'}] : [s])
  const before = mixer.state.sources.map(s => [s.input, s.source, s.port])
  await (await new EmergencyAudio(mixer, new Buttons()).prepare('FIBER'))()
  assert.ok(mixer.state.sources.filter(s => s.input === 1).every(s => s.mix === OFF))
  assert.deepEqual(mixer.state.sources.map(s => [s.input, s.source, s.port]), before)
})
test('manual audio changes or reconnect between prepare and routing completion stop dispatch', async () => {
  for (const reconnect of [true, false]) {
    const mixer = new Mixer(); const buttons = new Buttons()
    const apply = await new EmergencyAudio(mixer, buttons).prepare('NETWORK')
    if (reconnect) mixer.state.epoch++; else mixer.state.sources[0].mix = OFF
    await assert.rejects(apply(), /changed|reconnected/)
    assert.equal(buttons.presses.length, 0); assert.equal(mixer.writes.length, 0)
  }
})
test('failed Zoom dispatch does not mute ATEM; it is not retried', async () => {
  const mixer = new Mixer(); const buttons = new Buttons()
  buttons.press = async target => { buttons.presses.push(target); throw new Error('dispatch timeout') }
  const apply = await new EmergencyAudio(mixer, buttons).prepare('NETWORK')
  await assert.rejects(apply(), /timeout/)
  assert.deepEqual(buttons.presses, ['NETWORK']); assert.equal(mixer.writes.length, 0)
})
test('manual changes during ATEM writes abort with no global rollback', async () => {
  const mixer = new Mixer(); const buttons = new Buttons()
  mixer.afterWrite = () => {mixer.state.sources.find(s => s.input === 1516)!.mix = OFF}
  await assert.rejects((await new EmergencyAudio(mixer, buttons).prepare('NETWORK'))(), /changed/)
  assert.equal(mixer.writes.length, 1); assert.deepEqual(buttons.presses, ['NETWORK'])
})
test('HTTP bridge uses the exact existing button locations, validates connections and never retries a failed press', async () => {
  const requests: string[] = []; let fail = false
  const server = createServer((req, res) => {
    requests.push(`${req.method} ${req.url}`)
    if (req.method === 'GET') {res.setHeader('content-type', 'application/json'); res.end(JSON.stringify(['x32','zoomswitch'].map(label => ({label,enabled:true,status:{level:'ok'}}))))}
    else {res.statusCode = fail ? 500 : 200; res.end('ok')}
  })
  await new Promise<void>(resolve => server.listen(0, '127.0.0.1', resolve))
  try {
    const port = (server.address() as {port: number}).port
    const bridge = new CompanionZoom(`http://127.0.0.1:${port}`, '1/1/3', '1/1/2')
    await bridge.check(); await bridge.press('NETWORK'); await bridge.press('FIBER')
    fail = true; await assert.rejects(bridge.press('NETWORK'), /not retried/)
    assert.deepEqual(requests, ['GET /api/connections','POST /api/location/1/1/3/press','POST /api/location/1/1/2/press','POST /api/location/1/1/3/press'])
    assert.throws(() => new CompanionZoom('http://example.com', '1/1/3', '1/1/2'), /localhost/)
    assert.throws(() => new CompanionZoom('http://localhost', '1/1/0', '1/1/2'), /fail-over/)
  } finally {server.closeAllConnections(); await new Promise<void>(resolve => server.close(() => resolve()))}
})

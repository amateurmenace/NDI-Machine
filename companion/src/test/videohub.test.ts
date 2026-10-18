import { test } from 'node:test'
import assert from 'node:assert/strict'
import { createServer, type Socket } from 'node:net'
import { Blocks, Videohub } from '../videohub.js'
test('incremental CRLF/LF parsing accepts fragments, unknown blocks, and bounds memory', () => {
  const parser = new Blocks(); const blocks: string[][] = []
  for (const character of 'UNKNOWN:\r\nabc\r\n\r\nACK\n\nVIDEO OUTPUT ROUTING:\n32 7\n\n') blocks.push(...parser.feed(character))
  assert.deepEqual(blocks, [['UNKNOWN:', 'abc'], ['ACK'], ['VIDEO OUTPUT ROUTING:', '32 7']])
  assert.throws(() => parser.feed('X'.repeat(262145)), /limit/)
})
async function mockHub(ackOnly = false) {
  const peers = new Set<Socket>(); const writes: string[] = []; let source = 8
  const server = createServer(socket => {
    peers.add(socket); socket.on('close', () => peers.delete(socket))
    const parser = new Blocks()
    socket.write(`PROTOCOL PREAMBLE:\nVersion: 2.8\n\nVIDEOHUB DEVICE:\nDevice present: true\nVideo inputs: 40\nVideo outputs: 1\n\nVIDEO OUTPUT ROUTING:\n0 ${source}\n\nVIDEO OUTPUT LOCKS:\n0 U\n\nTAKE MODE:\n0 true\n\nEND PRELUDE:\n\n`)
    socket.on('data', chunk => {
      for (const [header, ...lines] of parser.feed(chunk.toString())) {
        if (header === 'VIDEO OUTPUT ROUTING:') {
          if (lines.length) { writes.push(lines.join('\n')); source = Number(lines[0].split(' ')[1]) }
          socket.write('ACK\n\n')
          if (!lines.length || !ackOnly) socket.write(`VIDEO OUTPUT ROUTING:\n0 ${source}\n\n`)
        } else if (header === 'VIDEO OUTPUT LOCKS:') socket.write('ACK\n\nVIDEO OUTPUT LOCKS:\n0 U\n\n')
      }
    })
  })
  await new Promise<void>(resolve => server.listen(0, '127.0.0.1', resolve))
  const address = server.address(); assert.ok(address && typeof address !== 'string')
  return {port: address.port, writes, close: async () => {
    for (const socket of peers) socket.destroy()
    await new Promise<void>(resolve => server.close(() => resolve()))
  }}
}
test('TCP adapter converts one-based ports once and demands crosspoint readback', async () => {
  const server = await mockHub(); const hub = new Videohub('127.0.0.1', server.port, 300)
  try {
    const state = await hub.snapshot(); assert.equal(state.routes.get(1), 9)
    await hub.route(1, 8, 9, state.epoch)
    assert.deepEqual(server.writes, ['0 7'])
    assert.equal((await hub.snapshot()).routes.get(1), 8)
  } finally { hub.close(); await server.close() }
})
test('ACK without readback times out and reconnect never replays a write', async () => {
  const server = await mockHub(true); const hub = new Videohub('127.0.0.1', server.port, 100)
  try {
    const state = await hub.snapshot()
    await assert.rejects(hub.route(1, 8, 9, state.epoch), /unknown/)
    await hub.snapshot()
    assert.deepEqual(server.writes, ['0 7'])
  } finally { hub.close(); await server.close() }
})

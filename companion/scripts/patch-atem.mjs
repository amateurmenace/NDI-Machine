// atem-connection 3.10.3 can schedule a UDP ACK from a late message while
// _closeSocket is closing, after its first ACK timer cleanup. Detach the old
// socket's message listener first. Pin and verify the patch instead of silently
// applying it to a different dependency version.
import { readFileSync, writeFileSync } from 'node:fs'
const root = new URL(process.argv[2] === '--package' ? '../pkg/ndi-machine/node_modules/atem-connection/' : '../node_modules/atem-connection/', import.meta.url)
const version = JSON.parse(readFileSync(new URL('package.json', root), 'utf8')).version
if (version !== '3.10.3') throw new Error('Review the ATEM socket shutdown patch for the new dependency version')
const path = new URL('dist/lib/atemSocketChild.js', root)
const before = '    async _closeSocket() {\n'
const after = before + '        this._socket.removeAllListeners("message"); // NDI Machine: prevent late ACK after close\n'
const text = readFileSync(path, 'utf8').replace(/\r\n/g, '\n')
if (!text.includes(after)) {
  if (text.split(before).length !== 2) throw new Error('ATEM socket shutdown patch target changed')
  writeFileSync(path, text.replace(before, after))
}
// Audio control never renders multiview labels. Defer the optional native font
// renderer so this module needs no native-addon or broad filesystem permission.
const atemPath = new URL('dist/atem.js', root)
const eager = 'const multiviewLabel_1 = require("./lib/multiviewLabel");'
const lazy = 'const multiviewLabel_1 = new Proxy({}, { get: (_target, key) => require("./lib/multiviewLabel")[key] });'
const atemText = readFileSync(atemPath, 'utf8')
if (!atemText.includes(lazy)) {
  if (atemText.split(eager).length !== 2) throw new Error('ATEM optional font import patch target changed')
  writeFileSync(atemPath, atemText.replace(eager, lazy))
}

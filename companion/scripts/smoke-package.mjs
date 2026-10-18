// Import the actual packaged artifact under the runtime permission model. No
// connect() or hardware writes: this catches missing external/native assets.
await import('../pkg/ndi-machine/main.js')
const { default: connection } = await import('../pkg/ndi-machine/node_modules/atem-connection/dist/index.js')
const atem = new connection.Atem({disableMultithreaded: true})
await atem.destroy()
console.log('Packaged module import and ATEM construction passed (no hardware connection)')

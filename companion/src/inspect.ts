// Read-only connection proof. No route method is called from this executable.
import { Videohub } from './videohub.js'
const host = process.argv[2]
if (!host) throw new Error('Usage: node dist/inspect.js commissioned-videohub-ip')
const hub = new Videohub(host)
try {
  const state = await hub.snapshot()
  console.log(JSON.stringify({epoch: state.epoch, inputs: state.inputs, outputs: state.outputs,
    protectedPaths: [33, 34, 35, 36].map(destination => ({destination, input: state.routes.get(destination), lock: state.locks.get(destination)})),
    routeWrites: 0, physicalSignalVerified: 'Unknown'}, null, 2))
} finally { hub.close() }

import { randomUUID } from 'node:crypto'
import type { Plan } from './coordinator.js'

export type LaunchState = {schemaVersion: number; nonce: string; desktopRunning: boolean; controlRunning: boolean}
export class LocalLauncher {
  constructor(private base='http://127.0.0.1:9861') {
    const url=new URL(base)
    if(!['127.0.0.1','localhost'].includes(url.hostname) || url.protocol!=='http:' || url.username || url.password)throw new Error('Launcher must be localhost HTTP')
  }
  async state(): Promise<LaunchState> {
    const nonce=randomUUID()
    const response=await fetch(new URL(`/api/v1/state?nonce=${nonce}`,this.base),{signal:AbortSignal.timeout(1200),cache:'no-store'})
    if(!response.ok)throw new Error(`Launcher HTTP ${response.status}`)
    const state=await response.json() as LaunchState
    if(state.schemaVersion!==1 || state.nonce!==nonce || typeof state.desktopRunning!=='boolean' || typeof state.controlRunning!=='boolean')throw new Error('Invalid fresh launcher state')
    return state
  }
  async open():Promise<void> {
    const response=await fetch(new URL('/api/v1/prime',this.base),{method:'POST',signal:AbortSignal.timeout(3000)})
    if(!response.ok)throw new Error(`App launch failed: HTTP ${response.status}`)
  }
}
// Deliberately has no router/audio-transition interface. Priming cannot take.
export async function primeOnly(launcher:{open():Promise<void>;state():Promise<LaunchState>},
  media:{state():Promise<unknown>;prepare(plan:Plan):Promise<string>},plan:Plan,
  wait:()=>Promise<void>=()=>new Promise(resolve=>setTimeout(resolve,500))):Promise<string> {
  await launcher.open()
  for(let attempt=0;attempt<40;attempt++) {
    let connected=false
    try {const state=await launcher.state();if(state.desktopRunning && state.controlRunning){await media.state();connected=true}}
    catch(error){if(attempt===39)throw error}
    if(connected)return await media.prepare(plan) // A mutation is sent once, outside the retry loop.
    await wait()
  }
  throw new Error('App/engine startup was not confirmed; no route change')
}

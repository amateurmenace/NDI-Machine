import test from 'node:test'
import assert from 'node:assert/strict'
import {primeOnly} from '../prime.js'
import {HoldGesture} from '../hold.js'
import type {Plan} from '../coordinator.js'
const plan={schemaVersion:1,name:'test',audioEndpointId:'usb',paths:[]} as Plan
test('prime opens app, waits for connection, and prepares once without a router interface',async()=>{
  const events:string[]=[];let polls=0
  const launcher={open:async()=>{events.push('open')},state:async()=>({schemaVersion:1,nonce:'test',desktopRunning:++polls>1,controlRunning:true})}
  const media={state:async()=>{events.push('connected')},prepare:async()=>{events.push('prepare');return 'operation'}}
  assert.equal(await primeOnly(launcher,media,plan,async()=>{}),'operation')
  assert.deepEqual(events,['open','connected','prepare'])
})
test('lost preparation ACK is not retried by priming',async()=>{
  let prepares=0
  const launcher={open:async()=>{},state:async()=>({schemaVersion:1,nonce:'test',desktopRunning:true,controlRunning:true})}
  await assert.rejects(primeOnly(launcher,{state:async()=>{},prepare:async()=>{prepares++;throw new Error('ACK lost')}},plan,async()=>{}))
  assert.equal(prepares,1)
})
test('prime gesture is latched on down even if app connects before release or the button is held',()=>{
  let now=0;const gesture=new HoldGesture(()=>now)
  gesture.down('button',true);now=4000;assert.equal(gesture.up('button'),'prime')
  gesture.down('button',false);now=4100;assert.equal(gesture.up('button'),'toggle')
})

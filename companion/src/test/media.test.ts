import {test} from 'node:test'
import assert from 'node:assert/strict'
import {HttpMedia,type MediaState} from '../coordinator.js'

const snapshot:MediaState={schemaVersion:1,nonce:'test',instanceId:'host-a',outputs:[{
  slot:1,deviceId:'0xa1',workerInstanceId:'worker-a',source:'Production',backend:'hardware',running:true,configRevision:7,
  committedOperationId:'seven',videoAgeMs:0,scheduleAgeMs:0,fallback:false,format:'1920x1080p30000/1001',health:'healthy',
  audio:{endpointId:'',channels:[1,2],mode:'silent',ageMs:0,muted:true},
  // Native settings omit device; it is reported separately as deviceId.
  settings:{source:'Production',audioMode:'silent',gainDb:-3,bandwidth:'lowest'},
}]}
test('HTTP undo captures actual production device and settings, with current revisions and one mutation',async()=>{
  const original=globalThis.fetch;let writes=0;let body:Record<string,any>|undefined
  globalThis.fetch=async(url,options)=>{
    const parsed=new URL(String(url))
    if(parsed.pathname==='/api/v1/state')return Response.json({...snapshot,nonce:parsed.searchParams.get('nonce'),outputs:snapshot.outputs.map(o=>({...o,configRevision:9}))})
    if(parsed.pathname==='/api/v1/outputs/restore'){writes++;body=JSON.parse(String(options?.body));return Response.json({state:'accepted'})}
    return Response.json({state:'committed',instanceId:'host-a'})
  }
  try{
    const restore=await new HttpMedia('http://127.0.0.1:9860').captureUndo(snapshot)
    assert.equal(writes,0);await restore();assert.equal(writes,1)
    assert.equal(body?.outputs['1'].device,'0xa1');assert.equal(body?.outputs['1'].source,'Production')
    assert.equal(body?.outputs['1'].bandwidth,'lowest');assert.equal(body?.outputs['1'].gainDb,-3)
    assert.equal(body?.expectedWorkers['1'].expectedRevision,9)
  }finally{globalThis.fetch=original}
})
test('HTTP undo refuses a replaced host and never retries a lost restore acknowledgement',async()=>{
  const original=globalThis.fetch;let writes=0;let host='replacement'
  globalThis.fetch=async(url)=>{
    const parsed=new URL(String(url))
    if(parsed.pathname==='/api/v1/state')return Response.json({...snapshot,nonce:parsed.searchParams.get('nonce'),instanceId:host})
    writes++;throw new Error('lost acknowledgement')
  }
  try{
    const restore=await new HttpMedia('http://127.0.0.1:9860').captureUndo(snapshot)
    await assert.rejects(restore,/host changed/);assert.equal(writes,0)
    host='host-a';await assert.rejects(restore,/lost acknowledgement/);assert.equal(writes,1)
  }finally{globalThis.fetch=original}
})

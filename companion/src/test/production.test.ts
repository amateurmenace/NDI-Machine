import {test} from 'node:test'
import assert from 'node:assert/strict'
import {visibleNdi,selectWorker,validateMap,ProductionRouter,type Source} from '../production.js'
import type {MediaState} from '../coordinator.js'
import type {HubSnapshot,Router} from '../videohub.js'
const map=validateMap(JSON.stringify({normalInputs:[9,10,11,12,18,17,33,34],outputs:[{device:'0xc0',input:8},{device:'0xc1',input:6},{device:'0xc2',input:7},{device:'0xc3',input:5}]}))
const source:Source={kind:'ndi',id:'Panasonic',name:'Panasonic',endpoint:'NDI-HX-PANA-PTZ-V1://10.0.0.2'}
const state={instanceId:'host',outputs:[0,1,2,3].map((n)=>({slot:n+1,deviceId:'0xc'+n,running:true,source:'Old',health:'healthy',fallback:false,videoAgeMs:0,scheduleAgeMs:0,settings:{sourceUrl:''}}))} as unknown as MediaState
const hub=()=>({epoch:1,routes:new Map([[33,6],[34,7],[35,8],[36,5],[31,6]]),locks:new Map(Array.from({length:8},(_,i)=>[33+i,'U'])),changes:new Map(),inputs:40,outputs:40,sampledAt:performance.now()}) as HubSnapshot
test('NDI directory filters only the specified camera ranges and channel suffixes',()=>{
 for(const name of ['A Cam 1','a_cam-3 (Chan 1)','SB CAM 4 (Channel 1)','AUD CAM 2 (Chan 1)','sc cam 3','E_CAM_1'])assert.equal(visibleNdi(name),false,name)
 for(const name of ['A Cam 4','SB Cam 5','AUD Cam 5','Panasonic AG-2 Cam (Chan 1)','Zoom','A Camera 1'])assert.equal(visibleNdi(name),true,name)
})
test('allocation uses physical connector map and protects every unselected destination',()=>{
 assert.throws(()=>selectWorker(map,state,hub(),source,[1]),/other destinations/)
 assert.equal(selectWorker(map,state,hub(),source,[3]).input,8)
 assert.equal(selectWorker(map,state,hub(),source,[2]).output.slot,3)
})
test('one identical healthy NDI receiver can feed more destinations without retuning it',()=>{
 const current=structuredClone(state);Object.assign(current.outputs[1],{source:source.name,settings:{sourceUrl:source.endpoint}})
 assert.equal(selectWorker(map,current,hub(),source,[4]).reuse,true)
 current.outputs[1].fallback=true;assert.equal(selectWorker(map,current,hub(),source,[4]).reuse,false)
})
test('invalid or ambiguous physical maps are rejected',()=>{
 assert.throws(()=>validateMap('{}'));assert.throws(()=>validateMap(JSON.stringify({...map,outputs:[map.outputs[0],...map.outputs.slice(0,3)]})))
})
test('GUI ATEM labels follow the physical connector map, not the media slot number',()=>{
 const plan={paths:[{destination:33,backupInput:6},{destination:34,backupInput:7},{destination:35,backupInput:8},{destination:36,backupInput:5}]} as any
 const p=new ProductionRouter('http://127.0.0.1:9860','192.0.2.1',map,{snapshot:async()=>hub(),route:async()=>{}},plan);p.media=state;p.hub=hub()
 const mapping=p.outputMappings() as {slot:number;atemInput:number}[]
 assert.deepEqual(mapping.map(o=>[o.slot,o.atemInput]),[[1,3],[2,1],[3,2],[4,4]])
})
test('directory selection is passive and separate for both decks; cloud chooses one input',()=>{
 const router:Router={snapshot:async()=>{throw new Error('No I/O during selection')},route:async()=>{throw new Error('No writes during selection')}}
 const p=new ProductionRouter('http://127.0.0.1:9860','192.0.2.1',map,router);p.sources=[source,{kind:'cloud',id:'5',name:'Cloud 1'}]
 p.edit('mk','source',0);p.edit('mk','destination',3);assert.equal(p.views.xl.source,undefined);assert.deepEqual([...p.views.mk.destinations],[3])
 p.edit('xl','cloud');p.edit('xl','source',0);p.edit('xl','destination',1);p.edit('xl','destination',4);assert.deepEqual([...p.views.xl.destinations],[4])
 p.edit('mk','sdi');assert.equal(p.views.mk.destinations.size,8);p.edit('mk','cancel');assert.equal(p.views.mk.destinations.size,0)
})

test('TAKE shares a background scan, retries only discovery busy, and sends the physical device identity',async t=>{
 const current=structuredClone(state),snapshot=hub(),events:string[]=[];let scans=0,release!:()=>void
 const waiting=new Promise<void>(r=>release=r)
 const atem={epoch:'atem',sources:[],inputs:Array.from({length:8},(_,i)=>({id:i+1}))}
 t.mock.method(globalThis,'fetch',async (url:URL|string,options?:RequestInit)=>{
  const path=new URL(url),body=options?.body?JSON.parse(String(options.body)):undefined
  if(path.pathname.endsWith('/atem')){if(body.method==='input')events.push('atem');return Response.json(atem)}
  if(path.pathname.endsWith('/sources')){scans++;if(scans===1){await waiting;return Response.json({error:'Source discovery is already running'},{status:409})}return Response.json({sources:[source]})}
  if(path.pathname.endsWith('/state'))return Response.json({...current,nonce:path.searchParams.get('nonce')})
  if(path.pathname.endsWith('/settings')){assert.equal(body.settings.device,'0xc0');assert.equal(body.settings.sourceUrl,source.endpoint);assert.equal(body.settings.audioMode,'ndi');events.push('prepare');Object.assign(current.outputs[0],{source:source.name,settings:body.settings});return Response.json({operationId:'op'})}
  if(path.pathname.includes('/operations/')){events.push('commit');return Response.json({state:'committed',instanceId:'host'})}
  throw new Error('Unexpected request '+url)
 })
 const router:Router={snapshot:async()=>snapshot,route:async(d,s)=>{events.push('route');snapshot.routes.set(d,s)}}
 const p=new ProductionRouter('http://127.0.0.1:9860','192.0.2.1',map,router);p.sources=[source]
 await p.refresh(snapshot,current);p.edit('mk','source',0);p.edit('mk','destination',3)
 const take=p.take('mk');await new Promise(r=>setTimeout(r,10));release();await take
 assert.equal(scans,2);assert.deepEqual(events,['prepare','commit','route','atem']);assert.match(p.message,/Confirmed/)
 p.message='CHECK: Something failed';assert.match(p.values('mk').mk_take,/RETRY/);assert.match(p.values('mk').mk_health,/TAKE FAILED/)
 assert.match(p.values('mk').mk_home_ndi,/3: Panasonic/)
})

test('failed prepare never changes Videohub or ATEM inputs and is not replayed',async t=>{
 let prepares=0,writes=0
 t.mock.method(globalThis,'fetch',async(url:URL|string,options?:RequestInit)=>{
  const path=new URL(url),body=options?.body?JSON.parse(String(options.body)):undefined
  if(path.pathname.endsWith('/atem')){if(body.method==='input')writes++;return Response.json({epoch:'atem',sources:[],inputs:[{id:3}]})}
  if(path.pathname.endsWith('/sources'))return Response.json({sources:[source]})
  if(path.pathname.endsWith('/state'))return Response.json({...state,nonce:path.searchParams.get('nonce')})
  if(path.pathname.endsWith('/settings')){prepares++;return Response.json({})}
  if(path.pathname.includes('/operations/'))return Response.json({state:'failed',instanceId:'host',error:'Sender not ready'})
  throw new Error('Unexpected request')
 })
 const p=new ProductionRouter('http://127.0.0.1:9860','192.0.2.1',map,{snapshot:async()=>hub(),route:async()=>{writes++}})
 p.sources=[source];p.edit('mk','source',0);p.edit('mk','destination',3)
 await assert.rejects(p.take('mk'),/Sender not ready/);assert.equal(prepares,1);assert.equal(writes,0)
})

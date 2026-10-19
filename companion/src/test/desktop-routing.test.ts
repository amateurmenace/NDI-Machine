import {test,type TestContext} from 'node:test'
import assert from 'node:assert/strict'
import {createServer} from 'node:http'
import {randomUUID} from 'node:crypto'
import {ProductionRouter,validateMap,type DesktopRouteRequest} from '../production.js'
import {DesktopRoutingApi} from '../desktop-api.js'
import type {MediaState} from '../coordinator.js'
import type {HubSnapshot,Router} from '../videohub.js'

function fixture(t:TestContext){
 const normal=[9,10,11,12,18,17,33,34]
 const map=validateMap(JSON.stringify({normalInputs:normal,outputs:[{device:'0xc0',input:8},{device:'0xc1',input:6},{device:'0xc2',input:7},{device:'0xc3',input:5}]}))
 const state={schemaVersion:1,instanceId:'host',outputs:[{slot:2,deviceId:'0xc1',running:true,source:'Camera',backend:'hardware',format:'1920x1080p30000/1001',health:'healthy',fallback:false,videoAgeMs:10,scheduleAgeMs:10,configRevision:4,committedOperationId:'old',workerInstanceId:'worker',settings:{device:'0xc1',source:'Camera',sourceUrl:'',audioMode:'silent'}}]} as unknown as MediaState
 const hub:HubSnapshot={epoch:1,routes:new Map(normal.map((s,i)=>[33+i,s])),locks:new Map(normal.map((_,i)=>[33+i,'U'])),changes:new Map(),inputs:40,outputs:40,sampledAt:performance.now()}
 const atem={epoch:'atem',sources:[],inputs:normal.map((_,i)=>({id:i+1}))}
 const events:string[]=[];let operation='',failure='',reads=0
 const hooks={afterPrepare:()=>{},onState:(_read:number)=>{},onHub:()=>{}}
 t.mock.method(globalThis,'fetch',async(url:URL|string,options?:RequestInit)=>{
  const path=new URL(url),body=options?.body?JSON.parse(String(options.body)):undefined
  if(path.pathname.endsWith('/state')){hooks.onState(++reads);return Response.json({...state,nonce:path.searchParams.get('nonce')})}
  if(path.pathname.endsWith('/settings')){
   events.push('prepare');operation=String((options?.headers as Record<string,string>)['Idempotency-Key'])
   if(!failure)Object.assign(state.outputs[0],{source:body.settings.source,deviceId:body.settings.device,settings:body.settings,configRevision:5,committedOperationId:'native-'+operation,running:true})
   hooks.afterPrepare();return Response.json({instanceId:'host',operationId:operation})
  }
  if(path.pathname.includes('/operations/')){events.push('confirm');return Response.json({instanceId:'host',state:failure?'failed':'committed',error:failure})}
  if(path.pathname.endsWith('/atem')){if(body.method==='input')events.push('unlink:'+body.input);return Response.json(atem)}
  throw new Error('Unexpected request '+url)
 })
 const router:Router={snapshot:async()=>{hooks.onHub();return structuredClone(hub)},route:async(d,s)=>{events.push(`route:${s}:${d}`);hub.routes.set(d,s);hub.changes.set(d,(hub.changes.get(d)??0)+1)}}
 const production=new ProductionRouter('http://127.0.0.1:9860','192.0.2.1',map,router)
 const request=():DesktopRouteRequest=>({schemaVersion:1,routerInstanceId:production.instanceId,slot:2,input:3,expectedInstanceId:'host',expectedWorkerInstanceId:'worker',expectedRevision:4})
 return {production,request,state,hub,events,hooks,normal,fail:(message:string)=>{failure=message}}
}

test('desktop checkbox prepares the chosen slot then routes its physical connector to just the selected ATEM input',async t=>{
 const f=fixture(t),request=f.request();request.settings={device:'0xc1',source:'New camera',sourceUrl:'',audioMode:'silent',gainDb:-6}
 await f.production.routeOutput(request)
 assert.deepEqual(f.events,['prepare','confirm','route:6:35','unlink:3'])
 assert.equal(f.state.outputs[0].settings?.gainDb,-6)
 assert.deepEqual([...f.hub.routes.values()],[9,10,6,12,18,17,33,34])
})
test('desktop routing can prepare an idle output and does not start unrelated receivers',async t=>{
 const f=fixture(t);Object.assign(f.state.outputs[0],{running:false,workerInstanceId:undefined,configRevision:0})
 f.hooks.afterPrepare=()=>{f.state.outputs[0].workerInstanceId='new-worker'}
 await f.production.routeOutput({...f.request(),expectedWorkerInstanceId:'',expectedRevision:0,settings:{device:'0xc1',source:'New camera',sourceUrl:''}})
 assert.equal(f.events.filter(e=>e==='prepare').length,1);assert.equal(f.hub.routes.get(35),6)
})
test('route running source preserves audio settings and does not prepare or allocate another slot',async t=>{
 const f=fixture(t);await f.production.routeOutput(f.request())
 assert.deepEqual(f.events,['route:6:35','unlink:3']);assert.equal(f.state.outputs[0].settings?.audioMode,'silent')
})
test('failed prepare makes no Videohub or ATEM writes',async t=>{
 const f=fixture(t);f.fail('Sender not ready')
 await assert.rejects(f.production.routeOutput({...f.request(),settings:{device:'0xc1',source:'Missing',sourceUrl:''}}),/Sender not ready/)
 assert.deepEqual(f.events,['prepare','confirm']);assert.equal(f.hub.routes.get(35),11)
})
test('unmapped connectors, locked destinations and unselected consumers reject before preparation',async t=>{
 const f=fixture(t);let request={...f.request(),settings:{device:'0x999',source:'New',sourceUrl:''}}
 await assert.rejects(f.production.routeOutput(request),/no commissioned/)
 request.settings.device='0xc1';f.hub.locks.set(35,'L');await assert.rejects(f.production.routeOutput(request),/locked/)
 f.hub.locks.set(35,'U');f.hub.routes.set(1,6);await assert.rejects(f.production.routeOutput(request),/another destination/)
 assert.deepEqual(f.events,[])
})
test('new source readiness, output revision and router changes are checked before TAKE',async t=>{
 for(const reason of ['fallback','revision','router','session']){
  const f=fixture(t),request=f.request()
  if(reason==='fallback')f.state.outputs[0].fallback=true
  if(reason==='revision')request.expectedRevision=1
  if(reason==='session')request.routerInstanceId=randomUUID()
  if(reason==='router'){let n=0;f.hooks.onHub=()=>{if(++n===2)f.hub.routes.set(35,20)}}
  await assert.rejects(f.production.routeOutput(request));assert.deepEqual(f.events,[]);t.mock.restoreAll()
 }
})
test('an externally replaced worker after preparation cannot be routed',async t=>{
 const f=fixture(t);f.hooks.onState=n=>{if(n===3)f.state.outputs[0].workerInstanceId='replacement'}
 await assert.rejects(f.production.routeOutput({...f.request(),settings:{device:'0xc1',source:'New',sourceUrl:''}}),/changed before TAKE/)
 assert.deepEqual(f.events,['prepare','confirm'])
})
test('single return restores only the chosen input; return all restores the saved map without stopping media',async t=>{
 const f=fixture(t);f.hub.routes.set(35,8);f.hub.routes.set(36,5)
 await f.production.returnDefaults(f.production.instanceId,[3])
 assert.equal(f.hub.routes.get(35),11);assert.equal(f.hub.routes.get(36),5);assert.deepEqual(f.events,['route:11:35','unlink:3'])
 f.events.length=0;await f.production.returnDefaults(f.production.instanceId,[1,2,3,4,5,6,7,8])
 assert.deepEqual([...f.hub.routes.values()],f.normal);assert.equal(f.events.filter(e=>e.startsWith('unlink:')).length,8);assert.ok(f.state.outputs[0].running)
})
test('desktop API coalesces duplicate commands, rejects reused keys and echoes fresh state nonce',async t=>{
 // This test uses only a loopback stub. It never contacts the live module/router.
 const instance=randomUUID();let calls=0;let release!:()=>void
 const pending=new Promise<void>(resolve=>release=resolve)
 const stub={instanceId:instance,routingState:()=>({schemaVersion:1,instanceId:instance,ready:true}),routeOutput:async()=>{calls++;await pending},returnDefaults:async()=>{calls++}} as unknown as ProductionRouter
 const api=new DesktopRoutingApi(stub,()=>false,work=>work())
 const server=createServer(async(req,res)=>{if(!await api.handle(req,res))res.writeHead(404).end()})
 await new Promise<void>(resolve=>server.listen(0,'127.0.0.1',resolve))
 t.after(()=>server.close())
 const base=`http://127.0.0.1:${(server.address() as {port:number}).port}`
 const state=await (await fetch(base+'/api/v1/routing?nonce=unique')).json() as any;assert.equal(state.nonce,'unique');assert.equal(calls,0)
 const body={schemaVersion:1,routerInstanceId:instance,slot:2,input:3},id=randomUUID()
 const post=(value:unknown)=>fetch(base+'/api/v1/route-output',{method:'POST',headers:{'Content-Type':'application/json','Idempotency-Key':id},body:JSON.stringify(value)})
 const one=post(body),two=post(body);await new Promise(resolve=>setTimeout(resolve,20));release()
 assert.equal((await one).status,200);assert.equal((await two).status,200);assert.equal(calls,1)
 assert.equal((await post({...body,input:4})).status,400);assert.equal(calls,1)
})

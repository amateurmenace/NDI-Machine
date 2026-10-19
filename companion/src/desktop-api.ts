import type {IncomingMessage,ServerResponse} from 'node:http'
import type {ProductionRouter,DesktopRouteRequest} from './production.js'

// Local-only Host/Origin checks are applied by the owning HTTP server.
export class DesktopRoutingApi {
  private commands=new Map<string,{body:string;result:Promise<{status:number;value:unknown}>;complete:boolean}>()
  constructor(private production:ProductionRouter,private busy:()=>boolean,private execute:(work:()=>Promise<void>)=>Promise<void>){}
  async handle(request:IncomingMessage,response:ServerResponse):Promise<boolean>{
    const url=new URL(request.url ?? '/','http://127.0.0.1:9862')
    if(request.method==='GET' && url.pathname==='/api/v1/routing'){
      const nonce=url.searchParams.get('nonce')
      if(!nonce || nonce.length>128){response.writeHead(400).end(JSON.stringify({error:'Fresh bounded nonce required'}));return true}
      response.end(JSON.stringify({...this.production.routingState() as object,nonce,busy:this.busy()}));return true
    }
    if(request.method!=='POST' || !['/api/v1/route-output','/api/v1/return-defaults'].includes(url.pathname))return false
    try {
      const id=request.headers['idempotency-key']
      if(typeof id!=='string' || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(id))throw new Error('UUID Idempotency-Key required')
      if(!request.headers['content-type']?.startsWith('application/json'))throw new Error('JSON body required')
      const chunks:Buffer[]=[];let size=0
      for await(const chunk of request){size+=chunk.length;if(size>16384)throw new Error('Routing request exceeds 16 KiB');chunks.push(Buffer.from(chunk))}
      const text=Buffer.concat(chunks).toString('utf8'),body=JSON.parse(text)
      if(!body || body.schemaVersion!==1 || typeof body.routerInstanceId!=='string')throw new Error('Versioned routing request required')
      const signature=url.pathname+'\n'+text,existing=this.commands.get(id)
      if(existing && existing.body!==signature)throw new Error('Idempotency key reused for different routing request')
      let result=existing?.result
      if(!result){
        if(this.commands.size>=128){const oldest=[...this.commands].find(([,entry])=>entry.complete);if(!oldest)throw new Error('Routing commands still pending');this.commands.delete(oldest[0])}
        result=(async()=>{
          try {
            await this.execute(async()=>{
              if(url.pathname==='/api/v1/route-output')await this.production.routeOutput(body as DesktopRouteRequest)
              else {if(!Array.isArray(body.inputs))throw new Error('ATEM destination list required');await this.production.returnDefaults(body.routerInstanceId,body.inputs)}
            })
            return {status:200,value:{confirmed:true,instanceId:this.production.instanceId}}
          }catch(error){return {status:409,value:{error:String(error)+'. Check actual output/routes; no automatic rollback or retry.'}}}
        })()
        const entry={body:signature,result,complete:false};this.commands.set(id,entry);void result.then(()=>{entry.complete=true})
      }
      const reply=await result;response.writeHead(reply.status).end(JSON.stringify(reply.value))
    }catch(error){response.writeHead(400).end(JSON.stringify({error:String(error)}))}
    return true
  }
}

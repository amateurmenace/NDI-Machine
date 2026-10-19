// Companion's local UI API. No database access, credentials or full imports.
export class CompanionRpc {
  next=0; pending=new Map()
  async connect(){
    this.socket=new WebSocket('ws://127.0.0.1:8000/trpc')
    this.socket.addEventListener('message',event=>{
      const incoming=JSON.parse(String(event.data));for(const message of Array.isArray(incoming)?incoming:[incoming]){
        const pending=this.pending.get(message.id);if(!pending)continue
        if(message.error){clearTimeout(pending.timer);this.pending.delete(message.id);pending.reject(new Error(JSON.stringify(message.error)))}
        else if(message.result?.type==='data'){
          clearTimeout(pending.timer);this.pending.delete(message.id)
          if(pending.subscription)this.socket.send(JSON.stringify({id:message.id,method:'subscription.stop'}))
          pending.resolve(message.result.data)
        }
      }
    })
    await new Promise((resolve,reject)=>{const timer=setTimeout(()=>reject(new Error('Companion WebSocket timeout')),5000);this.socket.addEventListener('open',()=>{clearTimeout(timer);resolve()},{once:true});this.socket.addEventListener('error',()=>{clearTimeout(timer);reject(new Error('Companion WebSocket error'))},{once:true})})
  }
  call(path,input,method='mutation'){
    const id=++this.next;return new Promise((resolve,reject)=>{
      const timer=setTimeout(()=>{this.pending.delete(id);reject(new Error(`${path} timeout; not retried`))},60000)
      this.pending.set(id,{resolve,reject,timer,subscription:method==='subscription'})
      this.socket.send(JSON.stringify({id,method,params:{path,input}}))
    })
  }
  close(){this.socket?.close()}
}

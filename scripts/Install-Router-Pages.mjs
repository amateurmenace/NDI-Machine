// Explicit installation of seven owned pages and six authorized home cells.
// Only --update-owned-pages may replace pages 37-43. Never import home pages,
// the full configuration, triggers or new connections.
import {readFile,writeFile} from 'node:fs/promises'
import {createHash} from 'node:crypto'
import {CompanionRpc} from './Companion-Trpc.mjs'
const [file,evidence,mode]=process.argv.slice(2)
const replace=mode==='--update-owned-pages'
if(!file||!evidence)throw new Error('Usage: node Install-Router-Pages.mjs new-pages.json private-evidence.json')
const bytes=await readFile(file),bundle=JSON.parse(bytes),numbers=Object.keys(bundle.pages).map(Number).sort((a,b)=>a-b)
if(JSON.stringify(numbers)!=='[37,38,39,40,41,42,43]')throw new Error('Only the seven appended router pages are permitted')
const connections=Object.keys(bundle.instances);if(connections.length!==1 || bundle.instances[connections[0]].label!=='ndi_machine')throw new Error('Expected one existing NDI Machine connection')
const moves=[[[43,3,0],[1,1,3]],[[43,3,1],[1,1,4]],[[43,3,2],[1,2,0]],[[43,3,3],[21,1,4]],[[43,3,4],[21,1,5]],[[43,3,5],[21,3,0]]]
const location=([pageNumber,row,column])=>({pageNumber,row,column})
const rpc=new CompanionRpc();await rpc.connect()
try{
  const config=await rpc.call('userConfig.getConfig',undefined,'query')
  const grid=config.gridSize
  for(const page of Object.values(bundle.pages)){
    const size=page.gridSize
    if(!grid || size.minColumn<grid.minColumn || size.maxColumn>grid.maxColumn || size.minRow<grid.minRow || size.maxRow>grid.maxRow)throw new Error('Router page would expand the global grid; installation stopped')
  }
  const before=await rpc.call('pages.watch',undefined,'subscription');if(before.order.length!==(replace?43:36))throw new Error('Page count changed. Inspect before installing; no overwrite attempted.')
  if(replace)for(const number of numbers){const name=before.pages[before.order[number-1]].name;if(!/^(MK|XL) \| (NDI|CLOUD|Destinations|More Inputs|Stream)/.test(name))throw new Error('Page is not an owned router page: '+number)}
  const previous=[]
  for(const [,target] of moves){const [page,row,col]=target;const controlId=before.pages[before.order[page-1]].controls[row][col];const control=await rpc.call('controls.watchControl',{controlId},'subscription');previous.push({location:target,controlId,control:control.config})}
  await writeFile(evidence,JSON.stringify({capturedAt:new Date().toISOString(),before,previous},null,2))
  const sessionId=await rpc.call('importExport.prepareImport.start',{name:'NDI-Router-new-pages.json',size:bytes.length})
  for(let offset=0;offset<bytes.length;offset+=65536)await rpc.call('importExport.prepareImport.uploadChunk',{sessionId,offset,data:bytes.subarray(offset,offset+65536).toString('base64')})
  await rpc.call('importExport.prepareImport.complete',{sessionId,expectedChecksum:createHash('sha1').update(bytes).digest('hex'),userData:null})
  for(const number of numbers){
    await rpc.call('importExport.importSinglePage',{targetPage:replace?number:-1,sourcePage:number,connectionIdRemapping:{[connections[0]]:connections[0]}})
    const observed=await rpc.call('pages.watch',undefined,'subscription')
    if(observed.order.length!==(replace?43:number) || observed.pages[observed.order[number-1]].name!==bundle.pages[number].name)throw new Error('Owned page not confirmed; remaining changes stopped')
    console.log(`Installed owned page ${number}: ${bundle.pages[number].name}`)
  }
  const result=await rpc.call('controls.gridBatchTransfer',{operation:'copy',pairs:moves.map(([from,to])=>({fromLocation:location(from),toLocation:location(to)}))})
  console.log('Targeted home-cell copies:',JSON.stringify(result))
  await rpc.call('importExport.abort',undefined)
  console.log('Installed. Export and compare all untouched configuration before accepting delivery.')
}finally{rpc.close()}

const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const {spawnSync}=require('node:child_process');
const root=path.resolve(__dirname,'../..');
const fixture=spawnSync(process.execPath,[path.join(root,'scripts/make-provider-fixtures.cjs')],{encoding:'utf8'});
assert.equal(fixture.status,0,fixture.stderr);
const homes=JSON.parse(fixture.stdout);
const exe=path.join(root,'desktop/src-tauri/engine/CodexConversationManager.exe');
function request(body) {
 const p=spawnSync(exe,['--desktop-read'],{input:JSON.stringify({language:'zh',...body}),encoding:'utf8',timeout:30000,maxBuffer:32*1024*1024});
 assert.ifError(p.error); const response=JSON.parse(p.stdout); assert.equal(p.status,response.ok?0:1); return response;
}
function snapshot(folder) { const result={}; function walk(dir) { for(const entry of fs.readdirSync(dir,{withFileTypes:true})) {const file=path.join(dir,entry.name);if(entry.isDirectory())walk(file);else result[path.relative(folder,file)]=crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');} } walk(folder);return result; }
for(const provider of ['codex','dsh']) test(`${provider}: native catalog/preview preserve archive and parent state without writes`,()=>{
 const home=homes[provider],before=snapshot(home);
 const response=request({action:'list',provider,home,offset:null,projectId:null,id:null});assert.equal(response.ok,true,response.error);
 const projects=response.result.projects;const project=projects.find(p=>p.title==='演示项目');assert.ok(project);
 assert.equal(project.sessions.length,3);assert.equal(project.sessions.filter(s=>s.archived).length,1);assert.equal(project.sessions.filter(s=>s.subagent).length,1);
 const session=project.sessions.find(s=>!s.subagent&&!s.archived);
 assert.equal(project.canBackupFiles,true);
 const storage=request({action:'storage',provider,home,projectId:project.id});assert.equal(storage.ok,true,storage.error);assert.ok(storage.result.files>=1);assert.ok(storage.result.bytes>0);
 const preview=request({action:'preview',provider,home,projectId:project.id,id:session.id,offset:0});assert.equal(preview.ok,true,preview.error);
 assert.equal(preview.result.messages.length,2);assert.equal(preview.result.messages[0].role,'user');assert.equal(preview.result.hasMore,false);
 assert.equal(request({action:'preview',provider,home,projectId:project.id,id:'not-a-session'}).ok,false);
 assert.equal(request({action:'delete',provider,home,id:session.id}).ok,false);
 assert.deepEqual(snapshot(home),before);
});
test('bridge rejects invalid provider, missing home and relative paths',()=>{
 for(const body of [{provider:'unknown',home:homes.codex},{provider:'codex',home:'../outside'},{provider:'dsh',home:path.join(root,'artifacts/no-such-home')}]) assert.equal(request({action:'list',...body}).ok,false);
});

test('Codex zstd preview reads Node-produced frames and preserves compressed source files',()=>{
 const base=path.join(root,'artifacts');
 const home=fs.mkdtempSync(path.join(base,'desktop-read-zstd-'));
 try {
  fs.cpSync(homes.codex,home,{recursive:true});
  const zlib=require('node:zlib');
  function compress(dir) {
   for(const entry of fs.readdirSync(dir,{withFileTypes:true})) {
    const file=path.join(dir,entry.name);
    if(entry.isDirectory()) compress(file);
    else if(file.endsWith('.jsonl')) {
     const data=fs.readFileSync(file),split=data.indexOf(10)+1;
     fs.writeFileSync(file+'.zst',Buffer.concat([zlib.zstdCompressSync(data.subarray(0,split)),zlib.zstdCompressSync(data.subarray(split))]));
     fs.unlinkSync(file);
    }
   }
  }
  compress(home);
  const before=snapshot(home);
  const catalog=request({action:'list',provider:'codex',home}); assert.equal(catalog.ok,true,catalog.error);
  const project=catalog.result.projects.find(p=>p.title==='演示项目'); assert.ok(project);
  assert.equal(project.sessions.length,3);assert.ok(project.sessions.every(s=>s.compressed));
  assert.equal(project.sessions.filter(s=>s.archived).length,1);assert.equal(project.sessions.filter(s=>s.subagent).length,1);
  const session=project.sessions.find(s=>!s.archived&&!s.subagent);
  const preview=request({action:'preview',provider:'codex',home,projectId:project.id,id:session.id});
  assert.equal(preview.ok,true,preview.error);assert.equal(preview.result.messages.length,2);
  assert.equal(preview.result.messages[0].role,'user');assert.deepEqual(snapshot(home),before);
 } finally { assert.equal(path.dirname(home),base);fs.rmSync(home,{recursive:true}); }
});

test('Codex preview pages messages and labels oversized message truncation',()=>{
 const base=path.join(root,'artifacts');
 const home=fs.mkdtempSync(path.join(base,'desktop-read-paging-'));
 const id='dddddddd-dddd-4ddd-8ddd-dddddddddddd';
 const folder=path.join(home,'sessions','2026','10','15');fs.mkdirSync(folder,{recursive:true});
 const events=[{type:'session_meta',payload:{id,cwd:path.join(home,'project'),source:'cli',timestamp:'2026-10-15T01:00:00Z'}}];
 for(let i=0;i<25;i++) events.push({type:'response_item',timestamp:'2026-10-15T01:00:00Z',payload:{type:'message',role:i%2?'assistant':'user',content:[{type:i%2?'output_text':'input_text',text:i===24?'x'.repeat(100010):`message-${i}`}]}});
 fs.writeFileSync(path.join(folder,`rollout-${id}.jsonl`),events.map(e=>JSON.stringify(e)).join('\n')+'\n');
 const before=snapshot(home);
 const catalog=request({action:'list',provider:'codex',home});assert.equal(catalog.ok,true,catalog.error);
 const project=catalog.result.projects[0];
 const args={action:'preview',provider:'codex',home,projectId:project.id,id};
 const first=request({...args,offset:0});assert.equal(first.ok,true,first.error);assert.equal(first.result.messages.length,20);assert.equal(first.result.hasMore,true);assert.equal(first.result.nextOffset,20);
 const second=request({...args,offset:20});assert.equal(second.ok,true,second.error);assert.equal(second.result.messages.length,5);assert.equal(second.result.messages[0].text,'message-20');assert.equal(second.result.messages[4].text.length,100000);assert.equal(second.result.messages[4].truncated,true);assert.equal(second.result.hasMore,false);
 assert.equal(request({...args,offset:-1}).ok,false);assert.deepEqual(snapshot(home),before);
 // Keep only synthetic artifacts, never delete an arbitrary caller-supplied path.
 assert.equal(path.dirname(home),base);fs.rmSync(home,{recursive:true});
});

test('Codex preview cache reuses decompressed pages and invalidates when the source changes',()=>{
 const base=path.join(root,'artifacts'),home=fs.mkdtempSync(path.join(base,'desktop-cache-'));
 const id='eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee',folder=path.join(home,'sessions');fs.mkdirSync(folder);
 const file=path.join(folder,'rollout-'+id+'.jsonl.zst'),zlib=require('node:zlib');
 const events=[{type:'session_meta',payload:{id,cwd:path.join(home,'project'),source:'cli',timestamp:'2026-10-16T01:00:00Z'}}];
 for(let i=0;i<80;i++) events.push({type:'response_item',payload:{type:'message',role:i%2?'assistant':'user',content:[{type:'text',text:'cache-message-'+i}]}});
 const write=()=>fs.writeFileSync(file,zlib.zstdCompressSync(Buffer.from(events.map(e=>JSON.stringify(e)).join('\n')+'\n')));
 try {
  write();
  const project=request({action:'list',provider:'codex',home}).result.projects[0];
  const args={action:'preview',provider:'codex',home,projectId:project.id,id,sessionPath:file};
  const first=request({...args,offset:0});assert.equal(first.ok,true,first.error);assert.equal(first.result.cacheHit,false);
  assert.equal(first.result.navigation.length,40);assert.equal(first.result.navigation[39].index,78);assert.equal(first.result.navigation[39].answer,'cache-message-79');
  const later=request({...args,offset:60});assert.equal(later.ok,true,later.error);assert.equal(later.result.cacheHit,true);assert.equal(later.result.revision,first.result.revision);assert.equal(later.result.messages[0].text,'cache-message-60');
  const reopened=request({...args,offset:0});assert.equal(reopened.result.cacheHit,true);
  events.push({type:'response_item',payload:{type:'message',role:'user',content:[{type:'text',text:'new question'}]}});write();
  const changed=request({...args,offset:0});assert.equal(changed.ok,true,changed.error);assert.notEqual(changed.result.revision,first.result.revision);assert.equal(changed.result.cacheHit,false);assert.equal(changed.result.total,81);
  fs.unlinkSync(file);assert.equal(request({...args,offset:0}).ok,false);
 } finally {assert.equal(path.dirname(home),base);fs.rmSync(home,{recursive:true});}
});

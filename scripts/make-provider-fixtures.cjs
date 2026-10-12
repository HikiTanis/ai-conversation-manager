'use strict';
const fs=require('node:fs'),path=require('node:path'),z=require('node:zlib');
const root=path.resolve(__dirname,'../artifacts/provider-fixtures');
const codex=path.join(root,'codex'),dsh=path.join(root,'dsh'),project=path.join(root,'演示项目');
fs.mkdirSync(project,{recursive:true});fs.writeFileSync(path.join(project,'sample.txt'),'Synthetic project');
const ids=['aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa','bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb','cccccccc-cccc-4ccc-8ccc-cccccccccccc'];
for(let i=0;i<ids.length;i++){
 const dir=path.join(codex,i===1?'archived_sessions':'sessions','2026','10','12');fs.mkdirSync(dir,{recursive:true});
 const events=[{timestamp:'2026-10-12T03:00:00Z',type:'session_meta',payload:{id:ids[i],cwd:project,timestamp:'2026-10-12T03:00:00Z',originator:'codex-desktop',source:i===2?{subagent:{thread_spawn:{parent_thread_id:ids[0]}}}:'cli',model_provider:'openai',...(i===2?{parent_thread_id:ids[0]}:{})}},{timestamp:'2026-10-12T03:00:01Z',type:'response_item',payload:{type:'message',role:'user',content:[{type:'input_text',text:'Codex '+(i===1?'归档方案':i===2?'子代理检查':'对话管理功能')}]}},{timestamp:'2026-10-12T03:00:02Z',type:'response_item',payload:{type:'message',role:'assistant',content:[{type:'output_text',text:'这是 Codex 的合成测试数据。'}]}}];
 fs.writeFileSync(path.join(dir,'rollout-2026-10-12T03-00-00-'+ids[i]+'.jsonl'),events.map(e=>JSON.stringify(e)).join('\n')+'\n');
}
fs.mkdirSync(path.join(dsh,'storages/session_projcache/sessions'),{recursive:true});
fs.writeFileSync(path.join(dsh,'storages/workspace.json'),JSON.stringify({unit:{name:'workspace',version:2},global:{initialized:true,workspaceIds:['demo'],archivedSessionIds:['archive-demo'],pinnedSessionIds:[]},tables:{workspaces:{demo:{path:project,title:'演示项目',sessionIds:['main-demo','archive-demo'],createdAt:'2026-10-12',updatedAt:'2026-10-12'}}}}));
for(const [id,title,parent] of [['main-demo','DSH 对话管理功能'],['archive-demo','DSH 已归档的界面方案'],['child-demo','DSH 子代理检查','main-demo']]){
 const dir=path.join(dsh,'sessions/--demo--',id);fs.mkdirSync(dir,{recursive:true});
 const events=[{type:'session',version:4,id,cwd:project,createdAt:1791806000000,isSeeded:false,delegationDepth:parent?1:0,...(parent?{parentSession:parent,origin:'subagent'}:{})},{type:'user/message',time:1791806000001,data:{content:'请把对话分类，并支持切换 Codex 和 DSH。',source:{kind:'user'}}},{type:'assistant/message',time:1791806000002,data:{content:'这是 DSH 的合成测试数据。'}}];
 fs.writeFileSync(path.join(dir,'session.v4.jsonl.zstd'),Buffer.concat(events.map(e=>z.zstdCompressSync(Buffer.from(JSON.stringify(e)+'\n')))));
 fs.writeFileSync(path.join(dsh,'storages/session_projcache/sessions',id+'.json'),JSON.stringify({record:{rows:{title:{val:title}}}}));
}
console.log(JSON.stringify({codex,dsh,project}));

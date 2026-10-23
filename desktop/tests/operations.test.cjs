const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path'), crypto = require('node:crypto');
const { spawnSync } = require('node:child_process');
const root = path.resolve(__dirname, '../..');
const exe = path.join(root, 'desktop/src-tauri/engine/CodexConversationManager.exe');
const fixture = spawnSync(process.execPath, [path.join(root, 'scripts/make-provider-fixtures.cjs')], { encoding: 'utf8' });
assert.equal(fixture.status, 0, fixture.stderr);
const source = JSON.parse(fixture.stdout);
const work = fs.mkdtempSync(path.join(root, 'artifacts/desktop-operations-'));
// Mock only the process inventory used by the offline guard, in child Node
// processes for these isolated synthetic homes. Production adapter is unmodified.
const preload = path.join(work, 'offline-inventory.cjs');
fs.writeFileSync(preload, `const cp=require('node:child_process'),original=cp.spawnSync;cp.spawnSync=function(file,args,options){if(file==='powershell.exe'&&args?.some(a=>a.includes('Get-CimInstance Win32_Process')))return {status:0,stdout:'[]',stderr:''};return original.apply(this,arguments);};`);
function request(body, read = false) {
  assert.ok(path.resolve(body.home).startsWith(work + path.sep), 'integration tests must use their own synthetic data');
  const child = spawnSync(exe, [read ? '--desktop-read' : '--desktop-operation'], { input: JSON.stringify({ language: 'en', ...body }), env: {...process.env, NODE_OPTIONS: `--require "${preload.split(path.sep).join("/")}"`}, encoding: 'utf8', timeout: 60000, maxBuffer: 32 * 1024 * 1024 });
  assert.ifError(child.error);
  const response = JSON.parse(child.stdout);
  assert.equal(child.status, response.ok ? 0 : 1);
  return response;
}
function success(body, read = false) { const r = request(body, read); assert.equal(r.ok, true, r.error); return r.result; }
function snapshot(folder) {
  const entries = {};
  function walk(dir) { for (const e of fs.readdirSync(dir, { withFileTypes: true })) { const f = path.join(dir, e.name); if (e.isDirectory()) walk(f); else entries[path.relative(folder, f)] = crypto.createHash('sha256').update(fs.readFileSync(f)).digest('hex'); } }
  walk(folder); return entries;
}
function compressSessions(home) {
  const zlib = require('node:zlib');
  function walk(dir) { if (!fs.existsSync(dir)) return; for (const e of fs.readdirSync(dir, {withFileTypes:true})) {
    const file = path.join(dir, e.name);
    if (e.isDirectory()) walk(file);
    else if (file.endsWith('.jsonl')) { fs.writeFileSync(file + '.zst', zlib.zstdCompressSync(fs.readFileSync(file))); fs.unlinkSync(file); }
  } }
  walk(path.join(home, 'sessions')); walk(path.join(home, 'archived_sessions'));
  const dbPath = path.join(home, 'state_5.sqlite');
  if (fs.existsSync(dbPath)) {
    const {DatabaseSync} = require('node:sqlite'), db = new DatabaseSync(dbPath);
    for (const row of db.prepare('select id, rollout_path from threads').all()) {
      if (fs.existsSync(row.rollout_path + '.zst')) db.prepare('update threads set rollout_path=? where id=?').run(row.rollout_path + '.zst', row.id);
    }
    db.close();
  }
}
for (const [provider, compressed] of [['codex', false], ['codex', true], ['dsh', false]]) test(`${provider}${compressed ? '-zstd' : ''}: backup, read-only inspect, import, merge and copy preserve source`, () => {
  // Match the longer hosted-runner checkout paths even in a short local repo.
  // A rollout path remains below MAX_PATH, but appending a GUID suffix would
  // exceed it. Exercise archived import, merge and copy under that boundary.
  const scenario = (provider + (compressed ? '-zstd' : '')).padEnd(Math.max(10, 101 - work.length), 'x');
  const home = path.join(work, scenario, 'source'); fs.cpSync(source[provider], home, { recursive: true });
  if (compressed) compressSessions(home);
  const before = snapshot(home), args = { provider, home };
  const catalog = success({ ...args, action: 'list' }, true), project = catalog.projects[0];
  const output = path.join(work, scenario, provider === 'codex' ? 'backup.codexproject' : 'backup.dshproject');
  success({ ...args, action: 'backup', projectIds: [project.id], includeFiles: true, output });
  const info = success({ ...args, action: 'packageInfo', package: output });
  assert.equal(info.hasFiles, true); assert.equal(info.mainCount, 2); assert.equal(info.subagentCount, 1);
  assert.equal(info.projects.length, 1); assert.ok(info.fileCount >= 1); assert.ok(path.isAbsolute(info.suggestedTarget));
  assert.deepEqual(snapshot(home), before, 'package summary must not write source data');
  assert.equal(request({ ...args, action: 'backup', projectIds: [project.id], output }).ok, false, 'never overwrite an existing backup');
  const destination = path.join(work, scenario, 'destination'); fs.mkdirSync(destination);
  if (provider === 'dsh') {
    fs.mkdirSync(path.join(destination, 'storages')); fs.mkdirSync(path.join(destination, 'sessions'));
    fs.writeFileSync(path.join(destination, 'storages/workspace.json'), JSON.stringify({unit:{name:'workspace',version:2},global:{initialized:true,workspaceIds:[],archivedSessionIds:[],pinnedSessionIds:[]},tables:{workspaces:{}}}));
  } else {
    const { DatabaseSync } = require('node:sqlite');
    const db = new DatabaseSync(path.join(destination, 'state_5.sqlite'));
    db.exec("create table backfill_state(id integer primary key,status text not null,last_watermark text,last_success_at integer,updated_at integer not null);insert into backfill_state values(1,'complete',null,123,123);create table threads(id text primary key,rollout_path text not null,created_at integer not null,updated_at integer not null,source text not null,model_provider text not null,cwd text not null,title text not null,sandbox_policy text not null,approval_mode text not null,tokens_used integer default 0,has_user_event integer default 0,archived integer default 0,archived_at integer,cli_version text default '',first_user_message text default '',memory_mode text default 'enabled',preview text default '',recency_at integer default 0,recency_at_ms integer default 0,history_mode text default 'legacy');create table thread_spawn_edges(parent_thread_id text not null,child_thread_id text primary key,status text not null);"); db.close();
  }
  const beforeInspect = snapshot(destination);
  const target = path.join(work, scenario, 'restored-project');
  const importArgs = { provider, home: destination, package: output, target, map: true, restoreFiles: true, mode: 'merge', conflict: 'empty' };
  success({ ...importArgs, action: 'inspect' });
  assert.deepEqual(snapshot(destination), beforeInspect); assert.equal(fs.existsSync(target), false);
  success({ ...importArgs, action: 'import' });
  assert.equal(fs.readFileSync(path.join(target, 'sample.txt'), 'utf8'), 'Synthetic project');
  let imported = success({ provider, home: destination, action: 'list' }, true);
  assert.equal(imported.projects.flatMap(p => p.sessions).length, 3);
  if (compressed) compressSessions(destination);
  success({ ...importArgs, action: 'import', conflict: 'skip' });
  imported = success({ provider, home: destination, action: 'list' }, true);
  assert.equal(imported.projects.flatMap(p => p.sessions).length, 3, 'merge should be idempotent');
  success({ ...importArgs, action: 'import', conflict: 'skip', mode: 'copy' });
  imported = success({ provider, home: destination, action: 'list' }, true);
  assert.equal(imported.projects.flatMap(p => p.sessions).length, 6);
  assert.deepEqual(snapshot(home), before);
});
test('DSH archive, delete scope, trash restore and purge round trip', () => {
  const home = path.join(work, 'dsh-actions'); fs.cpSync(source.dsh, home, { recursive: true });
  const args = { provider: 'dsh', home }, ids = ['main-demo'];
  success({ ...args, action: 'archive', ids });
  assert.ok(success({ ...args, action: 'list' }, true).projects.flatMap(p => p.sessions).find(s => s.id === ids[0]).archived);
  success({ ...args, action: 'unarchive', ids });
  const plan = success({ ...args, action: 'planDelete', ids });
  assert.equal(plan.sessions.length, 2);
  assert.equal(request({ ...args, action: 'delete', ids, token: 'stale' }).ok, false);
  success({ ...args, action: 'delete', ids, token: plan.token });
  let trash = success({ ...args, action: 'trash' }); assert.equal(trash.items.length, 1);
  success({ ...args, action: 'restore', id: trash.items[0].id });
  assert.equal(success({ ...args, action: 'list' }, true).projects.flatMap(p => p.sessions).length, 3);
  const again = success({ ...args, action: 'planDelete', ids });
  success({ ...args, action: 'delete', ids, token: again.token });
  trash = success({ ...args, action: 'trash' });
  success({ ...args, action: 'purge', id: trash.items[0].id });
  assert.equal(success({ ...args, action: 'trash' }).items.length, 0);
  success({ ...args, action: 'recover' });
});
test('Codex deletion rejects a changed scope without touching source data', () => {
  const home = path.join(work, 'codex-stale'); fs.cpSync(source.codex, home, { recursive: true });
  const args = { provider: 'codex', home }, ids = ['aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'];
  const before = snapshot(home);
  assert.equal(success({ ...args, action: 'planDelete', ids }).sessions.length, 2);
  assert.equal(request({ ...args, action: 'delete', ids, token: 'unconfirmed' }).ok, false);
  assert.deepEqual(snapshot(home), before);
});

for (const provider of ['codex', 'dsh']) test(`${provider}: project deletion preserves legacy batch and typed-confirmation guards`, () => {
  const home = path.join(work, `${provider}-project-guards`); fs.cpSync(source[provider], home, { recursive: true });
  const args = { provider, home }, before = snapshot(home);
  const project = success({ ...args, action: 'list' }, true).projects[0];
  const main = project.sessions.filter(s => !s.subagent);
  const partial = request({ ...args, action: 'planDelete', ids: [main[0].id], batch: true, projectMode: 'permanent' });
  assert.equal(partial.ok, false); assert.match(partial.error, /every related main/i);
  const plan = success({ ...args, action: 'planDelete', ids: main.map(s => s.id), batch: true, projectMode: 'permanent' });
  const rejected = request({ ...args, action: 'delete', ids: main.map(s => s.id), batch: true, projectMode: 'permanent', token: plan.token, projectConfirmation: 'wrong-name' });
  assert.equal(rejected.ok, false); assert.match(rejected.error, /folder names do not match/i);
  assert.deepEqual(snapshot(home), before); assert.ok(fs.existsSync(project.path));
});

test('Codex diagnostic preserves live descendants, legacy snapshots', () => {
  const home = path.join(work, 'repair-plan'); fs.cpSync(source.codex, home, { recursive: true });
  const args = { provider: 'codex', home };
  const project = success({ ...args, action: 'list' }, true).projects[0];
  const child = project.sessions.find(s => s.subagent), main = project.sessions.find(s => s.id === child.parentId);
  const { DatabaseSync } = require('node:sqlite');
  const db = new DatabaseSync(path.join(home, 'state_5.sqlite'));
  db.exec('create table threads(id text primary key, rollout_path text, cwd text, title text, source text, parent_thread_id text)');
  const insert = db.prepare('insert into threads values(?,?,?,?,?,?)');
  insert.run(main.id, path.join(home, 'sessions/missing.jsonl'), project.path, 'Missing parent', 'cli', '');
  insert.run(child.id, child.path, project.path, 'Live child', 'subagent', main.id);
  db.close();
  const snapshotFile = main.path + '.ccm-txn-bak-1'; fs.copyFileSync(main.path, snapshotFile);
  const before = snapshot(home), plan = success({ ...args, action: 'planRepair' });
  assert.deepEqual(plan.ids, []); assert.equal(plan.blocked.length, 1); assert.equal(plan.blocked[0].id, child.id);
  assert.equal(plan.snapshotCount, 0); assert.match(plan.diagnostic, /preserved/i); assert.deepEqual(snapshot(home), before);
  fs.appendFileSync(snapshotFile, '\n');
  const changed = snapshot(home), rejected = request({ ...args, action: 'repair', token: plan.token });
  assert.equal(rejected.ok, true); assert.match(rejected.result.message, /preserved/i); assert.deepEqual(snapshot(home), changed);
});

test('Codex diagnostics preserve compressed rollouts with plain index paths', () => {
  const home = path.join(work, 'repair-compressed'); fs.cpSync(source.codex, home, { recursive: true });
  const args = {provider: 'codex', home};
  const project = success({...args, action:'list'}, true).projects[0];
  compressSessions(home);
  const {DatabaseSync} = require('node:sqlite');
  const db = new DatabaseSync(path.join(home, 'state_5.sqlite'));
  db.exec('create table threads(id text primary key, rollout_path text, cwd text, title text, source text, parent_thread_id text)');
  for (const s of project.sessions) db.prepare('insert into threads values(?,?,?,?,?,?)').run(s.id, s.path, project.path, s.title, 'cli', s.parentId || '');
  db.close();
  const before = snapshot(home);
  const plan = success({...args, action:'planRepair'});
  assert.match(plan.diagnostic, /Found 0 /);
  success({...args, action:'repair', token:'old-deletion-plan'});
  assert.deepEqual(snapshot(home), before);
});


test('Codex paginated groups use indexed entry and fingerprint every fragment', () => {
  const home = path.join(work, 'paged-scope'); fs.cpSync(source.codex, home, { recursive: true });
  const args = {provider: 'codex', home};
  const initial = success({...args, action:'list'}, true).projects[0];
  const child = initial.sessions.find(s=>s.subagent), main=initial.sessions.find(s=>s.id===child.parentId);
  const unrelated=initial.sessions.find(s=>!s.subagent && s.id!==main.id);
  const lines=fs.readFileSync(main.path,'utf8').trim().split('\n');
  const meta=JSON.parse(lines[0]);meta.ordinal=0;meta.payload.history_mode='paginated';
  lines[0]=JSON.stringify(meta);fs.writeFileSync(main.path,lines.join('\n')+'\n');
  const active=path.join(path.dirname(main.path),'rollout-paged-'+main.id+'_'+crypto.randomUUID()+'.jsonl');
  meta.ordinal=1604;meta.payload.history_base={thread_id:main.id,end_ordinal_exclusive:1604,end_byte_offset:10};
  fs.writeFileSync(active,JSON.stringify(meta)+'\n');
  const {DatabaseSync}=require('node:sqlite'), db=new DatabaseSync(path.join(home,'state_5.sqlite'));
  db.exec('create table threads(id text primary key, rollout_path text, cwd text, title text, source text, parent_thread_id text)');
  for(const s of initial.sessions) db.prepare('insert into threads values(?,?,?,?,?,?)').run(s.id,s.id===main.id?active:s.path,initial.path,s.title,'cli',s.parentId||'');
  db.close();
  const before=snapshot(home), current=success({...args, action:'list'},true).projects.flatMap(p=>p.sessions).find(s=>s.id===main.id);
  assert.equal(current.path,active,'catalog must use the indexed current fragment');
  const unrelatedPlan=success({...args,action:'planDelete',ids:[unrelated.id],projectMode:'none'});
  assert.equal(unrelatedPlan.fileCount,1,'unrelated multi-file conversation must not block deletion');
  const plan=success({...args,action:'planDelete',ids:[main.id],projectMode:'none'});
  assert.equal(plan.fileCount,plan.sessions.length+1,'scope must include every fragment and descendants');
  assert.deepEqual(snapshot(home),before,'planning must be read-only');
  fs.appendFileSync(main.path,'\n');
  const changed=snapshot(home), rejected=request({...args,action:'delete',ids:[main.id],projectMode:'none',token:plan.token});
  assert.equal(rejected.ok,false);assert.match(rejected.error,/changed/i);
  assert.deepEqual(snapshot(home),changed,'stale older-fragment plan must perform no writes');
});

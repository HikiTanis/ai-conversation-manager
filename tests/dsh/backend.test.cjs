'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const z = require('node:zlib');
const api = require('../../adapters/dsh/backend.cjs');
const offline = () => {};
const tempRoot = path.join(__dirname, '.tmp');
fs.mkdirSync(tempRoot, { recursive: true });
function fixture(t) {
  const home = fs.mkdtempSync(path.join(tempRoot, 'dsh-'));
  t.after(() => fs.rmSync(home, { recursive: true, force: true }));
  const data = { unit: { name: 'workspace', version: 2 }, global: { initialized: true, workspaceIds: ['w'], archivedSessionIds: ['old'], pinnedSessionIds: ['parent'], extra: 'keep' }, tables: { workspaces: { w: { title: '测试项目', path: 'E:/test', sessionIds: ['parent', 'old'], createdAt: '2026-01-01', updatedAt: '2026-01-01' } } } };
  fs.mkdirSync(path.join(home, 'storages/session_projcache/sessions'), { recursive: true });
  const file = path.join(home, 'storages/workspace.json');
  fs.writeFileSync(file, JSON.stringify(data));
  function session(id, parent, compressed = true) {
    const dir = path.join(home, 'sessions', '--E-test--', api.encode(id));
    fs.mkdirSync(dir, { recursive: true });
    const header = { type: 'session', version: 4, id, createdAt: 123, cwd: 'E:/test', isSeeded: false, delegationDepth: parent ? 1 : 0, ...(parent ? { parentSession: parent, origin: 'subagent' } : {}) };
    const events = [header, { type: 'user/message', time: 123, data: { content: '中文问题', source: { kind: 'user' } } }, { type: 'assistant/message', time: 124, data: { content: [{ type: 'text', text: '助手回复' }] } }];
    const buffers = events.map(e => Buffer.from(JSON.stringify(e) + '\n'));
    fs.writeFileSync(path.join(dir, compressed ? 'session.v4.jsonl.zstd' : 'session.v4.jsonl'), Buffer.concat(compressed ? buffers.map(b => z.zstdCompressSync(b)) : buffers));
    fs.writeFileSync(path.join(home, 'storages/session_projcache/sessions', api.encode(id) + '.json'), JSON.stringify({ record: { rows: { title: { val: '对话 ' + id } } } }));
    return dir;
  }
  session('parent'); session('child', 'parent'); session('grandchild', 'child'); session('old', undefined, false);
  return { home, file, session, read: () => JSON.parse(fs.readFileSync(file)), write: d => fs.writeFileSync(file, JSON.stringify(d)) };
}
function change(f, ids, op, guard = offline) { const p = api.plan(f.home, ids, op); return api.change(f.home, ids, op, p.token, guard); }
test('scan separates native archive state and decodes all concatenated frames', t => {
  const f = fixture(t), s = api.scan(f.home);
  assert.equal(s.rows.length, 4); assert.equal(s.errors.length, 0);
  assert.equal(s.rows.find(x => x.id === 'grandchild').owner, 'w');
  assert.deepEqual(s.rows.filter(x => x.archived).map(x => x.id), ['old']);
  const p = api.preview(f.home, 'parent').text;
  assert.match(p, /中文问题/); assert.match(p, /助手回复/);
  assert.match(api.preview(f.home, 'old').text, /助手回复/);
});
test('archive and unarchive preserve order, unknown fields, content and remove pin', t => {
  const f = fixture(t), before = f.read();
  change(f, ['parent'], 'archive');
  assert.deepEqual(f.read().global.archivedSessionIds, ['old', 'parent']);
  assert.deepEqual(f.read().global.pinnedSessionIds, []);
  assert.deepEqual(f.read().tables, before.tables); assert.equal(f.read().global.extra, 'keep');
  change(f, ['parent'], 'unarchive'); assert.deepEqual(f.read().global.archivedSessionIds, ['old']);
  assert.equal(api.scan(f.home).rows.length, 4);
});
test('trash includes recursive children, restore preserves newer unrelated changes', t => {
  const f = fixture(t), p = api.plan(f.home, ['parent'], 'trash'); assert.equal(p.count, 3);
  const r = change(f, ['parent'], 'trash');
  assert.equal(api.scan(f.home).rows.length, 1); assert.deepEqual(f.read().tables.workspaces.w.sessionIds, ['old']);
  assert.equal(fs.existsSync(path.join(f.home, 'storages/session_projcache/sessions/child.json')), false);
  f.session('new'); const current = f.read(); current.tables.workspaces.w.sessionIds.push('new'); f.write(current);
  api.restore(f.home, r.batch, offline);
  assert.equal(api.scan(f.home).rows.length, 5);
  assert.deepEqual(f.read().tables.workspaces.w.sessionIds, ['parent', 'old', 'new']);
  assert.deepEqual(f.read().global.pinnedSessionIds, ['parent']);
});
test('archived deletion restores its native archive membership', t => {
  const f = fixture(t), r = change(f, ['old'], 'trash');
  assert.deepEqual(f.read().global.archivedSessionIds, []);
  api.restore(f.home, r.batch, offline); assert.deepEqual(f.read().global.archivedSessionIds, ['old']);
});
test('reject stale plans, missing IDs and active processes before mutations', t => {
  const f = fixture(t), before = fs.readFileSync(f.file), p = api.plan(f.home, ['old'], 'trash');
  assert.throws(() => api.change(f.home, ['old'], 'trash', p.token, () => { throw Error('running'); }), /running/);
  assert.deepEqual(fs.readFileSync(f.file), before);
  assert.throws(() => api.plan(f.home, ['gone'], 'trash'), /变化/);
  assert.throws(() => api.change(f.home, ['old'], 'trash', 'bad-token', offline), /变化/);
});
test('unsupported format blocks destructive plan', t => {
  const f = fixture(t), dir = f.session('future');
  fs.writeFileSync(path.join(dir, 'session.v5.jsonl'), '{}');
  assert.equal(api.scan(f.home).errors.length, 1);
  assert.throws(() => api.plan(f.home, ['parent'], 'trash'), /扫描错误/);
});
test('restore refuses existing IDs without overwriting', t => {
  const f = fixture(t), r = change(f, ['old'], 'trash'); f.session('old');
  assert.throws(() => api.restore(f.home, r.batch, offline), /同名/);
  assert.equal(api.scan(f.home).trash.find(x => x.batch === r.batch).state, 'trashed');
});
test('unsafe paths and junctions are refused', t => {
  const f = fixture(t);
  assert.throws(() => api.safe(f.home, '../outside'), /越出/);
  const external = fs.mkdtempSync(path.join(tempRoot, 'external-'));
  t.after(() => fs.rmSync(external, { force: true, recursive: true }));
  fs.symlinkSync(external, path.join(f.home, 'sessions', 'linked'), 'junction');
  assert.throws(() => api.safe(f.home, 'sessions/linked/target'), /链接|联接/);
});
test('mid-move failure rolls back sessions and registry', t => {
  const f = fixture(t), before = f.read(), original = fs.renameSync;
  let failed = false;
  fs.renameSync = (a, b) => {
    if (!failed && String(a).endsWith(path.join('sessions', '--E-test--', 'child'))) { failed = true; throw Error('injected move failure'); }
    return original(a, b);
  };
  try { assert.throws(() => change(f, ['parent'], 'trash'), /injected/); }
  finally { fs.renameSync = original; }
  assert.deepEqual(f.read(), before); assert.equal(api.scan(f.home).rows.length, 4);
  assert.equal(api.scan(f.home).trash[0].state, 'rolledBack');
});
test('prepared transaction recovery restores deleted files after interrupted operation', t => {
  const f = fixture(t), before = f.read(), r = change(f, ['old'], 'trash');
  const mf = path.join(f.home, 'conversation-manager-trash', r.batch, 'manifest.json');
  const m = JSON.parse(fs.readFileSync(mf)); m.state = 'prepared'; fs.writeFileSync(mf, JSON.stringify(m));
  assert.throws(() => change(f, ['parent'], 'archive'), /中断/);
  assert.equal(api.recover(f.home, offline).count, 1);
  assert.deepEqual(f.read(), before); assert.equal(api.scan(f.home).rows.length, 4);
});
test('a concurrent registry change is never overwritten by rollback', t => {
  const f = fixture(t); let calls = 0;
  assert.throws(() => change(f, ['old'], 'trash', () => { if (++calls === 2) { const d = f.read(); d.global.extra = 'changed elsewhere'; f.write(d); } }), /其他程序修改|回滚未完成/);
  assert.equal(f.read().global.extra, 'changed elsewhere');
  assert.equal(api.scan(f.home).rows.length, 4);
});
test('interrupted restore rolls back to trash and can be retried', t => {
  const f = fixture(t), r = change(f, ['old'], 'trash');
  api.restore(f.home, r.batch, offline);
  const undo = api.scan(f.home).trash.find(x => x.operation === 'restore');
  const mf = path.join(f.home, 'conversation-manager-trash', undo.batch, 'manifest.json');
  const m = JSON.parse(fs.readFileSync(mf)); m.state = 'prepared'; fs.writeFileSync(mf, JSON.stringify(m));
  api.recover(f.home, offline);
  assert.equal(api.scan(f.home).rows.length, 3);
  assert.equal(api.scan(f.home).trash.find(x => x.batch === r.batch).state, 'trashed');
  api.restore(f.home, r.batch, offline);
  assert.equal(api.scan(f.home).rows.length, 4);
});
test('corrupt compressed tails fail preview without changing data', t => {
  const f = fixture(t);
  const file = path.join(f.home, 'sessions/--E-test--/parent/session.v4.jsonl.zstd');
  fs.appendFileSync(file, Buffer.from([1,2,3,4])); const original = fs.readFileSync(file);
  assert.throws(() => api.preview(f.home, 'parent'));
  assert.deepEqual(fs.readFileSync(file), original);
});
test('unknown workspace versions fail closed', t => {
  const f = fixture(t), data = f.read(); data.unit.version = 3; f.write(data);
  assert.throws(() => api.scan(f.home), /v2/);
});

const packages = require('../../adapters/dsh/packages.cjs');
function packageFixture(t) {
  const f = fixture(t), target = fixture(t);
  const staging = fs.mkdtempSync(path.join(tempRoot, 'package-'));
  t.after(() => fs.rmSync(staging, { recursive: true, force: true }));
  packages.exportPackage(f.home, { staging, ids: ['parent','child','grandchild','old'], projects: [{ project_key: 'w', source_project: 'E:/test', source_project_name: '测试项目', target_folder: 'test' }] });
  const r = { staging, targets: { w: path.join(target.home, 'project') }, mode: 'merge' };
  return { f, target, staging, r };
}
test('DSH package export/import preserves messages, archive state, parent relationships and native folder convention', t => {
  const { target, r } = packageFixture(t);
  const before = fs.readFileSync(target.file);
  const p = packages.importPlan(target.home, r);
  assert.equal(p.count, 4); assert.deepEqual(fs.readFileSync(target.file), before);
  packages.importApply(target.home, { ...r, token: p.token }, offline);
  const imported = api.scan(target.home).rows.filter(s => s.cwd === r.targets.w);
  assert.equal(imported.length, 4); assert.equal(imported.filter(s => s.archived).length, 1);
  const root = imported.find(s => s.title === '对话 parent');
  assert.equal(imported.find(s => s.title === '对话 child').parent, root.id);
  assert.match(api.preview(target.home, root.id).text, /中文问题/);
  assert.doesNotMatch(root.relative, /--manager-/);
  assert.equal(packages.importPlan(target.home, r).skipped, 4);
  const copy = { ...r, mode: 'copy' }, cp = packages.importPlan(target.home, copy);
  packages.importApply(target.home, { ...copy, token: cp.token }, offline);
  assert.equal(api.scan(target.home).rows.filter(s => s.cwd === r.targets.w).length, 8);
});
test('DSH package refuses tampered files and path escapes', t => {
  const { target, r, staging } = packageFixture(t);
  const file = path.join(staging, 'manifest.json'), m = JSON.parse(fs.readFileSync(file));
  m.native_sessions[0].files[0].relative = '../../manifest.json';
  fs.writeFileSync(file, JSON.stringify(m));
  assert.throws(() => packages.importPlan(target.home, r), /越出/);
});
test('DSH package import rejects stale plan and rolls back injected file move failure', t => {
  const { target, r } = packageFixture(t), p = packages.importPlan(target.home, r);
  assert.throws(() => packages.importApply(target.home, { ...r, token: 'stale' }, offline), /变化/);
  const before = fs.readFileSync(target.file), rename = fs.renameSync;
  let moved = 0;
  fs.renameSync = function(from, to) {
    if (String(from).includes(path.sep + 'new' + path.sep) && ++moved === 2) throw Error('injected import failure');
    return rename.apply(this, arguments);
  };
  try { assert.throws(() => packages.importApply(target.home, { ...r, token: p.token }, offline), /injected/); }
  finally { fs.renameSync = rename; }
  assert.deepEqual(JSON.parse(fs.readFileSync(target.file)), JSON.parse(before));
  assert.equal(api.scan(target.home).rows.length, 4);
  assert.equal(fs.existsSync(path.join(target.home, 'conversation-manager-lineage.json')), false);
});

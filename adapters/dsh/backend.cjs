'use strict';
// Independent offline adapter for DeepSeek Harness desktop 0.2.0-rc.2.
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const zlib = require('node:zlib');
const crypto = require('node:crypto');
const cp = require('node:child_process');
const LIMIT = 128 * 1024 * 1024;
const fail = message => { throw new Error(message); };
const hash = b => crypto.createHash('sha256').update(b).digest('hex');
const json = p => JSON.parse(fs.readFileSync(p, 'utf8').replace(/^\uFEFF/, ''));
const clone = x => JSON.parse(JSON.stringify(x));
const exists = p => fs.existsSync(p);
const encode = s => s === '.' ? '~002E' : s === '..' ? '~002E~002E' : s.split('').map(c => /[A-Za-z0-9._-]/.test(c) ? c : '~' + c.charCodeAt(0).toString(16).toUpperCase().padStart(4, '0')).join('');

function safe(root, relative) {
  if (!relative || path.isAbsolute(relative)) fail('不允许绝对路径或空路径。');
  const p = path.resolve(root, relative), rel = path.relative(root, p);
  if (!rel || rel === '..' || rel.startsWith('..' + path.sep) || path.isAbsolute(rel)) fail('路径越出数据目录。');
  // Inspect every ancestor as well as the destination: never traverse junctions.
  let part = path.parse(p).root;
  for (const segment of p.slice(part.length).split(path.sep)) {
    part = path.join(part, segment);
    if (exists(part) && fs.lstatSync(part).isSymbolicLink()) fail('不支持符号链接或目录联接：' + part);
  }
  return p;
}
function treeSize(p) {
  const st = fs.lstatSync(p);
  if (st.isSymbolicLink()) fail('目录包含符号链接，拒绝操作：' + p);
  if (st.isDirectory()) return fs.readdirSync(p).reduce((sum, n) => sum + treeSize(path.join(p, n)), 0);
  if (!st.isFile()) fail('不支持的文件类型：' + p);
  return st.size;
}
function atomic(p, value) {
  const tmp = path.join(path.dirname(p), '.' + crypto.randomBytes(6).toString('hex') + '.tmp');
  try {
    const fd = fs.openSync(tmp, 'wx');
    try { fs.writeFileSync(fd, JSON.stringify(value, null, 2) + '\n'); fs.fsyncSync(fd); }
    finally { fs.closeSync(fd); }
    fs.renameSync(tmp, p);
  } finally { if (exists(tmp)) fs.unlinkSync(tmp); }
}
function workspace(home) {
  const file = safe(home, 'storages/workspace.json');
  const bytes = fs.readFileSync(file);
  const data = JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/, ''));
  if (data.unit?.name !== 'workspace' || data.unit.version !== 2 || !data.global || !data.tables?.workspaces || Array.isArray(data.tables.workspaces)) fail('仅支持 workspace v2 格式，请勿直接修改未知版本。');
  if (data.global.pendingMutation) fail('DSH 有未完成的工作区操作，请先启动 DSH 完成恢复后退出。');
  for (const key of ['workspaceIds', 'archivedSessionIds', 'pinnedSessionIds']) {
    if (key !== 'workspaceIds' && data.global[key] === undefined) data.global[key] = [];
    if (!Array.isArray(data.global[key]) || data.global[key].some(x => typeof x !== 'string')) fail('工作区索引格式无效：' + key);
  }
  for (const v of Object.values(data.tables.workspaces)) {
    if (!v || typeof v.path !== 'string' || !Array.isArray(v.sessionIds) || v.sessionIds.some(x => typeof x !== 'string')) fail('工作区记录无效。');
  }
  return { file, bytes, data };
}
function readLog(file, headerOnly = false) {
  const stat = fs.statSync(file);
  if (stat.size > LIMIT) fail('会话超过 128 MiB 读取上限，保留原文件。');
  const raw = fs.readFileSync(file);
  let text;
  if (file.endsWith('.zstd')) {
    if (!zlib.zstdDecompressSync) fail('需要支持 Zstandard 的 Node.js 22.15+ 或 24+。');
    let offset = 0, total = 0;
    const parts = [];
    while (offset < raw.length) {
      const result = zlib.zstdDecompressSync(raw.subarray(offset), { info: true, maxOutputLength: LIMIT - total });
      if (!result.engine.bytesWritten) fail('压缩会话没有读取进展。');
      offset += result.engine.bytesWritten;
      total += result.buffer.length;
      parts.push(result.buffer);
      if (headerOnly || total >= LIMIT) break;
    }
    if (!headerOnly && offset < raw.length) fail('解压内容超过 128 MiB 上限。');
    text = Buffer.concat(parts).toString('utf8');
  } else text = raw.toString('utf8');
  const lines = text.split('\n').filter(x => x.trim());
  const result = (headerOnly ? lines.slice(0, 1) : lines).map(x => JSON.parse(x));
  const h = result[0];
  if (!h || h.type !== 'session' || h.version !== 4 || typeof h.id !== 'string' || !h.id || !Number.isSafeInteger(h.createdAt) || (h.parentSession !== undefined && typeof h.parentSession !== 'string')) fail('仅支持已验证的 session v4 格式。');
  return result;
}
function scan(home) {
  const w = workspace(home), rows = [], errors = [];
  let lineage = {};
  try { lineage = json(safe(home, 'conversation-manager-lineage.json')); } catch {}
  const root = safe(home, 'sessions');
  if (!exists(root)) fail('找不到 sessions 目录，请选择正确的 .dsh 目录。');
  const ids = new Set();
  for (const project of fs.readdirSync(root, { withFileTypes: true })) {
    if (!project.isDirectory() && !project.isSymbolicLink()) continue;
    try {
      const pd = safe(home, path.join('sessions', project.name));
      for (const entry of fs.readdirSync(pd, { withFileTypes: true })) {
        if (!entry.isDirectory() && !entry.isSymbolicLink()) continue;
        const relative = path.join('sessions', project.name, entry.name);
        try {
          const dir = safe(home, relative);
          const files = fs.readdirSync(dir).filter(n => /^session(?:\.v[1-9]\d*)?\.jsonl(?:\.zstd)?$/.test(n));
          const versions = files.map(n => Number(n.match(/\.v(\d+)\./)?.[1] || 0));
          if (!files.length) continue;
          if (Math.max(...versions) !== 4) fail('尚未支持的会话格式版本。');
          const current = files.filter(n => /^session\.v4\.jsonl(?:\.zstd)?$/.test(n));
          if (current.length !== 1) fail('同一会话有多个 v4 编码，无法确定有效文件。');
          const file = safe(home, path.join(relative, current[0]));
          const h = readLog(file, true)[0];
          if (encode(h.id) !== entry.name) fail('会话 ID 与目录名不匹配。');
          if (ids.has(h.id)) fail('同一会话 ID 存在重复目录。');
          ids.add(h.id);
          let title = lineage[h.id]?.title || h.id;
          try { title = json(safe(home, path.join('storages/session_projcache/sessions', encode(h.id) + '.json'))).record?.rows?.title?.val || title; } catch {}
          if (typeof title !== 'string') title = h.id;
          const owners = Object.entries(w.data.tables.workspaces).filter(([, v]) => v.sessionIds.includes(h.id));
          if (owners.length > 1) fail('同一会话属于多个工作区，拒绝猜测归属。');
          const owner = owners[0];
          rows.push({ id: h.id, title, cwd: h.cwd || '', project: owner?.[1].title || path.basename((h.cwd || '').replace(/\\/g, '/')) || '无项目', owner: owner?.[0] || '', parent: h.parentSession || '', kind: h.origin === 'subagent' || h.parentSession ? '子代理' : '主对话', archived: w.data.global.archivedSessionIds.includes(h.id), pinned: w.data.global.pinnedSessionIds.includes(h.id), relative, file: path.relative(home, file), bytes: treeSize(dir), updated: fs.statSync(file).mtime.toISOString() });
        } catch (e) { errors.push(relative + '：' + e.message); }
      }
    } catch (e) { errors.push(project.name + '：' + e.message); }
  }
  // Child sessions often have no workspace membership slot. Group them with
  // their parent's display workspace without inventing persisted membership.
  for (let pass = 0; pass < rows.length; pass++) {
    let changed = false;
    for (const row of rows.filter(x => !x.owner)) {
      const parent = rows.find(x => x.id === row.parent && x.owner);
      const matches = Object.entries(w.data.tables.workspaces).filter(([, v]) => path.resolve(v.path).toLowerCase() === path.resolve(row.cwd || '.').toLowerCase());
      if (parent) { row.owner = parent.owner; row.project = parent.project; changed = true; }
      else if (row.cwd && matches.length === 1) { row.owner = matches[0][0]; row.project = matches[0][1].title; changed = true; }
    }
    if (!changed) break;
  }
  rows.sort((a, b) => b.updated.localeCompare(a.updated));
  return { home, rows, errors, projects: Object.entries(w.data.tables.workspaces).map(([id, v]) => ({ id, title: v.title, path: v.path })), trash: listTrash(home) };
}
function content(value) {
  if (typeof value === 'string') return value;
  if (Array.isArray(value)) return value.map(x => typeof x === 'string' ? x : x?.text || (x?.type ? '[' + x.type + ']' : '')).join('\n');
  return '';
}
function preview(home, id) {
  const row = scan(home).rows.find(x => x.id === id);
  if (!row) fail('会话已不存在，请刷新。');
  const events = readLog(safe(home, row.file));
  const messages = [], structured = [];
  let used = 0;
  for (const event of events.slice(1)) {
    if (!['user/message', 'assistant/message'].includes(event.type)) continue;
    const d = event.data?.message || event.data;
    if (event.type === 'user/message' && d?.source?.kind && d.source.kind !== 'user') continue;
    const text = content(d?.content);
    if (!text) continue;
    used += text.length;
    if (used > 2000000) { messages.push('【预览达到 200 万字符上限】'); structured.push({ IsNotice: true, RoleLabel: '提示', Text: '预览达到 200 万字符上限，完整内容保留在原会话中。' }); break; }
    messages.push((event.type === 'user/message' ? '用户' : '助手') + (event.time ? '  ' + new Date(event.time).toISOString() : '') + '\n' + text);
    structured.push({ IsUser: event.type === 'user/message', RoleLabel: event.type === 'user/message' ? '你' : 'DeepSeek', DisplayTime: event.time ? new Date(event.time).toISOString() : '', Text: text });
  }
  return { messages: structured, text: row.title + '\n' + row.id + '\n' + row.cwd + '\n\n' + (messages.join('\n\n────────────────────\n\n') || '暂无可显示的用户或助手消息。') };
}
function assertOffline() {
  if (process.platform !== 'win32') fail('写入功能仅支持 Windows。');
  const script = "$ErrorActionPreference='Stop'; @(Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^(DeepSeek[ -]?Harness|dsh-desktop|dsh)(\\.exe)?$' -or ($_.Name -match '^(node|electron)(\\.exe)?$' -and $_.CommandLine -match '@deepseek-ai|dsh-host|dsh-boot|deepseek.harness|[\\\\/]dsh[\\\\/]|[\\\\/]\\.dsh[\\\\/].*(profiles|host|boot)') } | Select-Object ProcessId,Name) | ConvertTo-Json -Compress";
  const r = cp.spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], { encoding: 'utf8', windowsHide: true, timeout: 15000 });
  if (r.error || r.status !== 0) fail('无法检查 DSH 进程；为防止并发写入，本次未操作。');
  if (r.stdout.trim() && r.stdout.trim() !== '[]') fail('请完全退出 DeepSeek Harness 桌面端和 DSH 后台进程后重试。');
}
function trashRoot(home) { return safe(home, 'conversation-manager-trash'); }
function listTrash(home) {
  const root = trashRoot(home);
  if (!exists(root)) return [];
  return fs.readdirSync(root).filter(n => /^\d{13}-[a-f0-9-]{36}$/.test(n)).map(n => {
    try {
      const m = json(safe(home, path.join('conversation-manager-trash', n, 'manifest.json')));
      return { batch: n, created: m.created, count: m.rows?.length || 0, title: m.rows?.[0]?.title || m.operation, state: m.state, operation: m.operation, rows: m.rows || [], projectMode: m.projectMode || '' };
    } catch { return { batch: n, state: 'invalid', title: '无法读取记录', count: 0 }; }
  }).sort((a, b) => b.batch.localeCompare(a.batch));
}
function selection(home, ids, descendants) {
  if (!Array.isArray(ids) || !ids.length || ids.some(x => typeof x !== 'string')) fail('请先选择会话。');
  const s = scan(home);
  if (descendants && s.errors.length) fail('部分会话无法读取，无法保证关联子代理完整；请先处理扫描错误。\n' + s.errors.join('\n'));
  const selected = new Set(ids);
  if (ids.some(id => !s.rows.some(x => x.id === id))) fail('选中的会话已变化，请刷新。');
  if (descendants) {
    let size;
    do { size = selected.size; for (const row of s.rows) if (selected.has(row.parent)) selected.add(row.id); } while (size !== selected.size);
  }
  return s.rows.filter(x => selected.has(x.id));
}
function treeFingerprint(root) {
  const entries = [];
  function walk(p, relative) {
    const st = fs.lstatSync(p);
    if (st.isSymbolicLink()) fail('无法对链接进行安全校验：' + p);
    if (st.isDirectory()) for (const name of fs.readdirSync(p).sort()) walk(path.join(p, name), path.join(relative, name));
    else if (st.isFile()) entries.push([relative, hash(fs.readFileSync(p))]);
    else fail('不支持的文件类型：' + p);
  }
  walk(root, ''); return hash(JSON.stringify(entries));
}
function fingerprint(rows, home) { return hash(JSON.stringify(rows.map(row => ({...row, contentHash: treeFingerprint(safe(home, row.relative))})))); }
function plan(home, ids, operation) {
  const rows = selection(home, ids, operation === 'trash');
  return { rows, token: fingerprint(rows, home), count: rows.length, bytes: rows.reduce((n, x) => n + x.bytes, 0) };
}
function assertUnchanged(w) { if (hash(fs.readFileSync(w.file)) !== hash(w.bytes)) fail('DSH 工作区索引已被其他程序修改，请刷新后重试。'); }
function withLock(home, guard, fn, allowPending = false) {
  guard();
  const lock = safe(home, 'conversation-manager.lock');
  let fd;
  try { fd = fs.openSync(lock, 'wx'); }
  catch { fail('管理器锁已存在。请确认没有其他管理器运行；若上次异常退出，请使用“恢复中断操作”。'); }
  try {
    fs.writeFileSync(fd, JSON.stringify({ pid: process.pid }));
    if (!allowPending && listTrash(home).some(x => ['prepared', 'invalid'].includes(x.state))) fail('发现中断或损坏的操作记录，请先恢复中断操作。');
    return fn();
  } finally { fs.closeSync(fd); fs.unlinkSync(lock); }
}
function batchCreate(home, operation, w, rows, after) {
  const batch = Date.now() + '-' + crypto.randomUUID();
  const dir = safe(home, path.join('conversation-manager-trash', batch));
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, 'workspace.before.json'), w.bytes, { flag: 'wx' });
  const manifest = { version: 1, home: path.resolve(home), created: new Date().toISOString(), operation, state: 'prepared', rows, before: w.data, after, moves: [] };
  return { batch, dir, manifest, save() { atomic(path.join(dir, 'manifest.json'), manifest); } };
}
function change(home, ids, operation, token, guard = assertOffline) {
  if (!['trash', 'archive', 'unarchive'].includes(operation)) fail('未知操作。');
  return withLock(home, guard, () => {
    const p = plan(home, ids, operation);
    if (!token || token !== p.token) fail('会话列表已变化，请重新预览操作。');
    const w = workspace(home), after = clone(w.data), selected = new Set(p.rows.map(x => x.id));
    if (operation === 'archive') {
      after.global.archivedSessionIds = [...new Set([...after.global.archivedSessionIds, ...selected])];
      after.global.pinnedSessionIds = after.global.pinnedSessionIds.filter(id => !selected.has(id));
    } else {
      after.global.archivedSessionIds = after.global.archivedSessionIds.filter(id => !selected.has(id));
      if (operation === 'trash') {
        after.global.pinnedSessionIds = after.global.pinnedSessionIds.filter(id => !selected.has(id));
        for (const v of Object.values(after.tables.workspaces)) v.sessionIds = v.sessionIds.filter(id => !selected.has(id));
      }
    }
    const b = batchCreate(home, operation, w, p.rows, after);
    if (operation === 'trash') for (const row of p.rows) {
      for (const rel of [row.relative, path.join('storages/session_projcache/sessions', encode(row.id) + '.json')]) {
        const source = safe(home, rel);
        if (exists(source)) { treeSize(source); b.manifest.moves.push({ source: rel, target: path.join('payload', rel) }); }
      }
    }
    b.save();
    try {
      guard(); assertUnchanged(w);
      if (plan(home, ids, operation).token !== p.token) fail('会话内容在操作前发生变化，已停止。');
      for (const m of b.manifest.moves) {
        const target = safe(b.dir, m.target);
        fs.mkdirSync(path.dirname(target), { recursive: true });
        fs.renameSync(safe(home, m.source), target);
      }
      assertUnchanged(w); atomic(w.file, after);
      b.manifest.state = operation === 'trash' ? 'trashed' : 'completed'; b.save();
      return { count: p.count, batch: b.batch };
    } catch (e) {
      try { rollback(home, b.dir, b.manifest); }
      catch (recovery) { fail(e.message + '\n回滚未完成，请使用“恢复中断操作”：' + recovery.message); }
      throw e;
    }
  });
}
function manifest(home, batch) {
  if (!/^\d{13}-[a-f0-9-]{36}$/.test(batch)) fail('无效回收站编号。');
  const dir = safe(home, path.join('conversation-manager-trash', batch)), m = json(path.join(dir, 'manifest.json'));
  if (m.version !== 1 || path.resolve(m.home) !== path.resolve(home) || !Array.isArray(m.moves)) fail('回收站记录不属于当前数据目录。');
  for (const move of m.moves) {
    if (!/^(sessions[\\/][^\\/]+[\\/][^\\/]+|storages[\\/]session_projcache[\\/]sessions[\\/][^\\/]+\.json)$/.test(move.source) || move.target !== path.join('payload', move.source)) fail('回收站记录包含非法目标。');
    safe(home, move.source); safe(dir, move.target);
  }
  return { dir, m };
}
function rollback(home, dir, m) {
  const w = workspace(home);
  const same = x => JSON.stringify(w.data) === JSON.stringify(x);
  if (!same(m.before) && !same(m.after)) fail('索引有新的更改，拒绝覆盖；请保留回收站记录进行手动恢复。');
  for (const move of [...m.moves].reverse()) {
    const source = safe(home, move.source), target = safe(dir, move.target);
    if (exists(target) && exists(source)) fail('恢复目标已存在：' + source);
    if (!exists(target) && !exists(source)) fail('原文件和回收站副本均不存在：' + move.source);
    if (exists(target)) { fs.mkdirSync(path.dirname(source), { recursive: true }); fs.renameSync(target, source); }
  }
  atomic(w.file, m.before);
  m.state = 'rolledBack'; atomic(path.join(dir, 'manifest.json'), m);
}
function purge(home, batch, guard = assertOffline) {
  return withLock(home, guard, () => {
    const { dir, m } = manifest(home, batch);
    if (!['trashed', 'purging'].includes(m.state)) fail('只能永久清除回收站中的删除批次。');
    treeSize(dir); m.state = 'purging'; atomic(path.join(dir, 'manifest.json'), m);
    fs.rmSync(dir, { recursive: true });
    return { count: m.rows.length };
  });
}
function restore(home, batch, guard = assertOffline) {
  return withLock(home, guard, () => {
    const { dir, m } = manifest(home, batch);
    if (m.state !== 'trashed') fail('此记录不是可恢复的删除批次。');
    const w = workspace(home), next = clone(w.data), ids = new Set(m.rows.map(x => x.id));
    const current = scan(home);
    if (current.errors.length) fail('当前会话扫描不完整，无法验证恢复冲突。');
    if (current.rows.some(x => ids.has(x.id)) || Object.values(next.tables.workspaces).some(v => v.sessionIds.some(id => ids.has(id)))) fail('同名会话已存在，拒绝覆盖。');
    for (const [key, old] of Object.entries(m.before.tables.workspaces)) {
      const members = old.sessionIds.filter(id => ids.has(id));
      if (!members.length) continue;
      if (!next.tables.workspaces[key] || next.tables.workspaces[key].path !== old.path) fail('原工作区已删除或改变，请先恢复原工作区。');
      for (const id of members) next.tables.workspaces[key].sessionIds.splice(Math.min(old.sessionIds.indexOf(id), next.tables.workspaces[key].sessionIds.length), 0, id);
    }
    for (const field of ['archivedSessionIds', 'pinnedSessionIds']) next.global[field] = [...new Set([...next.global[field], ...m.before.global[field].filter(id => ids.has(id))])];
    for (const move of m.moves) {
      if (exists(safe(home, move.source))) fail('恢复目标已存在：' + move.source);
      treeSize(safe(dir, move.target));
    }
    // Persist a separate undo transaction so a interrupted restore can be rolled back.
    const undo = batchCreate(home, 'restore', w, m.rows, next);
    undo.manifest.restoreBatch = batch;
    undo.manifest.moves = [];
    undo.save();
    const moved = [];
    try {
      guard(); assertUnchanged(w);
      for (const move of m.moves) {
        const source = safe(home, move.source); fs.mkdirSync(path.dirname(source), { recursive: true });
        fs.renameSync(safe(dir, move.target), source); moved.push(move);
      }
      assertUnchanged(w); atomic(w.file, next);
      m.state = 'restored'; atomic(path.join(dir, 'manifest.json'), m);
      undo.manifest.state = 'completed'; undo.save();
      return { count: m.rows.length };
    } catch (e) {
      // Durable prepared restore is also handled by recover().
      recoverRestore(home, undo.dir, undo.manifest);
      throw e;
    }
  });
}
function recoverRestore(home, dir, m) {
  const original = manifest(home, m.restoreBatch);
  const w = workspace(home);
  if (JSON.stringify(w.data) !== JSON.stringify(m.before) && JSON.stringify(w.data) !== JSON.stringify(m.after)) fail('恢复中断后索引已变化，拒绝覆盖。');
  for (const move of original.m.moves) {
    const source = safe(home, move.source), target = safe(original.dir, move.target);
    if (exists(source) && exists(target)) fail('恢复存在同名冲突。');
    if (!exists(source) && !exists(target)) fail('恢复副本缺失：' + move.source);
    if (exists(source)) { fs.mkdirSync(path.dirname(target), { recursive: true }); fs.renameSync(source, target); }
  }
  atomic(w.file, m.before);
  original.m.state = 'trashed'; atomic(path.join(original.dir, 'manifest.json'), original.m);
  m.state = 'rolledBack'; atomic(path.join(dir, 'manifest.json'), m);
}
function recover(home, guard = assertOffline) {
  guard();
  const lock = safe(home, 'conversation-manager.lock');
  if (exists(lock)) {
    const pid = json(lock).pid;
    if (!Number.isInteger(pid) || pid < 1) fail('锁文件无效，请人工检查。');
    try { process.kill(pid, 0); fail('另一个管理器仍在运行。'); } catch (e) { if (e.code !== 'ESRCH') throw e; }
    fs.unlinkSync(lock);
  }
  return withLock(home, guard, () => {
    let count = 0;
    for (const b of listTrash(home).filter(x => x.state === 'prepared').reverse()) {
      const { dir, m } = manifest(home, b.batch);
      if (m.operation === 'restore') recoverRestore(home, dir, m);
      else if (m.operation === 'import') require('./packages.cjs').rollbackImport(home, dir, m);
      else rollback(home, dir, m);
      count++;
    }
    return { count };
  }, true);
}
async function main(request) {
  const home = path.resolve(request.home || path.join(os.homedir(), '.dsh'));
  switch (request.action) {
    case 'offline': assertOffline(); return {};
    case 'trash': return listTrash(home);
    case 'purge': return purge(home, request.batch);
    case 'markProject': {
      return withLock(home, assertOffline, () => {
        const { dir, m } = manifest(home, request.batch);
        m.projectMode = String(request.mode); atomic(path.join(dir, 'manifest.json'), m); return {};
      });
    }
    case 'export': return require('./packages.cjs').exportPackage(home, request);
    case 'importPlan': return require('./packages.cjs').importPlan(home, request);
    case 'importApply': return require('./packages.cjs').importApply(home, request);
    case 'scan': return scan(home);
    case 'preview': return preview(home, request.id);
    case 'plan': return plan(home, request.ids, request.operation);
    case 'change': return change(home, request.ids, request.operation, request.token);
    case 'restore': return restore(home, request.batch);
    case 'recover': return recover(home);
    default: fail('未知命令。');
  }
}
module.exports = { treeFingerprint, scan, preview, plan, change, restore, recover, readLog, safe, assertOffline, encode, purge, workspace, atomic, treeSize, hash, withLock, batchCreate, assertUnchanged, manifest };
if (require.main === module) {
  let input = '';
  process.stdin.setEncoding('utf8');
  process.stdin.on('data', x => { input += x; if (input.length > 1024 * 1024) process.exit(2); });
  process.stdin.on('end', async () => {
    try { console.log(JSON.stringify({ ok: true, result: await main(JSON.parse(input)) })); }
    catch (e) { console.log(JSON.stringify({ ok: false, error: e.message })); process.exitCode = 1; }
  });
}

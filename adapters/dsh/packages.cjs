'use strict';
const fs = require('node:fs'), path = require('node:path'), crypto = require('node:crypto'), z = require('node:zlib');
const a = require('./backend.cjs');
const fail = m => { throw Error(m); };
const json = p => JSON.parse(fs.readFileSync(p, 'utf8').replace(/^\uFEFF/, ''));
const canonical = p => path.resolve(p).toLowerCase();
const lineagePath = h => a.safe(h, 'conversation-manager-lineage.json');
function projectKey(cwd) {
  let result = '', separator = false;
  for (const ch of cwd.split('')) {
    if ('/\\:'.includes(ch)) { if (!separator) result += '-'; separator = true; }
    else { result += /^[A-Za-z0-9._-]$/.test(ch) ? ch : '~' + ch.charCodeAt(0).toString(16).toUpperCase().padStart(4, '0'); separator = false; }
  }
  return '--' + (result.replace(/^-+/, '') || 'root').slice(0, 251) + '--';
}
function lineage(h) {
  const file = lineagePath(h);
  if (!fs.existsSync(file)) return {};
  const obj = json(file);
  if (!obj || Array.isArray(obj) || typeof obj !== 'object') fail('同源索引格式无效。');
  return obj;
}
function files(dir, prefix = '') {
  const result = [];
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (e.isSymbolicLink()) fail('备份目录包含链接：' + e.name);
    const relative = path.join(prefix, e.name), file = path.join(dir, e.name);
    if (e.isDirectory()) result.push(...files(file, relative));
    else if (e.isFile() && e.name !== 'session.lock') result.push({ relative, length: fs.statSync(file).size, sha256: a.hash(fs.readFileSync(file)) });
    else if (!e.isFile()) fail('不支持的文件类型。');
  }
  return result;
}
function exportPackage(home, r) {
  const scan = a.scan(home), w = a.workspace(home), links = lineage(home);
  const ids = new Set(r.ids);
  if (scan.errors.length) fail('扫描有异常，不能创建完整备份：' + scan.errors.join('\n'));
  const rows = scan.rows.filter(x => ids.has(x.id));
  if (rows.length !== ids.size) fail('部分会话已不存在，请刷新后重试。');
  if (!Array.isArray(r.projects) || !r.projects.length) fail('没有选择项目。');
  const root = path.resolve(r.staging);
  if (fs.readdirSync(root).length) fail('备份暂存目录必须为空。');
  const manifest = { schema: 1, engine: 'deepseek-harness', engine_version: '0.2.0', created_at: new Date().toISOString(), mode: r.includeProjectFiles ? 'projects' : 'conversations', includes_subagents: rows.some(x => x.parent), projects: r.projects, sessions: [], native_sessions: [] };
  for (const [index, row] of rows.entries()) {
    const project = r.projects.find(p => p.project_key === row.owner || canonical(p.source_project) === canonical(row.cwd));
    if (!project) fail('找不到会话的来源项目。');
    const source = a.safe(home, row.relative), folder = path.join('sessions', String(index).padStart(6, '0'));
    const entries = files(source);
    for (const item of entries) {
      const from = a.safe(source, item.relative), to = a.safe(root, path.join(folder, item.relative));
      fs.mkdirSync(path.dirname(to), { recursive: true }); fs.copyFileSync(from, to);
      if (a.hash(fs.readFileSync(to)) !== item.sha256 || a.hash(fs.readFileSync(from)) !== item.sha256) fail('会话在备份时变化，请退出 DSH 后重试。');
    }
    a.readLog(a.safe(root, path.join(folder, path.basename(row.file))));
    manifest.native_sessions.push({ id: row.id, origin: links[row.id]?.origin || row.id, title: row.title, cwd: row.cwd, project_key: project.project_key, parent: row.parent, archived: row.archived, pinned: row.pinned, folder, log: path.basename(row.file), files: entries });
    manifest.sessions.push({ thread_id: row.id, origin_thread_id: links[row.id]?.origin || row.id, title: row.title, project_key: project.project_key, archived: row.archived, is_subagent: !!row.parent, updated_at: row.updated });
  }
  a.assertUnchanged(w); a.atomic(path.join(root, 'manifest.json'), manifest);
  return manifest;
}
function validatePackage(root) {
  const m = json(a.safe(root, 'manifest.json'));
  if (m.schema !== 1 || m.engine !== 'deepseek-harness' || !Array.isArray(m.native_sessions) || !Array.isArray(m.projects)) fail('这不是支持的 DSH 备份包。');
  if (m.native_sessions.length > 10000) fail('单个包超过 10000 条会话。');
  const ids = new Set(), owned = new Set();
  for (const row of m.native_sessions) {
    if (typeof row.id !== 'string' || !row.id || ids.has(row.id) || typeof row.origin !== 'string' || !row.origin || !Array.isArray(row.files) || row.files.length > 50000) fail('备份包含无效或重复会话。');
    ids.add(row.id);
    if (!/^sessions[\\/]\d{6}$/.test(row.folder) || owned.has(row.folder)) fail('备份会话目录无效或重复。');
    owned.add(row.folder);
    if (!/^session\.v4\.jsonl(?:\.zstd)?$/.test(row.log)) fail('仅支持 v4 会话。');
    const listed = new Set();
    for (const f of row.files) {
      if (listed.has(f.relative.toLowerCase())) fail('备份包含重复文件。');
      listed.add(f.relative.toLowerCase());
      const file = a.safe(a.safe(root, row.folder), f.relative);
      if (!Number.isSafeInteger(f.length) || f.length < 0 || fs.statSync(file).size !== f.length || a.hash(fs.readFileSync(file)) !== f.sha256) fail('会话文件校验失败：' + row.id);
    }
    if (!listed.has(row.log.toLowerCase())) fail('会话日志不在已校验文件清单内。');
    const h = a.readLog(a.safe(root, path.join(row.folder, row.log)), true)[0];
    if (h.id !== row.id || h.cwd !== row.cwd || !m.projects.some(p => p.project_key === row.project_key)) fail('会话头与备份清单不一致。');
  }
  return m;
}
const idKeys = new Set(['sessionId', 'session_id', 'parentSession', 'parentSessionId', 'childSessionId', 'rootSessionId', 'targetSessionId', 'sourceSessionId']);
function rewrite(value, idMap, from, to, key = '') {
  if (typeof value === 'string') {
    if (idKeys.has(key) && idMap.has(value)) return idMap.get(value);
    if (['cwd', 'workdir', 'workingDirectory'].includes(key) && from && (canonical(value) === canonical(from) || canonical(value).startsWith(canonical(from) + path.sep))) return path.join(to, path.relative(from, value));
    return value;
  }
  if (Array.isArray(value)) return value.map(v => rewrite(v, idMap, from, to, key));
  if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, rewrite(v, idMap, from, to, k)]));
  return value;
}
function buildPlan(home, r) {
  const root = path.resolve(r.staging), m = validatePackage(root), w = a.workspace(home), links = lineage(home), scan = a.scan(home);
  if (scan.errors.length) fail('目标数据扫描不完整，不能验证导入冲突。');
  if (!['merge', 'copy'].includes(r.mode)) fail('未知导入方式。');
  const targets = r.targets;
  for (const p of m.projects) {
    if (!targets || typeof targets[p.project_key] !== 'string' || !path.isAbsolute(targets[p.project_key])) fail('目标项目路径必须为绝对路径。');
    a.safe(path.parse(targets[p.project_key]).root, path.relative(path.parse(targets[p.project_key]).root, targets[p.project_key]));
  }
  const mappings = new Map(), actions = [], warnings = [];
  const usedOrigins = new Set();
  for (const row of m.native_sessions) {
    const cwd = path.resolve(targets[row.project_key]);
    const originKey = canonical(cwd) + '\0' + row.origin;
    if (r.mode === 'merge' && usedOrigins.has(originKey)) fail('同一包有多个同源副本映射到同一目录，请选择独立复制。');
    usedOrigins.add(originKey);
    const matches = scan.rows.filter(s => canonical(s.cwd) === canonical(cwd) && (links[s.id]?.origin || s.id) === row.origin);
    if (r.mode === 'merge' && matches.length > 1) fail('目标目录存在多个同源对话，无法确定合并目标。');
    const current = r.mode === 'merge' ? matches[0] : null;
    const id = current?.id || 'session-' + crypto.randomUUID();
    mappings.set(row.id, id);
    const folder = current?.relative || path.join('sessions', projectKey(cwd), a.encode(id));
    if (!current && fs.existsSync(a.safe(home, folder))) fail('新会话目标已存在。');
    actions.push({ row, cwd, id, current, folder });
  }
  for (const item of actions) {
    const source = a.readLog(a.safe(root, path.join(item.row.folder, item.row.log)));
    const log = source.map(e => rewrite(e, mappings, item.row.cwd, item.cwd));
    log[0].id = item.id; log[0].cwd = item.cwd;
    if (log[0].parentSession && !mappings.has(source[0].parentSession)) {
      delete log[0].parentSession; delete log[0].origin; log[0].delegationDepth = 0;
      warnings.push(item.row.title + '：包外父会话关系已解除。');
    }
    item.log = log;
    if (item.current) {
      const current = a.readLog(a.safe(home, item.current.file));
      if (current[0].createdAt !== log[0].createdAt || current[0].isSeeded !== log[0].isSeeded) fail('同源会话的创建身份不同，拒绝覆盖：' + item.row.title);
      const common = Math.min(current.length, log.length);
      for (let i = 1; i < common; i++) if (JSON.stringify(current[i]) !== JSON.stringify(log[i])) fail('历史存在分叉，不能自动合并：' + item.row.title + '；可选择作为新对话复制。');
      if (current.length >= log.length) item.skip = true;
    }
  }
  const token = a.hash(Buffer.from(JSON.stringify({ package: m, targets, mode: r.mode, workspace: a.hash(w.bytes), lineage: links, current: scan.rows })));
  return { root, manifest: m, w, links, actions, token, count: actions.filter(x => !x.skip).length, skipped: actions.filter(x => x.skip).length, warnings };
}
function importPlan(home, r) { const p = buildPlan(home, r); return { token: p.token, count: p.count, skipped: p.skipped, warnings: p.warnings }; }
function importApply(home, r, guard = a.assertOffline) {
  return a.withLock(home, guard, () => {
    const p = buildPlan(home, r);
    if (p.token !== r.token) fail('导入目标或备份内容已变化，请重新检查。');
    const after = JSON.parse(JSON.stringify(p.w.data)), linksAfter = { ...p.links };
    const now = new Date().toISOString();
    for (const project of p.manifest.projects) {
      const target = path.resolve(r.targets[project.project_key]);
      let entry = Object.entries(after.tables.workspaces).find(([, v]) => canonical(v.path) === canonical(target));
      if (!entry) {
        const id = crypto.randomUUID(); const record = { path: target, title: project.source_project_name || path.basename(target), sessionIds: [], createdAt: now, updatedAt: now };
        after.tables.workspaces[id] = record; after.global.workspaceIds.push(id); entry = [id, record];
      }
      for (const item of p.actions.filter(x => x.row.project_key === project.project_key)) {
        if (!item.log[0].parentSession && !entry[1].sessionIds.includes(item.id)) entry[1].sessionIds.push(item.id);
        if (!item.skip) {
          after.global.archivedSessionIds = after.global.archivedSessionIds.filter(id => id !== item.id);
          after.global.pinnedSessionIds = after.global.pinnedSessionIds.filter(id => id !== item.id);
          if (item.row.archived) after.global.archivedSessionIds.push(item.id);
          else if (item.row.pinned) after.global.pinnedSessionIds.push(item.id);
          linksAfter[item.id] = { origin: item.row.origin, project: item.cwd, title: item.row.title };
        }
      }
    }
    const b = a.batchCreate(home, 'import', p.w, [], after);
    const m = b.manifest;
    m.lineageBefore = fs.existsSync(lineagePath(home)) ? p.links : null;
    m.lineageAfter = linksAfter; m.createdPaths = [];
    for (const [i, item] of p.actions.entries()) {
      if (item.skip) continue;
      const staging = a.safe(b.dir, path.join('new', String(i))); fs.mkdirSync(staging, { recursive: true });
      // Preserve session-local assets, but don't install obsolete generation
      // files under a fresh identity. The formal backup retains all originals.
      for (const f of item.row.files.filter(f => !/^session(?:\.v\d+)?\.jsonl(?:\.zstd)?$/.test(f.relative))) {
        const target = a.safe(staging, f.relative); fs.mkdirSync(path.dirname(target), { recursive: true });
        fs.copyFileSync(a.safe(p.root, path.join(item.row.folder, f.relative)), target);
      }
      const raw = Buffer.from(item.log.map(x => JSON.stringify(x)).join('\n') + '\n');
      const logFile = 'session.v4.jsonl.zstd';
      fs.writeFileSync(path.join(staging, logFile), z.zstdCompressSync(raw));
      const hashes = files(staging);
      m.createdPaths.push({ source: item.folder, staged: path.relative(b.dir, staging), files: hashes, existed: !!item.current });
      if (item.current) m.moves.push({ source: item.current.relative, target: path.join('payload', item.current.relative) });
      const cache = path.join('storages/session_projcache/sessions', a.encode(item.id) + '.json');
      if (fs.existsSync(a.safe(home, cache))) m.moves.push({ source: cache, target: path.join('payload', cache) });
    }
    b.save();
    try {
      guard(); a.assertUnchanged(p.w);
      if (JSON.stringify(lineage(home)) !== JSON.stringify(p.links)) fail('同源索引已变化。');
      for (const move of m.moves) { const target = a.safe(b.dir, move.target); fs.mkdirSync(path.dirname(target), { recursive: true }); fs.renameSync(a.safe(home, move.source), target); }
      for (const created of m.createdPaths) { const target = a.safe(home, created.source); fs.mkdirSync(path.dirname(target), { recursive: true }); fs.renameSync(a.safe(b.dir, created.staged), target); }
      a.assertUnchanged(p.w); a.atomic(lineagePath(home), linksAfter); a.atomic(p.w.file, after);
      m.state = 'completed'; b.save();
      return { count: p.count, skipped: p.skipped, warnings: p.warnings, batch: b.batch };
    } catch (e) {
      try { rollbackImport(home, b.dir, m); } catch (re) { fail(e.message + '\n导入回滚未完成，请恢复中断操作：' + re.message); }
      throw e;
    }
  });
}
function rollbackImport(home, dir, m) {
  const w = a.workspace(home), links = lineage(home);
  if (![m.before, m.after].some(v => JSON.stringify(v) === JSON.stringify(w.data))) fail('工作区已有新更改，拒绝覆盖。');
  if (![m.lineageBefore || {}, m.lineageAfter].some(v => JSON.stringify(v) === JSON.stringify(links))) fail('同源索引已有新更改，拒绝覆盖。');
  for (const created of m.createdPaths) {
    if (!/^sessions[\\/][^\\/]+[\\/][^\\/]+$/.test(created.source)) fail('导入恢复路径无效。');
    const target = a.safe(home, created.source);
    const backup = m.moves.find(x => x.source === created.source);
    if (created.existed && (!backup || !fs.existsSync(a.safe(dir, backup.target)))) continue;
    if (!fs.existsSync(target)) continue;
    if (JSON.stringify(files(target)) !== JSON.stringify(created.files)) fail('导入的会话已有新内容，拒绝删除。');
    a.treeSize(target); fs.rmSync(target, { recursive: true });
  }
  for (const move of [...m.moves].reverse()) {
    const target = a.safe(home, move.source), backup = a.safe(dir, move.target);
    if (fs.existsSync(backup)) { if (fs.existsSync(target)) fail('回滚目标已存在。'); fs.mkdirSync(path.dirname(target), { recursive: true }); fs.renameSync(backup, target); }
  }
  if (m.lineageBefore) a.atomic(lineagePath(home), m.lineageBefore); else if (fs.existsSync(lineagePath(home))) fs.unlinkSync(lineagePath(home));
  a.atomic(w.file, m.before); m.state = 'rolledBack'; a.atomic(path.join(dir, 'manifest.json'), m);
}
module.exports = { exportPackage, importPlan, importApply, validatePackage, rollbackImport };

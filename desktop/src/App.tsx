import { ConversationView } from "./ConversationView";
import { Icon } from "./components/Icon";
import { Operations, type Command } from "./Operations";
import { Updates } from "./Updates";
import { version as appVersion } from "../package.json";
import { isTauri } from "@tauri-apps/api/core";
import { open } from "@tauri-apps/plugin-dialog";
import { Button, Input, Card, Dialog, Menu } from "./components/ui";
import { useEffect, useRef, useState } from "react";
import { gateway as nativeGateway } from "./gateway";
import { demoGateway } from "./demo";
import {
  filterSessions,
  formatBytes,
  type StorageSummary,
  type ArchiveFilter,
  type Catalog,
  type Gateway,
  type Language,
  type Preview,
  type Provider,
  type Session,
} from "./types";

function preference(key: string, fallback: string) {
  try {
    return localStorage.getItem(key) || fallback;
  } catch {
    return fallback;
  }
}
function remember(key: string, value: string) {
  try {
    localStorage.setItem(key, value);
  } catch {
    /* Browsing remains available without persistence. */
  }
}
export default function App({
  gateway = nativeGateway,
}: {
  gateway?: Gateway;
}) {
  const [provider, setProvider] = useState<Provider>(() =>
    preference("preview.provider", "codex") === "dsh" ? "dsh" : "codex",
  );
  const [lang, setLang] = useState<Language>(() =>
    preference("preview.language", "zh") === "en" ? "en" : "zh",
  );
  const [demo, setDemo] = useState(gateway === nativeGateway && !isTauri());
  const [home, setHome] = useState("");
  const [homeDraft, setHomeDraft] = useState("");
  const [homeOpen, setHomeOpen] = useState(false);
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [projectId, setProjectId] = useState("");
  const [query, setQuery] = useState("");
  const [projectQuery, setProjectQuery] = useState("");
  const [kind, setKind] = useState<"main" | "subagent">("main");
  const [archive, setArchive] = useState<ArchiveFilter>("all");
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [page, setPage] = useState<"manage" | "import">("manage");
  const [catalogBusy, setBusy] = useState(false);
  const [operationBusy, setOperationBusy] = useState(false);
  const [updateBusy, setUpdateBusy] = useState(false);
  const busy = catalogBusy || operationBusy || updateBusy;
  const [command, setCommand] = useState<Command | null>(null);
  const operationsHome = useRef("");
  const [error, setError] = useState("");
  const [refresh, setRefresh] = useState(0);
  const [previewSession, setPreviewSession] = useState<Session | null>(null);
  const [preview, setPreview] = useState<Preview | null>(null);
  const [previewBusy, setPreviewBusy] = useState(false);
  const [previewError, setPreviewError] = useState("");
  const [notice, setNotice] = useState("");
  const [homeError, setHomeError] = useState("");
  const [storage, setStorage] = useState<StorageSummary | null>(null);
  useEffect(() => {
    if (!notice) return;
    const timer = setTimeout(() => setNotice(""), 4000);
    return () => clearTimeout(timer);
  }, [notice]);
  const generation = useRef(0),
    previewGeneration = useRef(0);
  const previewDialog = useRef<HTMLDialogElement>(null),
    homeDialog = useRef<HTMLDialogElement>(null);
  const t = (zh: string, en: string) => (lang === "zh" ? zh : en);
  const api = demo ? demoGateway : gateway;
  useEffect(() => {
    document.documentElement.lang = lang === "zh" ? "zh-CN" : "en";
    const token = ++generation.current;
    ++previewGeneration.current;
    setBusy(true);
    setError("");
    setCatalog(null);
    setProjectId("");
    setProjectQuery("");
    setSelected(new Set());
    setQuery("");
    setArchive("all");
    setKind("main");
    setPreviewSession(null);
    setPreview(null);
    setPreviewBusy(false);
    api
      .read<Catalog>({ action: "list", provider, home, language: lang })
      .then((value) => {
        if (token !== generation.current) return;
        setCatalog(value);
        operationsHome.current = value.home;
        setProjectId(value.projects[0]?.id || "");
      })
      .catch((e) => {
        if (token === generation.current) setError(String(e));
      })
      .finally(() => {
        if (token === generation.current) setBusy(false);
      });
    return () => {
      ++generation.current;
      ++previewGeneration.current;
    };
  }, [provider, home, lang, api, refresh]);
  useEffect(() => {
    if (previewSession) previewDialog.current?.showModal();
    else previewDialog.current?.close();
  }, [previewSession]);
  useEffect(() => {
    if (homeOpen) homeDialog.current?.showModal();
    else homeDialog.current?.close();
  }, [homeOpen]);
  const project = catalog?.projects.find((p) => p.id === projectId);
  const filteredProjects =
    catalog?.projects.filter((p) =>
      (p.title + " " + p.path)
        .toLocaleLowerCase()
        .includes(projectQuery.toLocaleLowerCase()),
    ) || [];
  const sessions = project?.sessions || [];
  const typed = sessions.filter((s) => s.subagent === (kind === "subagent"));
  const visible = filterSessions(sessions, kind, archive, query);
  const selectedVisible = visible.filter((s) => selected.has(s.id));
  const selectedMain =
    catalog?.projects.flatMap((p) =>
      p.sessions.filter((s) => !s.subagent && selected.has(s.id)),
    ) || [];
  const allVisibleSelected =
    visible.length > 0 && selectedVisible.length === visible.length;
  useEffect(() => {
    setStorage(null);
    if (!project || !catalog) return;
    let current = true;
    const timer = setTimeout(() => {
      api
        .read<StorageSummary>({
          action: "storage",
          provider,
          home: catalog.home,
          projectId: project.id,
          language: lang,
        })
        .then((summary) => {
          if (current) setStorage(summary);
        })
        .catch((e) => {
          if (current)
            setStorage({
              bytes: 0,
              files: 0,
              directories: 0,
              skipped: 0,
              error: String(e),
            });
        });
    }, 150);
    return () => {
      current = false;
      clearTimeout(timer);
    };
  }, [project, catalog, api, provider, lang]);
  async function copy(text: string) {
    try {
      await navigator.clipboard.writeText(text);
      setNotice(t("已复制到剪贴板", "Copied to clipboard"));
    } catch (e) {
      setNotice(t("复制失败：", "Could not copy: ") + String(e));
    }
  }
  const closePreview = () => {
    ++previewGeneration.current;
    setPreviewSession(null);
    setPreview(null);
    setPreviewBusy(false);
  };
  async function loadPreview(session: Session, offset = 0) {
    const token = ++previewGeneration.current;
    setPreviewSession(session);
    setPreviewBusy(true);
    setPreviewError("");
    if (!offset) setPreview(null);
    try {
      const result = await api.read<Preview>({
        action: "preview",
        provider,
        home: catalog?.home || home,
        projectId,
        id: session.id,
        sessionPath: session.path,
        offset,
        language: lang,
      });
      if (token === previewGeneration.current)
        setPreview((old) => ({
          ...result,
          messages:
            offset && old
              ? [...old.messages, ...result.messages]
              : result.messages,
        }));
    } catch (e) {
      if (token === previewGeneration.current) setPreviewError(String(e));
    } finally {
      if (token === previewGeneration.current) setPreviewBusy(false);
    }
  }
  function changeProvider(next: Provider) {
    if (busy || previewBusy || next === provider) return;
    setProvider(next);
    setHome("");
    setHomeDraft("");
    remember("preview.provider", next);
  }
  function toggle(id: string) {
    setSelected((old) => {
      const next = new Set(old);
      next.has(id) ? next.delete(id) : next.add(id);
      return next;
    });
  }
  return (
    <div className="app">
      <header className="titlebar">
        <span className="brand-mark">
          <Icon name="chat" />
        </span>
        <strong>{t("AI 对话管理器", "AI Conversation Manager")}</strong>
        <div className="segmented provider-switch" aria-label="Provider">
          {(["codex", "dsh"] as Provider[]).map((p) => (
            <Button
              key={p}
              aria-pressed={provider === p}
              disabled={busy || previewBusy}
              onClick={() => changeProvider(p)}
            >
              {p === "codex" ? "Codex" : "DSH"}
            </Button>
          ))}
        </div>
        <nav className="navigation" aria-label={t("工作区", "Workspace")}>
          <Button
            className={page === "manage" ? "nav active" : "nav"}
            onClick={() => setPage("manage")}
          >
            {t("管理与备份", "Manage and Back Up")}
          </Button>
          <Button
            className={page === "import" ? "nav active" : "nav"}
            onClick={() => setPage("import")}
          >
            <Icon name="download" />
            {t("导入与恢复", "Import and Restore")}
          </Button>
        </nav>
        <div className="spacer" />
        <Updates
          lang={lang}
          enabled={gateway === nativeGateway && isTauri()}
          busy={catalogBusy || operationBusy}
          onBusy={setUpdateBusy}
        />
        <Button
          variant="text"
          className="trash-entry"
          disabled={busy || demo}
          onClick={() => setCommand({ action: "trash", ids: [] })}
        >
          <Icon name="archive" />
          {t("回收站", "Trash")}
        </Button>
        <Menu
          label={t("设置与维护", "Settings and maintenance")}
          icon={<Icon name="settings" />}
        >
          <span className="menu-caption">
            {t("设置与维护", "Settings and maintenance")}
          </span>
          <Button
            variant="text"
            disabled={busy || demo}
            onClick={() => {
              setHomeDraft(home || catalog?.home || "");
              setHomeError("");
              setHomeOpen(true);
            }}
          >
            {t("数据目录", "Data folder")}
          </Button>
          <Button
            variant="text"
            disabled={busy || demo}
            onClick={() =>
              setCommand({
                action: provider === "dsh" ? "recover" : "repair",
                ids: [],
              })
            }
          >
            {provider === "dsh"
              ? t("中断恢复", "Recover")
              : t("检查修复", "Repair")}
          </Button>
          <Button
            variant="text"
            disabled={busy || previewBusy}
            onClick={() => {
              const next = lang === "zh" ? "en" : "zh";
              setLang(next);
              remember("preview.language", next);
            }}
          >
            {lang === "zh" ? "English" : "简体中文"}
          </Button>
          <Button
            variant="text"
            disabled={busy || previewBusy}
            onClick={() => {
              setDemo(!demo);
              setHome("");
            }}
          >
            {demo
              ? t("读取本机数据", "Use local data")
              : t("查看演示", "View demo")}
          </Button>
          <span className="menu-caption">
            {t(`v${appVersion} · 本地运行`, `v${appVersion} · Local`)}
          </span>
        </Menu>
      </header>
      {demo && (
        <div className="demo-banner">
          {t("演示模式 · 以下为合成数据", "Demo mode · synthetic data")}
          <Button
            variant="text"
            onClick={() => {
              setDemo(false);
              setHome("");
            }}
          >
            {t("读取本机数据", "Use local data")}
            <Icon name="chevron" />
          </Button>
        </div>
      )}
      <Operations
        key={provider + home + demo}
        gateway={api}
        provider={provider}
        home={catalog?.home || home || operationsHome.current}
        lang={lang}
        demo={demo}
        page={page}
        catalog={catalog}
        command={command}
        clearCommand={() => setCommand(null)}
        onBusy={setOperationBusy}
        onRefresh={() => setRefresh((v) => v + 1)}
        onFocusSession={(id) => {
          const p = catalog?.projects.find((item) =>
            item.sessions.some((s) => s.id === id),
          );
          if (!p) {
            setNotice(
              t(
                "未在目录中找到子代理，请刷新后按编号搜索。",
                "Subagent not found in the catalog. Refresh and search by ID.",
              ),
            );
            return;
          }
          setPage("manage");
          setProjectId(p.id);
          setKind(
            p.sessions.find((s) => s.id === id)?.subagent ? "subagent" : "main",
          );
          setArchive("all");
          setQuery(id);
          setSelected(new Set([id]));
        }}
      />
      {page === "manage" ? (
        <>
          <main className="workspace">
            <Card as="aside" className="projects">
              <div className="sidebar-heading">
                <h2>{t("项目", "Projects")}</h2>
                <span className="count">{catalog?.projects.length || 0}</span>
              </div>
              <label className="search-field project-search">
                <Icon name="search" />
                <Input
                  aria-label={t("搜索项目", "Search projects")}
                  placeholder={t("搜索项目…", "Search projects…")}
                  value={projectQuery}
                  onChange={(e) => setProjectQuery(e.target.value)}
                />
              </label>
              <div className="project-list">
                {filteredProjects.map((p) => (
                  <Button
                    key={p.id}
                    aria-pressed={p.id === projectId}
                    className={`project ${p.id === projectId ? "selected" : ""}`}
                    onClick={() => {
                      setProjectId(p.id);
                      setQuery("");
                    }}
                  >
                    <Icon name="folder" />
                    <span className="project-label">
                      <strong title={p.title}>{p.title}</strong>
                      <small>
                        {p.sessions.filter((s) => !s.subagent).length}{" "}
                        {t("主对话", "main")} ·{" "}
                        {p.sessions.filter((s) => s.subagent).length}{" "}
                        {t("子代理", "subagents")}
                      </small>
                    </span>
                  </Button>
                ))}
                {!catalogBusy && !filteredProjects.length && (
                  <p className="sidebar-empty">
                    {projectQuery
                      ? t("没有匹配的项目", "No matching projects")
                      : t("暂无项目", "No projects yet")}
                  </p>
                )}
              </div>
            </Card>
            <Card className="conversations" aria-busy={busy}>
              <div className="project-heading">
                <div className="row">
                  <h2>{project?.title || t("对话列表", "Conversations")}</h2>
                  <div className="spacer" />
                  <Button
                    variant="text"
                    aria-label={t("刷新", "Refresh")}
                    title={t("刷新", "Refresh")}
                    disabled={busy}
                    onClick={() => setRefresh((v) => v + 1)}
                  >
                    <Icon name="refresh" />
                  </Button>
                  <Button
                    variant="text"
                    disabled={!project?.path}
                    onClick={() => project && void copy(project.path)}
                  >
                    {t("复制路径", "Copy path")}
                  </Button>
                </div>
                <p className="path" title={project?.path}>
                  {project?.path ||
                    catalog?.home ||
                    t(
                      "选择数据源后读取本地记录",
                      "Select a provider to read local records",
                    )}
                </p>
                <small>
                  {sessions.filter((s) => !s.subagent).length}{" "}
                  {t("个主对话", "main conversations")} ·{" "}
                  {sessions.filter((s) => s.subagent).length}{" "}
                  {t("个子代理对话", "subagent conversations")}
                </small>
              </div>
              {project && (
                <small className="storage-summary">
                  {storage
                    ? storage.error ||
                      t(
                        `项目文件：${formatBytes(storage.bytes)} · ${storage.files} 个文件${storage.skipped ? ` · 跳过 ${storage.skipped} 项` : ""}`,
                        `Project files: ${formatBytes(storage.bytes)} · ${storage.files} files${storage.skipped ? ` · ${storage.skipped} skipped` : ""}`,
                      )
                    : t("项目文件：正在统计…", "Measuring project files…")}
                </small>
              )}
              <div className="toolbar">
                <div className="row">
                  <label className="search-field conversation-search">
                    <Icon name="search" />
                    <Input
                      aria-label={t("筛选对话", "Search conversations")}
                      placeholder={t("搜索对话…", "Search conversations…")}
                      value={query}
                      onChange={(e) => {
                        setQuery(e.target.value);
                      }}
                    />
                  </label>
                  <div className="segmented">
                    {(["main", "subagent"] as const).map((k) => (
                      <Button
                        key={k}
                        aria-pressed={kind === k}
                        onClick={() => {
                          setKind(k);
                        }}
                      >
                        {k === "main"
                          ? t("主对话", "Main")
                          : t("子代理", "Subagents")}{" "}
                        {
                          sessions.filter(
                            (s) => s.subagent === (k === "subagent"),
                          ).length
                        }
                      </Button>
                    ))}
                  </div>
                  <Button
                    disabled={!visible.length}
                    onClick={() =>
                      setSelected((old) => {
                        const next = new Set(old);
                        visible.forEach((s) =>
                          allVisibleSelected
                            ? next.delete(s.id)
                            : next.add(s.id),
                        );
                        return next;
                      })
                    }
                  >
                    {allVisibleSelected
                      ? t("全不选", "Clear")
                      : t("全选", "Select all")}
                  </Button>
                  <Button
                    disabled={busy || demo || !selectedVisible.length}
                    onClick={() =>
                      setCommand({
                        action: "delete",
                        ids: selectedVisible.map((s) => s.id),
                        batch: true,
                      })
                    }
                    className="danger"
                  >
                    {t("删除所选", "Delete selected")}
                  </Button>
                </div>
                <div className="row filters">
                  {(["all", "active", "archived"] as ArchiveFilter[]).map(
                    (a) => (
                      <Button
                        key={a}
                        aria-pressed={archive === a}
                        onClick={() => {
                          setArchive(a);
                        }}
                      >
                        {a === "all"
                          ? t("全部", "All")
                          : a === "active"
                            ? t("未归档", "Active")
                            : t("已归档", "Archived")}{" "}
                        (
                        {
                          typed.filter(
                            (s) =>
                              a === "all" || s.archived === (a === "archived"),
                          ).length
                        }
                        )
                      </Button>
                    ),
                  )}
                  <div className="spacer" />
                  {provider === "dsh" && (
                    <>
                      <Button
                        disabled={busy || demo || !selectedVisible.length}
                        onClick={() =>
                          setCommand({
                            action: "archive",
                            ids: selectedVisible.map((s) => s.id),
                          })
                        }
                      >
                        {t("归档所选", "Archive")}
                      </Button>
                      <Button
                        disabled={busy || demo || !selectedVisible.length}
                        onClick={() =>
                          setCommand({
                            action: "unarchive",
                            ids: selectedVisible.map((s) => s.id),
                          })
                        }
                      >
                        {t("取消归档", "Unarchive")}
                      </Button>
                    </>
                  )}
                </div>
              </div>
              <div className="session-list">
                {busy ? (
                  <p className="empty" role="status">
                    {t("正在读取本地对话…", "Reading local conversations…")}
                  </p>
                ) : error ? (
                  <div className="empty error" role="alert">
                    <strong>
                      {t("读取失败", "Could not read conversations")}
                    </strong>
                    <p>{error}</p>
                    <Button onClick={() => setRefresh((v) => v + 1)}>
                      {t("重试", "Retry")}
                    </Button>
                  </div>
                ) : !visible.length ? (
                  <p className="empty">
                    {t("没有符合条件的对话", "No matching conversations")}
                  </p>
                ) : (
                  visible.map((s) => (
                    <article
                      className={`session ${selected.has(s.id) ? "selected" : ""}`}
                      key={s.id}
                    >
                      <Input
                        type="checkbox"
                        aria-label={`${t("选择", "Select")} ${s.title}`}
                        checked={selected.has(s.id)}
                        onChange={() => toggle(s.id)}
                      />
                      <div className="session-detail">
                        <Button
                          variant="text"
                          className="session-title"
                          onClick={() => void loadPreview(s)}
                        >
                          {s.title}
                        </Button>
                        <small>
                          {s.archived
                            ? t("已归档", "Archived")
                            : t("未归档", "Active")}{" "}
                          · {s.updated} · {s.source} · {formatBytes(s.bytes)}
                        </small>
                        {s.subagent && (
                          <small>
                            {t("所属主对话：", "Parent: ")}
                            {s.parentTitle ||
                              s.parentId ||
                              t("未找到", "Not found")}
                          </small>
                        )}
                      </div>
                      <Button
                        className="text-button row-preview"
                        onClick={() => void loadPreview(s)}
                      >
                        {t("查看", "View")}
                      </Button>
                      <Menu
                        label={t("对话操作", "Conversation actions")}
                        icon={<Icon name="more" />}
                      >
                        <Button variant="text" onClick={() => void copy(s.id)}>
                          {t("复制 Thread ID", "Copy Thread ID")}
                        </Button>
                        <Button
                          variant="text"
                          onClick={() => void copy(s.path)}
                        >
                          {t("复制文件路径", "Copy file path")}
                        </Button>
                        <Button
                          variant="text"
                          className="danger"
                          disabled={busy || demo}
                          onClick={() =>
                            setCommand({ action: "delete", ids: [s.id] })
                          }
                        >
                          {t("删除…", "Delete…")}
                        </Button>
                      </Menu>
                    </article>
                  ))
                )}
              </div>
              <div className="selection-footer">
                <div>
                  <strong>
                    {t(
                      `已选 ${selected.size} 个对话`,
                      `Selected ${selected.size} conversations`,
                    )}
                  </strong>
                  <small>
                    {t(
                      selected.size
                        ? `跨项目备份 ${selectedMain.length} 个主对话 · 删除限当前列表 ${selectedVisible.length} 项`
                        : "勾选对话，或创建整个项目的备份",
                      selected.size
                        ? `${selectedMain.length} main conversations across projects · Delete: ${selectedVisible.length} visible items`
                        : "Select conversations, or back up an entire project",
                    )}
                  </small>
                </div>
                <Button
                  className={selected.size ? "" : "quiet-clear"}
                  variant="text"
                  disabled={!selected.size || busy}
                  onClick={() => setSelected(new Set())}
                >
                  {t("清空全部选择", "Clear all selections")}
                </Button>
                <Button
                  className="primary"
                  disabled={busy || !catalog?.projects.length}
                  onClick={() =>
                    setCommand({
                      action: "backup",
                      ids: selectedMain.map((s) => s.id),
                    })
                  }
                >
                  <Icon name="upload" />
                  {t("创建备份…", "Create backup…")}
                </Button>
              </div>
            </Card>
          </main>
        </>
      ) : null}
      <footer className="status" title={catalog?.warnings.join("\n")}>
        <span>●</span>
        {notice && <span role="status">{notice}</span>}
        {busy
          ? t("正在加载…", "Loading…")
          : t(
              `已载入 ${catalog?.projects.length || 0} 个项目`,
              `Loaded ${catalog?.projects.length || 0} projects`,
            )}
        {catalog?.warnings.length ? (
          <details>
            <summary>
              {t("读取提示", "Read diagnostics")} ({catalog.warnings.length})
            </summary>
            <div>
              {catalog.warnings.map((w, i) => (
                <p key={i}>{w}</p>
              ))}
            </div>
          </details>
        ) : null}
      </footer>
      <Dialog
        ref={previewDialog}
        className="preview-dialog"
        aria-labelledby="preview-title"
        onCancel={closePreview}
      >
        <div className="dialog-heading">
          <div>
            <h2 id="preview-title">{previewSession?.title}</h2>
            <small>
              {t("对话预览", "Conversation preview")} · {preview?.total || 0}{" "}
              {t("条消息", "messages")}
            </small>
          </div>
          <Button
            aria-label={t("关闭预览", "Close preview")}
            onClick={closePreview}
          >
            ✕
          </Button>
        </div>
        <details className="preview-metadata">
          <summary>{t("对话信息", "Conversation details")}</summary>
          <span>Thread ID: {previewSession?.id}</span>
          <span>{previewSession?.path}</span>
        </details>
        {preview && previewSession && (
          <ConversationView
            key={previewSession.id + (preview.revision ?? "")}
            initial={preview}
            lang={lang}
            provider={provider}
            loadPage={(offset) =>
              api.read<Preview>({
                action: "preview",
                provider,
                home: catalog?.home || home,
                projectId,
                id: previewSession.id,
                sessionPath: previewSession.path,
                offset,
                language: lang,
              })
            }
          />
        )}
        {previewError && <p role="alert">{previewError}</p>}
        {previewBusy && (
          <p role="status">{t("正在读取消息…", "Reading messages…")}</p>
        )}
        <div className="preview-footer">
          <Button
            disabled={!previewSession}
            onClick={() => previewSession && void copy(previewSession.id)}
          >
            {t("复制 Thread ID", "Copy Thread ID")}
          </Button>
          {notice && <small role="status">{notice}</small>}
        </div>
      </Dialog>
      <Dialog
        ref={homeDialog}
        className="home-dialog"
        aria-label={t("选择数据目录", "Choose a data folder")}
        onCancel={() => setHomeOpen(false)}
      >
        <form
          onSubmit={(e) => {
            e.preventDefault();
            setHome(homeDraft.trim());
            setHomeOpen(false);
            setRefresh((v) => v + 1);
          }}
        >
          <h2>
            {t("选择数据目录", "Choose a data folder")} ·{" "}
            {provider === "codex" ? "Codex" : "DSH"}
          </h2>
          <label>
            {t(
              "数据目录绝对路径（留空使用默认值）",
              "Absolute data folder path (empty uses default)",
            )}
            <Input
              autoFocus
              value={homeDraft}
              onChange={(e) => setHomeDraft(e.target.value)}
              placeholder={
                provider === "codex"
                  ? "C:\\Users\\you\\.codex"
                  : "C:\\Users\\you\\.dsh"
              }
            />
          </label>
          <Button
            onClick={async () => {
              try {
                const path = await open({
                  directory: true,
                  multiple: false,
                  defaultPath: homeDraft || undefined,
                });
                if (typeof path === "string") setHomeDraft(path);
              } catch (e) {
                setHomeError(String(e));
              }
            }}
          >
            {t("浏览目录…", "Browse folder…")}
          </Button>
          {homeError && (
            <p className="error" role="alert">
              {homeError}
            </p>
          )}
          <p className="muted">
            {t(
              "仅用于当前数据源，本窗口关闭后不保存路径。",
              "Applies only to the current provider. Paths are not saved between launches.",
            )}
          </p>
          <div className="row">
            <Button type="button" onClick={() => setHomeOpen(false)}>
              {t("取消", "Cancel")}
            </Button>
            <Button className="primary" type="submit">
              {t("读取目录", "Read folder")}
            </Button>
          </div>
        </form>
      </Dialog>
    </div>
  );
}

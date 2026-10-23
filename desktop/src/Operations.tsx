import { useEffect, useRef, useState } from "react";
import { open, save } from "@tauri-apps/plugin-dialog";
import { Button, Card, Dialog, Input, Select } from "./components/ui";
import { formatBytes } from "./types";
import type {
  Catalog,
  Gateway,
  Language,
  OperationAction,
  OperationRequest,
  Provider,
  PackageSummary,
} from "./types";

export type Command = {
  action:
    | "backup"
    | "delete"
    | "archive"
    | "unarchive"
    | "trash"
    | "repair"
    | "recover";
  ids: string[];
  batch?: boolean;
};
type TrashItem = {
  id: string;
  title: string;
  path: string;
  deleted: string;
  bytes: number;
  projectHandled: boolean;
  originalPath?: string;
};
type DeletePlan = {
  token: string;
  sessions: { id: string; title: string; subagent: boolean }[];
  paths: string[];
  remaining: number;
};
type RepairPlan = {
  diagnostic?: string;
  token: string;
  ids: string[];
  snapshotCount?: number;
  cacheCleanup?: boolean;
  blocked?: { id: string; rootId: string; title: string; path: string }[];
};
type Props = {
  gateway: Gateway;
  provider: Provider;
  home: string;
  lang: Language;
  demo: boolean;
  page: string;
  catalog: Catalog | null;
  command: Command | null;
  clearCommand: () => void;
  onRefresh: () => void;
  onBusy: (value: boolean) => void;
  onFocusSession: (id: string) => void;
};

export function Operations({
  gateway,
  provider,
  home,
  lang,
  demo,
  page,
  catalog,
  command,
  clearCommand,
  onRefresh,
  onBusy,
  onFocusSession,
}: Props) {
  const t = (zh: string, en: string) => (lang === "zh" ? zh : en);
  const [files, setFiles] = useState(false),
    [output, setOutput] = useState("");
  const [projects, setProjects] = useState<string[]>([]);
  const [packagePath, setPackage] = useState(""),
    [target, setTarget] = useState("");
  const [summary, setSummary] = useState<PackageSummary | null>(null);
  const [summaryBusy, setSummaryBusy] = useState(false),
    [summaryError, setSummaryError] = useState("");
  const [projectConfirmation, setProjectConfirmation] = useState("");
  const [repairBlocked, setRepairBlocked] = useState<
    NonNullable<RepairPlan["blocked"]>
  >([]);
  const [map, setMap] = useState(true),
    [restoreFiles, setRestoreFiles] = useState(false);
  const [mode, setMode] = useState<"merge" | "copy">("merge"),
    [conflict, setConflict] = useState<"empty" | "skip" | "overwrite">("empty");
  const [active, setActive] = useState<Command | null>(null);
  const [running, setRunning] = useState(false),
    [log, setLog] = useState(""),
    [error, setError] = useState("");
  const [trash, setTrash] = useState<TrashItem[] | null>(null);
  const [plan, setPlan] = useState<DeletePlan | null>(null);
  const [permanent, setPermanent] = useState(false),
    [projectMode, setProjectMode] = useState<"none" | "recycle" | "permanent">(
      "none",
    );
  const [confirmation, setConfirmation] = useState<{
    description: string;
    request: Partial<OperationRequest> & { action: OperationAction };
    paths?: string[];
  } | null>(null);
  const dialog = useRef<HTMLDialogElement>(null),
    locked = useRef(false);
  const unavailable = demo || !gateway.operate;
  const base = { provider, home, language: lang };
  const allowedProjects =
    catalog?.projects.filter((p) =>
      files
        ? p.canBackupFiles !== false && p.sessions.some((s) => !s.subagent)
        : p.sessions.some((s) => !s.subagent),
    ) || [];
  const selectedProjects = projects.filter((id) =>
    allowedProjects.some((p) => p.id === id),
  );
  const backupScope =
    catalog?.projects.flatMap((p) =>
      p.sessions.filter((s) =>
        files
          ? selectedProjects.includes(p.id)
          : !s.subagent &&
            (active?.ids.includes(s.id) || selectedProjects.includes(p.id)),
      ),
    ) || [];
  const backupBytes = backupScope.reduce((sum, s) => sum + s.bytes, 0);
  function expectedNames(paths: string[]) {
    return paths
      .map(
        (p) =>
          p
            .replace(/[\\/]+$/, "")
            .split(/[\\/]/)
            .pop() || "",
      )
      .join("\n");
  }
  const deleteNames = expectedNames(plan?.paths || []);
  const deletingSessions =
    catalog?.projects
      .flatMap((p) => p.sessions)
      .filter((s) => active?.ids.includes(s.id)) || [];
  const canProcessProject =
    deletingSessions.length > 0 &&
    deletingSessions.every((s) => !s.subagent) &&
    (!active?.batch ||
      (catalog?.projects
        .filter((p) => p.sessions.some((s) => active.ids.includes(s.id)))
        .every((p) =>
          p.sessions
            .filter((s) => !s.subagent)
            .every((s) => active.ids.includes(s.id)),
        ) ??
        false));
  function show() {
    dialog.current?.showModal();
  }
  function close() {
    if (locked.current) return;
    dialog.current?.close();
    setActive(null);
    setConfirmation(null);
    setPlan(null);
    setProjectConfirmation("");
    setRepairBlocked([]);
  }
  async function call<T>(
    request: Partial<OperationRequest> & { action: OperationAction },
  ): Promise<T> {
    if (unavailable)
      throw new Error(
        t(
          "演示数据不支持写入，请切换本机数据。",
          "Switch to local data to perform operations.",
        ),
      );
    return gateway.operate!<T>({ ...base, ...request });
  }
  async function run<T>(
    request: Partial<OperationRequest> & { action: OperationAction },
    done?: (result: T) => void,
  ) {
    if (locked.current) return;
    locked.current = true;
    setRunning(true);
    onBusy(true);
    setError("");
    setLog("");
    show();
    try {
      const result = await call<T>(request);
      if (done) done(result);
      else {
        setLog(
          (result as { message?: string }).message ||
            t("操作完成", "Completed"),
        );
        setConfirmation(null);
        setPlan(null);
        if (["restore", "purge", "trashProject"].includes(request.action)) {
          setActive({ action: "trash", ids: [] });
          setTrash(
            (await call<{ items: TrashItem[] }>({ action: "trash" })).items,
          );
        } else {
          setActive(null);
          setTrash(null);
        }
        if (
          ![
            "packageInfo",
            "inspect",
            "backup",
            "trash",
            "planDelete",
            "planRepair",
          ].includes(request.action)
        )
          onRefresh();
      }
    } catch (e) {
      setError(String(e));
      setConfirmation(null);
      setPlan(null);
      if (
        ![
          "packageInfo",
          "inspect",
          "backup",
          "trash",
          "planDelete",
          "planRepair",
        ].includes(request.action)
      )
        onRefresh();
    } finally {
      locked.current = false;
      setRunning(false);
      onBusy(false);
    }
  }
  useEffect(() => {
    setProjects([]);
    setOutput("");
    setPackage("");
    setTarget("");
    setLog("");
    setError("");
    close();
  }, [provider, home]);
  useEffect(() => {
    let current = true;
    setSummary(null);
    setSummaryError("");
    setRestoreFiles(false);
    setTarget("");
    if (!packagePath.trim() || unavailable) {
      setSummaryBusy(false);
      return;
    }
    setSummaryBusy(true);
    const timer = setTimeout(() => {
      call<PackageSummary>({
        action: "packageInfo",
        package: packagePath.trim(),
      })
        .then((info) => {
          if (current) {
            setSummary(info);
            setRestoreFiles(info.hasFiles);
            setConflict("empty");
            setTarget(info.suggestedTarget);
          }
        })
        .catch((e) => {
          if (current) setSummaryError(String(e));
        })
        .finally(() => {
          if (current) setSummaryBusy(false);
        });
    }, 300);
    return () => {
      current = false;
      clearTimeout(timer);
    };
  }, [packagePath, provider, home, unavailable]);
  function maintenance(action: "repair" | "recover") {
    setActive(null);
    setTrash(null);
    setLog("");
    setError("");
    setConfirmation(null);
    setRepairBlocked([]);
    if (action === "recover") {
      setConfirmation({
        description: t(
          "退出 DSH 后恢复中断的管理器操作。",
          "Close DSH, then recover interrupted manager operations.",
        ),
        request: { action: "recover" },
      });
      show();
    } else
      void run<RepairPlan>({ action: "planRepair" }, (result) => {
        setRepairBlocked(result.blocked || []);
        if (result.diagnostic) {
          setLog(result.diagnostic);
          return;
        }
        if (
          !result.ids.length &&
          !result.snapshotCount &&
          !result.cacheCleanup
        ) {
          setLog(
            t(
              "未发现可直接修复的侧栏残留或遗留快照。",
              "No sidebar entries or legacy snapshots can be repaired directly.",
            ),
          );
          return;
        }
        setConfirmation({
          description:
            t(
              "修复以下无效侧栏记录：\n",
              "Repair these stale sidebar entries:\n",
            ) +
            result.ids.join("\n") +
            (result.snapshotCount
              ? t(
                  `\n整理 ${result.snapshotCount} 个遗留事务快照：每组保留一份到软件回收站，清理重复副本。`,
                  `\nProcess ${result.snapshotCount} legacy transaction snapshots: keep one per group in app trash and remove redundant copies.`,
                )
              : "") +
            (result.cacheCleanup
              ? t(
                  "\n刷新已完成修复的桌面缓存。",
                  "\nRefresh desktop caches for completed repairs.",
                )
              : ""),
          request: { action: "repair", token: result.token },
        });
      });
  }
  useEffect(() => {
    if (!command) return;
    const next = command;
    clearCommand();
    setLog("");
    setError("");
    setTrash(null);
    setPlan(null);
    setProjectConfirmation("");
    setRepairBlocked([]);
    setPermanent(false);
    setProjectMode("none");
    setConfirmation(null);
    setActive(next);
    if (next.action === "backup") {
      setProjects([]);
      setFiles(false);
      setOutput("");
    }
    show();
    if (next.action === "repair" || next.action === "recover") {
      maintenance(next.action);
      return;
    }
    if (next.action === "trash")
      void run<{ items: TrashItem[] }>({ action: "trash" }, (value) =>
        setTrash(value.items),
      );
    if (next.action === "archive" || next.action === "unarchive")
      setConfirmation({
        description: t(
          `更新所选 ${next.ids.length} 条对话的归档状态。请先退出 DSH。`,
          `Update archive status for ${next.ids.length} selected conversations. Close DSH first.`,
        ),
        request: { action: next.action, ids: next.ids },
      });
  }, [command]);
  async function pick(kind: "output" | "package" | "target") {
    try {
      const extension =
        provider === "codex"
          ? files
            ? "codexproject"
            : "codexchat"
          : files
            ? "dshproject"
            : "dshchat";
      const value =
        kind === "output"
          ? await save({
              defaultPath: `backup-${new Date().toISOString().replace(/[:.]/g, "-")}.${extension}`,
              filters: [
                { name: t("备份包", "Backup"), extensions: [extension] },
              ],
            })
          : await open({
              directory: kind === "target",
              multiple: false,
              filters:
                kind === "package"
                  ? [
                      {
                        name: t("备份包", "Backup"),
                        extensions:
                          provider === "codex"
                            ? [
                                "codexchat",
                                "codexproject",
                                "codexpack",
                                "codexbundle",
                              ]
                            : ["dshchat", "dshproject"],
                      },
                    ]
                  : undefined,
            });
      if (typeof value === "string")
        (kind === "output"
          ? setOutput
          : kind === "package"
            ? setPackage
            : setTarget)(value);
    } catch (e) {
      setError(String(e));
      show();
    }
  }
  const importRequest = {
    package: packagePath,
    target,
    map,
    restoreFiles,
    mode,
    conflict,
  };
  function prepareImport() {
    setActive(null);
    setTrash(null);
    setError("");
    setLog("");
    setConfirmation({
      description: `${packagePath}\n→ ${home}\n${map || restoreFiles ? target : t("保留原项目路径", "Keep original project paths")}\n${mode === "copy" ? t("生成独立副本", "Create independent copies") : t("按来源智能合并", "Merge by origin")}\n${restoreFiles ? t("还原项目文件：", "Restore project files: ") + conflict : t("仅导入对话", "Conversations only")}\n${t("请先退出目标客户端。", "Close the destination client first.")}`,
      request: { action: "import", ...importRequest },
    });
    show();
  }
  return (
    <>
      {page === "import" && (
        <main className="import-page">
          <Card className="operation-form">
            <div className="import-heading">
              <span className="eyebrow">
                {t("导入与恢复", "IMPORT & RESTORE")}
              </span>
              <h2>{t("让备份回到工作区", "Bring your work back")}</h2>
              <p className="muted">
                {t(
                  "选择备份包，确认内容与目标位置，再开始导入。",
                  "Choose a backup, review its contents and destination, then import.",
                )}
              </p>
            </div>
            <span className="section-label">
              {t("01 · 选择备份", "01 · Choose a backup")}
            </span>
            <label>
              {t("备份包", "Backup package")}
              <div className="row">
                <Input
                  aria-label={t("备份包", "Backup package")}
                  value={packagePath}
                  disabled={running}
                  onChange={(e) => setPackage(e.target.value)}
                />
                <Button
                  disabled={unavailable || running}
                  onClick={() => void pick("package")}
                >
                  {t("浏览", "Browse")}
                </Button>
              </div>
            </label>
            {summaryBusy && (
              <p className="muted" role="status">
                {t("正在读取备份包摘要…", "Reading package summary…")}
              </p>
            )}
            {summaryError && (
              <p className="error" role="alert">
                {summaryError}
              </p>
            )}
            {summary && (
              <section
                className="package-summary"
                aria-label={t("备份包摘要", "Package summary")}
              >
                <strong>
                  {summary.raw
                    ? t(
                        "原始 .codexbundle（检查时验证内容）",
                        "Raw .codexbundle (validated during inspection)",
                      )
                    : t(
                        `${summary.projects.length} 个项目 · ${summary.mainCount} 个主对话 · ${summary.subagentCount} 个子代理`,
                        `${summary.projects.length} projects · ${summary.mainCount} main conversations · ${summary.subagentCount} subagents`,
                      )}
                </strong>
                <small>
                  {summary.created} · {formatBytes(summary.bytes)}
                </small>
                {summary.projects.map((p, i) => (
                  <div key={i}>
                    {p.title}
                    <span className="path" title={p.path}>
                      {p.path}
                    </span>
                  </div>
                ))}
                {summary.hasFiles && (
                  <p>
                    {t(
                      `包含项目文件：${summary.fileCount} 个 · ${formatBytes(summary.fileBytes)}`,
                      `Project files: ${summary.fileCount} · ${formatBytes(summary.fileBytes)}`,
                    )}
                  </p>
                )}
                {summary.projects.length > 1 && (
                  <p>
                    {t(
                      "请选择项目总目录，各项目会还原到独立子目录。",
                      "Choose a parent folder; each project gets its own subfolder.",
                    )}
                  </p>
                )}
              </section>
            )}
            <span className="section-label">
              {t("02 · 还原位置", "02 · Destination")}
            </span>
            <label>
              <Input
                type="checkbox"
                checked={map || restoreFiles}
                disabled={running || restoreFiles}
                onChange={(e) => setMap(e.target.checked)}
              />
              {t(
                "映射到新的项目目录（多项目时选择总目录）",
                "Map project paths (choose a parent folder for multiple projects)",
              )}
            </label>
            <div className="row">
              <Input
                aria-label={t("目标项目目录", "Target project folder")}
                value={target}
                disabled={running || (!map && !restoreFiles)}
                onChange={(e) => setTarget(e.target.value)}
              />
              <Button
                disabled={unavailable || (!map && !restoreFiles)}
                onClick={() => void pick("target")}
              >
                {t("选择目录", "Choose folder")}
              </Button>
            </div>
            <span className="section-label">
              {t("03 · 导入选项", "03 · Import options")}
            </span>
            <label>
              {t("对话冲突策略", "Conversation conflicts")}
              <Select
                label={t("对话冲突策略", "Conversation conflicts")}
                disabled={running}
                value={mode}
                onValueChange={(value) => setMode(value as typeof mode)}
                options={[
                  {
                    value: "merge",
                    label: t("智能合并同源历史", "Merge matching history"),
                  },
                  {
                    value: "copy",
                    label: t("创建独立副本", "Create independent copies"),
                  },
                ]}
              />
            </label>
            <p className="field-help">
              {mode === "merge"
                ? t(
                    "按目标项目和原始编号匹配：已有记录合并，首次迁入生成新编号。",
                    "Match by target project and original ID: merge existing history; create a new ID on first import.",
                  )
                : t(
                    "每次生成独立编号并重写父子关系，两份对话互不共用文件。",
                    "Generate independent IDs and rewrite parent relationships; copies do not share files.",
                  )}
            </p>
            <label>
              <Input
                type="checkbox"
                checked={restoreFiles}
                disabled={running || !summary?.hasFiles}
                onChange={(e) => setRestoreFiles(e.target.checked)}
              />
              {t("还原包内项目文件", "Restore project files from the package")}
            </label>
            <label>
              {t("项目文件冲突策略", "Project file conflicts")}
              <Select
                label={t("项目文件冲突策略", "Project file conflicts")}
                disabled={running || !restoreFiles}
                value={conflict}
                onValueChange={(value) => setConflict(value as typeof conflict)}
                options={[
                  {
                    value: "empty",
                    label: t("要求空目录", "Require empty folder"),
                  },
                  {
                    value: "skip",
                    label: t("跳过同名文件", "Keep existing files"),
                  },
                  {
                    value: "overwrite",
                    label: t("备份后覆盖", "Back up before overwriting"),
                  },
                ]}
              />
            </label>
            <div className="row import-actions">
              <Button
                disabled={
                  unavailable ||
                  running ||
                  summaryBusy ||
                  !summary ||
                  ((map || restoreFiles) && !target.trim())
                }
                onClick={() => {
                  setActive(null);
                  setTrash(null);
                  void run({ action: "inspect", ...importRequest });
                }}
              >
                {t("先检查（不写入）", "Inspect without writing")}
              </Button>
              <Button
                variant="primary"
                disabled={
                  unavailable ||
                  running ||
                  summaryBusy ||
                  !summary ||
                  ((map || restoreFiles) && !target.trim())
                }
                onClick={prepareImport}
              >
                {t("开始导入…", "Import…")}
              </Button>
            </div>
            {log && <pre className="operation-log">{log}</pre>}
          </Card>
        </main>
      )}
      <Dialog
        ref={dialog}
        className={`operation-dialog ${!confirmation && active?.action === "backup" ? "backup-dialog" : ""}`}
        aria-labelledby="operation-title"
        onCancel={(e) => {
          if (locked.current) e.preventDefault();
          else close();
        }}
      >
        <div className="dialog-heading">
          <h2 id="operation-title">
            {active?.action === "backup"
              ? t("创建备份", "Create backup")
              : active?.action === "delete"
                ? t("删除确认", "Confirm deletion")
                : active?.action === "trash"
                  ? t("回收站", "Trash")
                  : t("操作详情", "Operation details")}
          </h2>
          <Button
            disabled={running}
            onClick={close}
            aria-label={t("关闭操作窗口", "Close operation")}
          >
            ✕
          </Button>
        </div>
        {running && (
          <p role="status">
            {t("正在处理，请保持窗口打开…", "Working; keep this window open…")}
          </p>
        )}
        {error && (
          <p className="error operation-log" role="alert">
            {error}
          </p>
        )}
        {log && <pre className="operation-log">{log}</pre>}
        {repairBlocked.length > 0 && (
          <section className="repair-blocked">
            <p>
              {t(
                "以下子代理的本地文件仍存在，其父记录暂不清理。可先定位子代理，备份或移入软件回收站后再次检查。",
                "These subagents still have local files, so their parents will be preserved. Locate and back up or trash them before checking again.",
              )}
            </p>
            {repairBlocked.map((item) => (
              <div
                key={`${item.rootId}-${item.id}`}
                className="repair-descendant"
              >
                <strong>{item.title || item.id}</strong>
                <small>
                  {t("父记录：", "Parent: ")}
                  {item.rootId}
                </small>
                <Button
                  disabled={running}
                  onClick={() => {
                    close();
                    onFocusSession(item.id);
                  }}
                >
                  {t("定位子代理", "Locate subagent")}
                </Button>
              </div>
            ))}
          </section>
        )}
        {!confirmation && active?.action === "backup" && (
          <div className="operation-form">
            <div className="backup-scroll">
              <span className="section-label">
                {t("01 · 备份内容", "01 · Backup content")}
              </span>
              <div className="mode">
                <Button
                  disabled={running}
                  aria-pressed={!files}
                  className={!files ? "chosen" : ""}
                  onClick={() => {
                    setFiles(false);
                    setOutput("");
                  }}
                >
                  {t("仅对话", "Conversations only")}
                </Button>
                <Button
                  disabled={running}
                  aria-pressed={files}
                  className={files ? "chosen" : ""}
                  onClick={() => {
                    setFiles(true);
                    setOutput("");
                    if (!projects.length)
                      setProjects(
                        catalog?.projects
                          .filter(
                            (p) =>
                              p.canBackupFiles !== false &&
                              p.sessions.some(
                                (s) => active.ids.includes(s.id) && !s.subagent,
                              ),
                          )
                          .map((p) => p.id) || [],
                      );
                  }}
                >
                  {t("项目 + 对话", "Projects + conversations")}
                </Button>
              </div>

              <p>
                {files
                  ? t(
                      "备份所选项目的全部对话（包含子代理）和项目文件。",
                      "Back up every conversation, including subagents, and files in the selected projects.",
                    )
                  : t(
                      "备份当前所选对话；勾选项目后额外备份该项目全部主对话。",
                      "Back up selected conversations; checked projects add all their main conversations.",
                    )}
              </p>
              <div className="backup-summary" role="status">
                <strong>
                  {t(
                    `本次备份 ${backupScope.length} 条对话`,
                    `${backupScope.length} conversations in this backup`,
                  )}
                </strong>
                <small>
                  {t(
                    `对话数据 ${formatBytes(backupBytes)}${files ? " · 另包含所选项目文件" : ""}`,
                    `${formatBytes(backupBytes)} of conversation data${files ? " · plus selected project files" : ""}`,
                  )}
                </small>
              </div>
              {demo && (
                <small>
                  {t(
                    "演示模式仅预览配置，不创建备份。",
                    "Demo mode previews settings without creating a backup.",
                  )}
                </small>
              )}
              <div className="operation-projects">
                <div className="row">
                  <Button
                    variant="text"
                    disabled={running || !allowedProjects.length}
                    onClick={() =>
                      setProjects(allowedProjects.map((p) => p.id))
                    }
                  >
                    {t("全选项目", "Select all projects")}
                  </Button>
                  <Button
                    variant="text"
                    disabled={running || !projects.length}
                    onClick={() => setProjects([])}
                  >
                    {t("清空项目", "Clear projects")}
                  </Button>
                  <small>
                    {t(
                      `已选 ${selectedProjects.length} 个项目`,
                      `${selectedProjects.length} projects selected`,
                    )}
                  </small>
                </div>
                {catalog?.projects.map((p) => (
                  <label key={p.id}>
                    <Input
                      type="checkbox"
                      checked={selectedProjects.includes(p.id)}
                      disabled={
                        running ||
                        !allowedProjects.some((item) => item.id === p.id)
                      }
                      onChange={(e) =>
                        setProjects((old) =>
                          e.target.checked
                            ? [...old, p.id]
                            : old.filter((id) => id !== p.id),
                        )
                      }
                    />
                    {p.title}
                    <small>
                      {p.path}
                      {files && p.canBackupFiles === false
                        ? t(
                            " · 项目目录不可备份",
                            " · Project files unavailable",
                          )
                        : ""}
                    </small>
                  </label>
                ))}
              </div>
            </div>
            <div className="backup-footer">
              <label>
                <span className="section-label">
                  {t("02 · 保存到新文件", "02 · Save to a new file")}
                </span>
                <div className="row">
                  <Input
                    aria-label={t("备份输出文件", "Backup output file")}
                    value={output}
                    disabled={running}
                    onChange={(e) => setOutput(e.target.value)}
                  />
                  <Button
                    disabled={running || unavailable}
                    onClick={() => void pick("output")}
                  >
                    {t("浏览", "Browse")}
                  </Button>
                </div>
              </label>
              <Button
                variant="primary"
                disabled={
                  running ||
                  unavailable ||
                  !output ||
                  (files
                    ? !selectedProjects.length
                    : !selectedProjects.length && !active.ids.length)
                }
                onClick={() =>
                  void run({
                    action: "backup",
                    ids: files ? [] : active.ids,
                    projectIds: selectedProjects,
                    includeFiles: files,
                    output,
                  })
                }
              >
                {t("创建备份", "Create backup")}
              </Button>
            </div>
          </div>
        )}
        {!confirmation && active?.action === "delete" && (
          <div className="operation-form">
            <label>
              <Input
                type="checkbox"
                checked={permanent}
                disabled={running}
                onChange={(e) => {
                  setPermanent(e.target.checked);
                  setPlan(null);
                }}
              />
              {t(
                "永久删除对话（不勾选则移入软件回收站）",
                "Permanently delete conversations (otherwise move to app trash)",
              )}
            </label>
            <label>
              {t("关联项目目录", "Associated project folders")}
              <Select
                label={t("关联项目目录", "Associated project folders")}
                disabled={running}
                value={projectMode}
                onValueChange={(value) => {
                  setProjectMode(value as typeof projectMode);
                  setPlan(null);
                  setProjectConfirmation("");
                }}
                options={[
                  { value: "none", label: t("保留", "Keep") },
                  {
                    value: "recycle",
                    label: t(
                      "移入 Windows 回收站",
                      "Move to Windows Recycle Bin",
                    ),
                    disabled: !canProcessProject,
                  },
                  {
                    value: "permanent",
                    label: t("永久递归删除", "Permanently delete recursively"),
                    disabled: !canProcessProject,
                  },
                ]}
              />
            </label>
            {!canProcessProject && (
              <p className="field-help">
                {t(
                  "项目目录处理仅供主对话使用；批量操作必须选齐该项目的全部主对话。",
                  "Project folders can only be processed from main conversations; batch actions require all main conversations in the project.",
                )}
              </p>
            )}
            <Button
              disabled={running}
              onClick={() =>
                void run<DeletePlan>(
                  {
                    action: "planDelete",
                    ids: active.ids,
                    batch: active.batch,
                    permanent,
                    projectMode,
                  },
                  setPlan,
                )
              }
            >
              {t("核对删除范围", "Review deletion scope")}
            </Button>
            {plan && (
              <>
                <p>
                  {t(
                    `将处理 ${plan.sessions.length} 条对话，包含关联子代理。`,
                    `${plan.sessions.length} conversations will be affected, including descendants.`,
                  )}
                </p>
                <ul className="operation-scope">
                  {plan.sessions.map((s) => (
                    <li key={s.id}>
                      {s.title} <small>{s.id}</small>
                    </li>
                  ))}
                </ul>
                {plan.paths.length > 0 && (
                  <pre className="operation-log">
                    {plan.paths.join("\n") +
                      "\n" +
                      t(
                        `这些目录还有 ${plan.remaining} 条未选择的对话引用。`,
                        `${plan.remaining} unselected conversations still reference these folders.`,
                      )}
                  </pre>
                )}
                <p>
                  {t(
                    "请先退出目标客户端。确认后会再次核对文件与父子关系。",
                    "Close the destination client. Files and ancestry will be checked again before deletion.",
                  )}
                </p>
                {projectMode === "permanent" && (
                  <label className="typed-confirmation">
                    {t(
                      `输入项目目录名以确认永久删除：${deleteNames}`,
                      `Type the project folder name to confirm permanent deletion: ${deleteNames}`,
                    )}
                    <Input
                      aria-label={t(
                        "永久删除项目确认",
                        "Confirm permanent project deletion",
                      )}
                      value={projectConfirmation}
                      disabled={running}
                      onChange={(e) => setProjectConfirmation(e.target.value)}
                      autoComplete="off"
                    />
                  </label>
                )}
                <Button
                  variant="danger"
                  disabled={
                    running ||
                    (projectMode === "permanent" &&
                      (!deleteNames ||
                        projectConfirmation.trim().toLocaleLowerCase() !==
                          deleteNames.toLocaleLowerCase()))
                  }
                  onClick={() =>
                    void run({
                      action: "delete",
                      ids: active.ids,
                      batch: active.batch,
                      projectConfirmation,
                      permanent,
                      projectMode,
                      token: plan.token,
                    })
                  }
                >
                  {projectMode === "permanent" && !permanent
                    ? t(
                        "确认永久删除项目并回收对话",
                        "Delete project permanently and trash conversations",
                      )
                    : permanent
                      ? t("确认永久删除", "Confirm permanent deletion")
                      : t("确认移入回收站", "Confirm move to trash")}
                </Button>
              </>
            )}
          </div>
        )}
        {!confirmation && trash && (
          <div className="operation-trash">
            {!trash.length && <p>{t("回收站为空", "Trash is empty")}</p>}
            {trash.map((item) => (
              <article key={item.id}>
                <strong>{item.title}</strong>
                <small>
                  {item.deleted} · {formatBytes(item.bytes)} · {item.path}
                </small>
                {item.originalPath && (
                  <small>
                    {t("原会话位置：", "Original conversation: ")}
                    {item.originalPath}
                  </small>
                )}
                <div className="row">
                  {(["restore", "purge", "trashProject"] as const).map(
                    (action) => (
                      <Button
                        key={action}
                        variant={action === "purge" ? "danger" : "default"}
                        disabled={
                          running ||
                          (action === "trashProject" && item.projectHandled)
                        }
                        onClick={() =>
                          setConfirmation({
                            description: `${item.title}\n${item.path}\n${action === "restore" ? t("恢复到原数据目录。", "Restore to the original data folder.") : action === "purge" ? t("永久清除此回收站记录，无法恢复。", "Permanently remove this trash entry; it cannot be restored.") : t("选择关联项目目录的处理方式。", "Choose how to process the associated project folder.")}`,
                            paths: [item.path],
                            request: {
                              action,
                              id: item.id,
                              projectMode: "recycle",
                            },
                          })
                        }
                      >
                        {action === "restore"
                          ? t("恢复…", "Restore…")
                          : action === "purge"
                            ? t("永久清除…", "Purge…")
                            : t("处理项目目录…", "Process project folder…")}
                      </Button>
                    ),
                  )}
                </div>
              </article>
            ))}
          </div>
        )}
        {confirmation && (
          <>
            <pre className="operation-log">{confirmation.description}</pre>
            {confirmation.request.action === "trashProject" && (
              <div className="operation-form">
                <Select
                  label={t("项目目录处理方式", "Project folder action")}
                  disabled={running}
                  value={confirmation.request.projectMode || "recycle"}
                  options={[
                    {
                      value: "recycle",
                      label: t(
                        "移入 Windows 回收站",
                        "Move to Windows Recycle Bin",
                      ),
                    },
                    {
                      value: "permanent",
                      label: t(
                        "永久递归删除",
                        "Permanently delete recursively",
                      ),
                    },
                  ]}
                  onValueChange={(value) => {
                    setProjectConfirmation("");
                    setConfirmation({
                      ...confirmation,
                      request: {
                        ...confirmation.request,
                        projectMode: value as "recycle" | "permanent",
                      },
                    });
                  }}
                />
                {confirmation.request.projectMode === "permanent" && (
                  <label className="typed-confirmation">
                    {t(
                      `输入项目目录名以确认永久删除：${expectedNames(confirmation.paths || [])}`,
                      `Type the project folder name to confirm permanent deletion: ${expectedNames(confirmation.paths || [])}`,
                    )}
                    <Input
                      aria-label={t(
                        "永久删除项目确认",
                        "Confirm permanent project deletion",
                      )}
                      value={projectConfirmation}
                      disabled={running}
                      onChange={(e) => setProjectConfirmation(e.target.value)}
                      autoComplete="off"
                    />
                  </label>
                )}
              </div>
            )}
            <div className="row">
              <Button disabled={running} onClick={() => setConfirmation(null)}>
                {t("取消", "Cancel")}
              </Button>
              <Button
                variant={
                  confirmation.request.action === "purge" ||
                  confirmation.request.projectMode === "permanent"
                    ? "danger"
                    : "primary"
                }
                disabled={
                  running ||
                  (confirmation.request.projectMode === "permanent" &&
                    (!expectedNames(confirmation.paths || []) ||
                      projectConfirmation.trim().toLocaleLowerCase() !==
                        expectedNames(
                          confirmation.paths || [],
                        ).toLocaleLowerCase()))
                }
                onClick={() =>
                  void run({ ...confirmation.request, projectConfirmation })
                }
              >
                {t("确认执行", "Confirm")}
              </Button>
            </div>
          </>
        )}
      </Dialog>
    </>
  );
}

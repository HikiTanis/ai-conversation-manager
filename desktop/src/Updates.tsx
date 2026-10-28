import { useEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { Button, Dialog, Input } from "./components/ui";
import type { Language } from "./types";
import { version as appVersion } from "../package.json";

export type UpdateInfo = {
  currentVersion: string;
  version: string;
  available: boolean;
  notes: string;
  publishedAt: string;
  installed: boolean;
};
type Progress = { phase: string; downloaded: number; total: number };
export const updateApi = {
  check: () => invoke<UpdateInfo>("check_update"),
  install: (version: string) =>
    invoke<void>("install_update", { expectedVersion: version }),
  listen: (handler: (progress: Progress) => void) =>
    listen<Progress>("update-progress", (event) => handler(event.payload)),
};
function autoCheckEnabled() {
  try {
    return localStorage.getItem("updates.autoCheck") !== "false";
  } catch {
    return true;
  }
}

export function Updates({
  lang,
  enabled,
  busy,
  onBusy,
  api = updateApi,
}: {
  lang: Language;
  enabled: boolean;
  busy: boolean;
  onBusy: (value: boolean) => void;
  api?: typeof updateApi;
}) {
  const [autoCheck, setAutoCheck] = useState(autoCheckEnabled);
  const [opened, setOpened] = useState(false);
  const [pendingPrompt, setPendingPrompt] = useState(false);
  const [checking, setChecking] = useState(false);
  const [installing, setInstalling] = useState(false);
  const [info, setInfo] = useState<UpdateInfo | null>(null);
  const [error, setError] = useState("");
  const [progress, setProgress] = useState<Progress | null>(null);
  const dialog = useRef<HTMLDialogElement>(null);
  const checkingRef = useRef(false);
  const started = useRef(false);
  const t = (zh: string, en: string) => (lang === "zh" ? zh : en);

  async function check(manual: boolean) {
    if (checkingRef.current || installing) return;
    checkingRef.current = true;
    if (manual) setOpened(true);
    setChecking(true);
    setError("");
    try {
      const next = await api.check();
      setInfo(next);
      if (!manual && next.available) setPendingPrompt(true);
    } catch (e) {
      setError(String(e));
    } finally {
      checkingRef.current = false;
      setChecking(false);
    }
  }
  useEffect(() => {
    if (!enabled || started.current) return;
    started.current = true;
    if (autoCheck) void check(false);
  }, [enabled]);
  useEffect(() => {
    if (pendingPrompt && !busy) {
      setOpened(true);
      setPendingPrompt(false);
    }
  }, [pendingPrompt, busy]);
  useEffect(() => {
    if (opened && !dialog.current?.open) dialog.current?.showModal();
    else if (!opened && dialog.current?.open) dialog.current?.close();
  }, [opened]);

  async function install() {
    if (!info?.available || busy || installing) return;
    setInstalling(true);
    onBusy(true);
    setError("");
    setProgress(null);
    let unlisten: (() => void) | undefined;
    try {
      unlisten = await api.listen(setProgress);
      await api.install(info.version);
    } catch (e) {
      setError(String(e));
    } finally {
      unlisten?.();
      setInstalling(false);
      onBusy(false);
    }
  }
  const percent = progress?.total
    ? Math.min(100, Math.floor((progress.downloaded / progress.total) * 100))
    : 0;
  return (
    <>
      <Button
        variant="text"
        disabled={!enabled || installing}
        onClick={() => {
          setOpened(true);
          if (!info?.available) void check(true);
        }}
      >
        {info?.available
          ? t(`发现新版 ${info.version}`, `Update ${info.version}`)
          : t("检查更新", "Check for updates")}
      </Button>
      <Dialog
        ref={dialog}
        className="update-dialog"
        aria-labelledby="update-title"
        onCancel={(e) => {
          if (installing) e.preventDefault();
          else setOpened(false);
        }}
        onClose={() => {
          if (!installing) setOpened(false);
        }}
      >
        <div className="update-heading">
          <h2 id="update-title">{t("软件更新", "Software update")}</h2>
          <Button
            variant="text"
            disabled={installing}
            onClick={() => setOpened(false)}
            aria-label={t("关闭", "Close")}
          >
            ×
          </Button>
        </div>
        <p>{t(`当前版本 ${appVersion}`, `Current version ${appVersion}`)}</p>
        <label className="update-preference">
          <Input
            type="checkbox"
            checked={autoCheck}
            disabled={installing}
            onChange={(e) => {
              setAutoCheck(e.target.checked);
              try {
                localStorage.setItem(
                  "updates.autoCheck",
                  String(e.target.checked),
                );
              } catch {
                /* Optional preference. */
              }
            }}
          />
          {t("启动时自动检查更新", "Check for updates on startup")}
        </label>
        {checking && (
          <p role="status">
            {t(
              "正在检查 GitHub 最新正式版…",
              "Checking the latest stable release on GitHub…",
            )}
          </p>
        )}
        {error && (
          <div role="alert" className="update-error">
            <p>
              {t(
                "暂未完成更新，请查看错误详情后重试。",
                "Update could not be completed. See the details below and retry.",
              )}
            </p>
            <details>
              <summary>{t("错误详情", "Details")}</summary>
              {error}
            </details>
          </div>
        )}
        {info && !checking && (
          <>
            <h3>
              {info.available
                ? t(
                    `新版本 ${info.version}`,
                    `Version ${info.version} is available`,
                  )
                : t("当前已是最新版本", "You are up to date")}
            </h3>
            {info.available && (
              <>
                {info.publishedAt && (
                  <p className="muted">
                    {t("发布日期：", "Released: ")}
                    {new Date(info.publishedAt).toLocaleDateString(
                      lang === "zh" ? "zh-CN" : "en",
                    )}
                  </p>
                )}
                <pre className="update-notes">
                  {info.notes ||
                    t("此版本未提供更新说明。", "No release notes provided.")}
                </pre>
                <p>
                  {info.installed
                    ? t(
                        "下载并校验后，软件将关闭、安装新版并重新打开。",
                        "After download and verification, the app will close, install the update and reopen.",
                      )
                    : t(
                        "下载并校验后，将在当前目录更新便携版并重新打开。旧程序文件会保留一份备份。",
                        "After verification, the portable app will update in this folder and reopen. A backup of the old app files will be kept.",
                      )}
                </p>
                <p>
                  {t(
                    "更新只处理程序文件，不修改对话和项目数据。",
                    "Updates only replace application files; conversation and project data are preserved.",
                  )}
                </p>
              </>
            )}
          </>
        )}
        {installing && (
          <div role="status" aria-live="polite">
            <p>
              {progress?.phase === "installing"
                ? t("正在重启并安装…", "Restarting to install…")
                : progress?.phase === "verifying"
                  ? t("正在校验并准备更新…", "Verifying and preparing update…")
                  : t(
                      `正在下载更新 ${percent}%`,
                      `Downloading update ${percent}%`,
                    )}
            </p>
            <progress
              max={100}
              value={percent}
              aria-label={t("下载进度", "Download progress")}
            />
          </div>
        )}
        {busy && !installing && (
          <p>
            {t(
              "请等待当前操作完成后再更新。",
              "Wait for the current operation to finish before updating.",
            )}
          </p>
        )}
        <div className="update-actions">
          <Button
            disabled={checking || installing}
            onClick={() => void check(true)}
          >
            {t("重新检查", "Check again")}
          </Button>
          <Button disabled={installing} onClick={() => setOpened(false)}>
            {t("稍后", "Later")}
          </Button>
          {info?.available && (
            <Button
              variant="primary"
              disabled={checking || installing || busy}
              onClick={() => void install()}
            >
              {t("立即更新并重启", "Update and restart")}
            </Button>
          )}
        </div>
      </Dialog>
    </>
  );
}

import { useMemo, useState } from "react";
import { Updates, type updateApi } from "./Updates";
import { Button, Card, Select } from "./components/ui";

// Development-only visual fixture. Never invokes native installation commands.
export default function UpdatePreview() {
  const [scenario, setScenario] = useState("available");
  const [busy, setBusy] = useState(false);
  const [completed, setCompleted] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const api = useMemo(() => {
    let handler: Parameters<typeof updateApi.listen>[0] | undefined;
    return {
      check: async () => {
        await new Promise((resolve) => setTimeout(resolve, 400));
        if (scenario === "offline")
          throw Error("无法连接更新源；请检查网络后重试。");
        return {
          currentVersion: "2.1.0",
          version: "2.2.0",
          available: scenario !== "current",
          installed: scenario === "installed",
          publishedAt: "2026-10-28T00:00:00Z",
          notes:
            "浏览器测试用更新说明\n\n• 改善更新检查与下载体验\n• 校验下载文件，失败可重试\n• 保留对话与项目数据\n\n这里使用合成版本，点击更新不会安装任何程序。",
        };
      },
      listen: async (callback: Parameters<typeof updateApi.listen>[0]) => {
        handler = callback;
        return () => {
          handler = undefined;
        };
      },
      install: async (_version: string) => {
        for (let n = 0; n <= 100; n += 10) {
          handler?.({ phase: "downloading", downloaded: n, total: 100 });
          await new Promise((resolve) => setTimeout(resolve, 180));
          if (scenario === "failure" && n === 50)
            throw Error("测试：下载校验失败，旧版程序保持不变。");
        }
        handler?.({ phase: "verifying", downloaded: 100, total: 100 });
        await new Promise((resolve) => setTimeout(resolve, 500));
        handler?.({ phase: "installing", downloaded: 100, total: 100 });
        await new Promise((resolve) => setTimeout(resolve, 500));
        setCompleted(true);
      },
    } satisfies typeof updateApi;
  }, [scenario, attempt]);
  return (
    <main style={{ maxWidth: 900, margin: "48px auto", padding: 24 }}>
      <h1>软件更新 · 浏览器测试</h1>
      <p>仅模拟界面与下载进度，不访问对话、不下载或安装程序。</p>
      <Card style={{ padding: 24, display: "grid", gap: 20 }}>
        <Select
          label="测试场景"
          value={scenario}
          disabled={busy}
          onValueChange={(value) => {
            setScenario(value);
            setCompleted(false);
          }}
          options={[
            { value: "available", label: "便携版：发现新版本" },
            { value: "installed", label: "安装版：发现新版本" },
            { value: "current", label: "已经是最新版" },
            { value: "offline", label: "检查更新失败" },
            { value: "failure", label: "下载失败与重试" },
          ]}
        />
        {completed ? (
          <p role="status">模拟更新完成：真实客户端将在这里重新启动。</p>
        ) : (
          <Updates
            key={`${scenario}-${attempt}`}
            lang="zh"
            enabled
            busy={false}
            onBusy={setBusy}
            api={api}
          />
        )}
        <Button
          disabled={busy}
          onClick={() => {
            setCompleted(false);
            setAttempt((n) => n + 1);
          }}
        >
          重新演练
        </Button>
      </Card>
    </main>
  );
}

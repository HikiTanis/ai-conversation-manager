import type { Catalog, Gateway, Preview, Provider } from "./types";
export function demoCatalog(provider: Provider): Catalog {
  const name = provider === "codex" ? "Codex" : "DSH";
  return {
    provider,
    home: `演示数据 / ${name}`,
    warnings: [],
    projects: [
      {
        id: "demo-project",
        title: "演示项目",
        path: "D:/Projects/demo",
        canBackupFiles: true,
        sessions: [
          {
            id: "main-demo",
            title: `${name} 对话管理功能`,
            path: "sessions/main-demo.jsonl",
            archived: false,
            subagent: false,
            updated: "2026-10-12 12:00",
            bytes: 1240,
            source: name,
            compressed: false,
          },
          {
            id: "archive-demo",
            title: `${name} 已归档的界面方案`,
            path: "sessions/archive-demo.jsonl",
            archived: true,
            subagent: false,
            updated: "2026-10-11 16:20",
            bytes: 2070,
            source: name,
            compressed: false,
          },
          {
            id: "child-demo",
            title: `${name} 子代理检查`,
            path: "sessions/child-demo.jsonl",
            archived: false,
            subagent: true,
            parentId: "main-demo",
            updated: "2026-10-12 12:10",
            bytes: 820,
            source: name,
            compressed: false,
          },
        ],
      },
      {
        id: "empty-project",
        title: "空项目",
        path: "D:/Projects/empty",
        canBackupFiles: false,
        sessions: [],
      },
    ],
  };
}
export const demoGateway: Gateway = {
  async read<T>(request: import("./types").ReadRequest): Promise<T> {
    await new Promise((resolve) => setTimeout(resolve, 80));
    if (request.action === "list") return demoCatalog(request.provider) as T;
    if (request.action === "storage")
      return { bytes: 24576, files: 8, directories: 2, skipped: 0 } as T;
    const result: Preview = {
      messages: [
        {
          role: "user",
          text: "沿用原来的界面，支持切换 Codex 和 DSH，再按归档状态分类。",
          time: "12:00",
          truncated: false,
        },
        {
          role: "assistant",
          text: "保留项目侧栏、对话列表和预览浮层，共用控件与样式。\n\n这里展示的是合成数据，没有读取你的真实对话。",
          time: "12:01",
          truncated: false,
        },
      ],
      total: 2,
      nextOffset: 2,
      hasMore: false,
    };
    return result as T;
  },
};

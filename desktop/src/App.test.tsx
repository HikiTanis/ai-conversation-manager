// @vitest-environment jsdom
import { beforeEach, afterEach, expect, test, vi } from "vitest";
import {
  render,
  screen,
  cleanup,
  waitFor,
  within,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import App from "./App";
import { demoCatalog } from "./demo";
import { filterSessions, type Gateway, type Catalog } from "./types";
beforeEach(() => {
  localStorage.clear();
  vi.spyOn(HTMLElement.prototype, "offsetHeight", "get").mockReturnValue(600);
  vi.spyOn(HTMLElement.prototype, "offsetWidth", "get").mockReturnValue(900);
  HTMLDialogElement.prototype.showModal = function () {
    this.setAttribute("open", "");
  };
  HTMLDialogElement.prototype.close = function () {
    this.removeAttribute("open");
  };
});
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});
function fakeGateway(): Gateway {
  return {
    read: vi.fn(
      async (r) =>
        (r.action === "list"
          ? demoCatalog(r.provider)
          : r.action === "storage"
            ? { bytes: 1000, files: 2, directories: 0, skipped: 0 }
            : {
                messages: [
                  {
                    role: "user",
                    text: "<script>unsafe</script>",
                    time: "12:00",
                    truncated: false,
                  },
                ],
                total: 1,
                hasMore: false,
                nextOffset: 1,
              }) as never,
    ),
  };
}
test("archive, subagent, ID and path filters retain original grouping", () => {
  const sessions = demoCatalog("codex").projects[0].sessions;
  expect(filterSessions(sessions, "main", "archived", "")).toHaveLength(1);
  expect(
    filterSessions(sessions, "subagent", "active", "child-demo"),
  ).toHaveLength(1);
  expect(filterSessions(sessions, "main", "active", "sessions/")).toHaveLength(
    1,
  );
  expect(filterSessions(sessions, "main", "active", "no match")).toHaveLength(
    0,
  );
});

test("project search preserves conversation selection and full backup includes its project", async () => {
  const api = fakeGateway(),
    user = userEvent.setup();
  api.operate = vi.fn(async () => ({ message: "Backup done" }) as never);
  render(<App gateway={api} />);
  await screen.findByText("Codex 对话管理功能");
  expect(screen.queryByRole("textbox", { name: "备份输出文件" })).toBeNull();
  await user.click(
    screen.getByRole("checkbox", { name: "选择 Codex 对话管理功能" }),
  );
  const search = screen.getByRole("textbox", { name: "搜索项目" });
  await user.type(search, "no-such-project");
  expect(screen.getByText("没有匹配的项目")).toBeTruthy();
  expect(screen.getByText("已选 1 个对话")).toBeTruthy();
  await user.clear(search);
  await user.click(screen.getByRole("button", { name: "创建备份…" }));
  const dialog = screen.getByRole("dialog", { name: "创建备份" });
  expect(within(dialog).getByText("本次备份 1 条对话")).toBeTruthy();
  await user.click(within(dialog).getByRole("button", { name: "项目 + 对话" }));
  expect(within(dialog).getByText("本次备份 3 条对话")).toBeTruthy();
  await user.type(
    within(dialog).getByRole("textbox", { name: "备份输出文件" }),
    "E:\\selected.codexproject",
  );
  await user.click(within(dialog).getByRole("button", { name: "创建备份" }));
  expect(api.operate).toHaveBeenLastCalledWith(
    expect.objectContaining({
      action: "backup",
      ids: [],
      includeFiles: true,
      projectIds: [demoCatalog("codex").projects[0].id],
    }),
  );
});
test("switching provider clears selection, search, archive and preview state", async () => {
  const user = userEvent.setup();
  render(<App gateway={fakeGateway()} />);
  await screen.findByText("Codex 对话管理功能");
  await user.click(
    screen.getByRole("checkbox", { name: "选择 Codex 对话管理功能" }),
  );
  expect(screen.getByText("已选 1 个对话")).toBeTruthy();
  await user.type(
    screen.getByRole("textbox", { name: "筛选对话" }),
    "main-demo",
  );
  await user.click(screen.getByRole("button", { name: "DSH" }));
  await screen.findByText("DSH 对话管理功能");
  expect(screen.getByText("已选 0 个对话")).toBeTruthy();
  expect(
    (screen.getByRole("textbox", { name: "筛选对话" }) as HTMLInputElement)
      .value,
  ).toBe("");
  await user.click(screen.getByRole("button", { name: "已归档 (1)" }));
  expect(screen.queryByText("DSH 对话管理功能")).toBeNull();
  expect(screen.getByText("DSH 已归档的界面方案")).toBeTruthy();
  await user.click(screen.getByRole("button", { name: "Codex" }));
  await screen.findByText("Codex 对话管理功能");
});
test("preview treats conversation content as text and exposes no write command", async () => {
  const api = fakeGateway(),
    user = userEvent.setup();
  render(<App gateway={api} />);
  await screen.findByText("Codex 对话管理功能");
  await user.click(screen.getAllByRole("button", { name: "查看" })[0]);
  await screen.findByText("<script>unsafe</script>");
  expect(document.querySelector("dialog script")).toBeNull();
  expect(
    (screen.getByRole("button", { name: "删除所选" }) as HTMLButtonElement)
      .disabled,
  ).toBe(true);
  await user.click(screen.getByRole("button", { name: "关闭预览" }));
  expect(document.querySelector(".preview-dialog[open]")).toBeNull();
});
test("loading blocks provider changes and a failed read allows retry", async () => {
  let reject!: (error: Error) => void;
  const api: Gateway = {
    read: vi.fn(
      () =>
        new Promise<never>((_resolve, r) => {
          reject = r;
        }),
    ),
  };
  render(<App gateway={api} />);
  expect(
    (screen.getByRole("button", { name: "DSH" }) as HTMLButtonElement).disabled,
  ).toBe(true);
  reject(new Error("Synthetic read error"));
  await screen.findByRole("alert");
  await waitFor(() =>
    expect(
      (screen.getByRole("button", { name: "DSH" }) as HTMLButtonElement)
        .disabled,
    ).toBe(false),
  );
  expect(screen.getByRole("button", { name: "重试" })).toBeTruthy();
});
test("late responses from an unmounted provider view are ignored", async () => {
  let resolve!: (value: Catalog) => void;
  const api: Gateway = {
    read: vi.fn(
      () =>
        new Promise<Catalog>((r) => {
          resolve = r;
        }) as never,
    ),
  };
  const mounted = render(<App gateway={api} />);
  mounted.unmount();
  resolve(demoCatalog("codex"));
  await Promise.resolve();
  expect(screen.queryByText("Codex 对话管理功能")).toBeNull();
});

test("deletion requires a reviewed descendant scope and sends its token", async () => {
  const user = userEvent.setup();
  const api = fakeGateway();
  api.operate = vi.fn(
    async (r) =>
      (r.action === "planDelete"
        ? {
            token: "reviewed-token",
            sessions: [
              { id: "main-demo", title: "Main", subagent: false },
              { id: "child-demo", title: "Child", subagent: true },
            ],
            paths: [],
            remaining: 0,
          }
        : { message: "Deleted" }) as never,
  );
  render(<App gateway={api} />);
  await screen.findByText("Codex 对话管理功能");
  await user.click(
    screen.getByRole("checkbox", { name: "选择 Codex 对话管理功能" }),
  );
  await user.click(screen.getByRole("button", { name: "删除所选" }));
  expect(screen.queryByRole("button", { name: "确认移入回收站" })).toBeNull();
  await user.click(screen.getByRole("button", { name: "核对删除范围" }));
  await screen.findByText("Child");
  await user.click(screen.getByRole("button", { name: "确认移入回收站" }));
  await screen.findByText("Deleted");
  expect(api.operate).toHaveBeenLastCalledWith(
    expect.objectContaining({
      action: "delete",
      token: "reviewed-token",
      ids: ["main-demo"],
    }),
  );
});

test("changing deletion options invalidates the reviewed scope", async () => {
  const user = userEvent.setup(),
    api = fakeGateway();
  api.operate = vi.fn(
    async () =>
      ({ token: "old", sessions: [], paths: [], remaining: 0 }) as never,
  );
  render(<App gateway={api} />);
  await screen.findByText("Codex 对话管理功能");
  await user.click(
    screen.getByRole("checkbox", { name: "选择 Codex 对话管理功能" }),
  );
  await user.click(screen.getByRole("button", { name: "删除所选" }));
  await user.click(screen.getByRole("button", { name: "核对删除范围" }));
  await screen.findByRole("button", { name: "确认移入回收站" });
  await user.click(screen.getByRole("checkbox", { name: /永久删除对话/ }));
  expect(screen.queryByRole("button", { name: "确认永久删除" })).toBeNull();
  expect(api.operate).toHaveBeenCalledTimes(1);
});

test("cross-project backup selections survive navigation while delete is limited to visible results", async () => {
  const user = userEvent.setup(),
    api = fakeGateway();
  const catalog = demoCatalog("codex");
  catalog.projects[1].sessions = [
    {
      ...catalog.projects[0].sessions[0],
      id: "other-main",
      title: "Other conversation",
    },
  ];
  api.read = vi.fn(
    async (r) =>
      (r.action === "list" ? catalog : { bytes: 0, files: 0 }) as never,
  );
  api.operate = vi.fn(
    async (r) =>
      (r.action === "planDelete"
        ? { token: "scope", sessions: [], paths: [], remaining: 0 }
        : { message: "Backup done" }) as never,
  );
  render(<App gateway={api} />);
  await screen.findByText("Codex 对话管理功能");
  await user.click(
    screen.getByRole("checkbox", { name: "选择 Codex 对话管理功能" }),
  );
  await user.click(screen.getByRole("button", { name: /空项目/ }));
  await user.click(
    screen.getByRole("checkbox", { name: "选择 Other conversation" }),
  );
  expect(screen.getByText("已选 2 个对话")).toBeTruthy();
  await user.type(screen.getByRole("textbox", { name: "筛选对话" }), "hidden");
  expect(
    (screen.getByRole("button", { name: "删除所选" }) as HTMLButtonElement)
      .disabled,
  ).toBe(true);
  await user.clear(screen.getByRole("textbox", { name: "筛选对话" }));
  await user.click(screen.getByRole("button", { name: "删除所选" }));
  await user.click(screen.getByRole("button", { name: "核对删除范围" }));
  expect(api.operate).toHaveBeenLastCalledWith(
    expect.objectContaining({
      action: "planDelete",
      ids: ["other-main"],
      batch: true,
    }),
  );
  await user.click(screen.getByRole("button", { name: "关闭操作窗口" }));
  await user.click(screen.getByRole("button", { name: "创建备份…" }));
  const dialog = screen.getByRole("dialog", { name: "创建备份" });
  await user.type(within(dialog).getByRole("textbox"), "E:\\backup.codexchat");
  await user.click(within(dialog).getByRole("button", { name: "创建备份" }));
  await screen.findByText("Backup done");
  expect(api.operate).toHaveBeenLastCalledWith(
    expect.objectContaining({
      action: "backup",
      ids: ["main-demo", "other-main"],
    }),
  );
});

test("package summary enables file restoration and suggests the target before import", async () => {
  const user = userEvent.setup(),
    api = fakeGateway();
  api.operate = vi.fn(
    async (r) =>
      (r.action === "packageInfo"
        ? {
            raw: false,
            created: "2026-10-15",
            bytes: 2048,
            mainCount: 2,
            subagentCount: 1,
            projects: [
              {
                title: "Packed project",
                path: "D:\\old",
                targetFolder: "project",
                hasFiles: true,
              },
            ],
            hasFiles: true,
            fileCount: 4,
            fileBytes: 8000,
            suggestedTarget: "E:\\restored",
          }
        : { message: "Inspected" }) as never,
  );
  render(<App gateway={api} />);
  await screen.findByText("Codex 对话管理功能");
  await user.click(screen.getByRole("button", { name: "导入与恢复" }));
  await user.type(
    screen.getByRole("textbox", { name: "备份包" }),
    "E:\\test.codexproject",
  );
  await screen.findByText("Packed project");
  expect(
    (
      screen.getByRole("checkbox", {
        name: "还原包内项目文件",
      }) as HTMLInputElement
    ).checked,
  ).toBe(true);
  expect(
    (screen.getByRole("textbox", { name: "目标项目目录" }) as HTMLInputElement)
      .value,
  ).toBe("E:\\restored");
  await user.click(screen.getByRole("button", { name: "先检查（不写入）" }));
  expect(api.operate).toHaveBeenLastCalledWith(
    expect.objectContaining({
      action: "inspect",
      restoreFiles: true,
      target: "E:\\restored",
      conflict: "empty",
    }),
  );
});

test("repair protects live descendants and can locate the child for follow-up", async () => {
  const user = userEvent.setup(),
    api = fakeGateway();
  api.operate = vi.fn(
    async () =>
      ({
        token: "repair",
        ids: [],
        snapshotCount: 0,
        cacheCleanup: false,
        blocked: [
          {
            id: "child-demo",
            rootId: "main-demo",
            title: "Live child",
            path: "sessions/child-demo.jsonl",
          },
        ],
      }) as never,
  );
  render(<App gateway={api} />);
  await screen.findByText("Codex 对话管理功能");
  await user.click(screen.getByRole("button", { name: "设置与维护" }));
  await user.click(screen.getByRole("button", { name: "检查修复" }));
  await screen.findByText("Live child");
  expect(screen.queryByRole("button", { name: "确认执行" })).toBeNull();
  await user.click(screen.getByRole("button", { name: "定位子代理" }));
  expect(
    (screen.getByRole("textbox", { name: "筛选对话" }) as HTMLInputElement)
      .value,
  ).toBe("child-demo");
  expect(
    (
      screen.getByRole("checkbox", {
        name: "选择 Codex 子代理检查",
      }) as HTMLInputElement
    ).checked,
  ).toBe(true);
  expect(api.operate).toHaveBeenCalledTimes(1);
});

test("permanent project deletion requires its typed folder name", async () => {
  const user = userEvent.setup(),
    api = fakeGateway();
  api.operate = vi.fn(
    async (r) =>
      (r.action === "planDelete"
        ? {
            token: "scope",
            sessions: [{ id: "main-demo", title: "Main", subagent: false }],
            paths: ["E:\\project-to-delete"],
            remaining: 1,
          }
        : { message: "Deleted safely" }) as never,
  );
  render(<App gateway={api} />);
  await screen.findByText("Codex 对话管理功能");
  await user.click(screen.getAllByRole("button", { name: "对话操作" })[0]);
  await user.click(screen.getByRole("button", { name: "删除…" }));
  await user.click(screen.getByRole("combobox", { name: "关联项目目录" }));
  await user.click(screen.getByRole("option", { name: "永久递归删除" }));
  await user.click(screen.getByRole("button", { name: "核对删除范围" }));
  const confirmation = await screen.findByRole("textbox", {
    name: "永久删除项目确认",
  });
  const execute = screen.getByRole("button", {
    name: "确认永久删除项目并回收对话",
  }) as HTMLButtonElement;
  expect(execute.disabled).toBe(true);
  await user.type(confirmation, "wrong");
  expect(execute.disabled).toBe(true);
  await user.clear(confirmation);
  await user.type(confirmation, "project-to-delete");
  await user.click(execute);
  expect(api.operate).toHaveBeenLastCalledWith(
    expect.objectContaining({
      action: "delete",
      projectMode: "permanent",
      projectConfirmation: "project-to-delete",
      token: "scope",
    }),
  );
});

test("read-only diagnostics never offer deletion confirmation", async () => {
  const user = userEvent.setup(),
    api = fakeGateway();
  api.operate = vi.fn(
    async () =>
      ({
        token: "read-only",
        ids: [],
        snapshotCount: 0,
        cacheCleanup: false,
        blocked: [],
        diagnostic: "All history preserved",
      }) as never,
  );
  render(<App gateway={api} />);
  await screen.findByText("Codex 对话管理功能");
  await user.click(screen.getByRole("button", { name: "设置与维护" }));
  await user.click(screen.getByRole("button", { name: "检查修复" }));
  await screen.findByText("All history preserved");
  expect(screen.queryByRole("button", { name: "确认执行" })).toBeNull();
  expect(api.operate).toHaveBeenCalledTimes(1);
});

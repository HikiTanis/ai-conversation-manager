import { invoke, isTauri } from "@tauri-apps/api/core";
import type { Gateway } from "./types";
export const gateway: Gateway = {
  async operate<T>(request: import("./types").OperationRequest): Promise<T> {
    if (!isTauri())
      throw new Error("Open the desktop app to manage local data.");
    return invoke<T>("run_operation", { request });
  },
  async read<T>(request: import("./types").ReadRequest): Promise<T> {
    if (!isTauri())
      throw new Error(
        "请用 Tauri 桌面程序打开以读取本地数据。浏览器演示仅使用合成数据。 / Open the desktop app to read local data.",
      );
    return invoke<T>("read_catalog", { request });
  },
};

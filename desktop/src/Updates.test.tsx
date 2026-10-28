// @vitest-environment jsdom
import { afterEach, beforeEach, expect, test, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Updates, type UpdateInfo, type updateApi } from "./Updates";

const release: UpdateInfo = {
  currentVersion: "2.1.0",
  version: "2.2.0",
  available: true,
  notes: "Update details <script>unsafe</script>",
  publishedAt: "2026-10-28T00:00:00Z",
  installed: false,
};
function api() {
  return {
    check: vi.fn(async () => release),
    install: vi.fn(async () => {}),
    listen: vi.fn(async () => vi.fn()),
  } satisfies typeof updateApi;
}
beforeEach(() => {
  localStorage.clear();
  HTMLDialogElement.prototype.showModal = function () {
    this.setAttribute("open", "");
  };
  HTMLDialogElement.prototype.close = function () {
    this.removeAttribute("open");
  };
});
afterEach(cleanup);

test("startup checks automatically, defers its prompt during operations and never installs without a click", async () => {
  const backend = api(),
    onBusy = vi.fn();
  const view = render(
    <Updates lang="zh" enabled busy onBusy={onBusy} api={backend} />,
  );
  await waitFor(() => expect(backend.check).toHaveBeenCalledTimes(1));
  expect(document.querySelector("dialog")?.hasAttribute("open")).toBe(false);
  expect(backend.install).not.toHaveBeenCalled();
  view.rerender(
    <Updates lang="zh" enabled busy={false} onBusy={onBusy} api={backend} />,
  );
  await waitFor(() =>
    expect(document.querySelector("dialog")?.hasAttribute("open")).toBe(true),
  );
  expect(screen.getByText(release.notes)).toBeTruthy();
  expect(document.querySelector("script")).toBeNull();
  await userEvent.click(screen.getByText("稍后"));
  expect(backend.install).not.toHaveBeenCalled();
});

test("startup preference disables automatic requests but manual checks still work", async () => {
  localStorage.setItem("updates.autoCheck", "false");
  const backend = api();
  render(
    <Updates lang="en" enabled busy={false} onBusy={vi.fn()} api={backend} />,
  );
  expect(backend.check).not.toHaveBeenCalled();
  await userEvent.click(screen.getByText("Check for updates"));
  await screen.findByText("Version 2.2.0 is available");
  expect(backend.check).toHaveBeenCalledTimes(1);
  expect(backend.install).not.toHaveBeenCalled();
});

test("download failure releases the operation lock and allows retry without closing the app", async () => {
  const backend = api(),
    onBusy = vi.fn();
  backend.install.mockRejectedValueOnce(new Error("checksum mismatch"));
  render(
    <Updates lang="en" enabled busy={false} onBusy={onBusy} api={backend} />,
  );
  await userEvent.click(await screen.findByText("Update and restart"));
  await screen.findByText("checksum mismatch", { exact: false });
  expect(onBusy.mock.calls).toEqual([[true], [false]]);
  expect(backend.install).toHaveBeenCalledWith("2.2.0");
  expect(document.querySelector("dialog")?.hasAttribute("open")).toBe(true);
  await userEvent.click(screen.getByText("Update and restart"));
  expect(backend.install).toHaveBeenCalledTimes(2);
});

test("an operation in progress disables installation", async () => {
  const backend = api();
  render(<Updates lang="en" enabled busy onBusy={vi.fn()} api={backend} />);
  await userEvent.click(screen.getByText("Check for updates"));
  const button = await screen.findByText("Update and restart");
  expect((button as HTMLButtonElement).disabled).toBe(true);
  expect(backend.install).not.toHaveBeenCalled();
});

test("offline startup does not open a modal, manual retry displays the error", async () => {
  const backend = api();
  backend.check.mockRejectedValue(new Error("offline"));
  render(
    <Updates lang="en" enabled busy={false} onBusy={vi.fn()} api={backend} />,
  );
  await waitFor(() => expect(backend.check).toHaveBeenCalledTimes(1));
  expect(document.querySelector("dialog")?.hasAttribute("open")).toBe(false);
  await userEvent.click(screen.getByText("Check for updates"));
  await screen.findByText("Error: offline");
  expect(document.querySelector("dialog")?.hasAttribute("open")).toBe(true);
});

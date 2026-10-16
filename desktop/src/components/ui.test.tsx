// @vitest-environment jsdom
import { afterEach, expect, test, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { Select, Menu, Button } from "./ui";
afterEach(cleanup);
test("action menus support keyboard navigation and return focus on escape", async () => {
  const user = userEvent.setup();
  const act = vi.fn();
  render(
    <Menu label="Actions">
      <Button disabled>Unavailable</Button>
      <Button onClick={act}>Copy</Button>
      <Button>Details</Button>
    </Menu>,
  );
  const trigger = screen.getByRole("button", { name: "Actions" });
  await user.click(trigger);
  expect(document.activeElement).toBe(
    screen.getByRole("button", { name: "Copy" }),
  );
  await user.keyboard("{ArrowDown}");
  expect(document.activeElement).toBe(
    screen.getByRole("button", { name: "Details" }),
  );
  await user.keyboard("{Home}{Enter}");
  expect(act).toHaveBeenCalledTimes(1);
  expect(trigger.getAttribute("aria-expanded")).toBe("false");
  await user.click(trigger);
  await user.keyboard("{Escape}");
  expect(document.activeElement).toBe(trigger);
  expect(screen.queryByRole("button", { name: "Copy" })).toBeNull();
});
test("shared dropdown supports keyboard choice, disabled options, escape and typeahead", async () => {
  const user = userEvent.setup();
  const changed = vi.fn();
  function Example() {
    const [value, setValue] = useState("empty");
    return (
      <Select
        label="Conflict policy"
        value={value}
        onValueChange={(v) => {
          changed(v);
          setValue(v);
        }}
        options={[
          { value: "empty", label: "Empty folder" },
          { value: "blocked", label: "Blocked", disabled: true },
          { value: "skip", label: "Keep files" },
          { value: "overwrite", label: "Overwrite with backup" },
        ]}
      />
    );
  }
  render(<Example />);
  const trigger = screen.getByRole("combobox", { name: "Conflict policy" });
  await user.tab();
  await user.keyboard("{ArrowDown}{Enter}");
  expect(changed).toHaveBeenLastCalledWith("skip");
  expect(trigger.getAttribute("aria-expanded")).toBe("false");
  await user.click(trigger);
  await user.click(screen.getByRole("option", { name: "Blocked" }));
  expect(changed).toHaveBeenCalledTimes(1);
  await user.keyboard("{End}{Escape}");
  expect(changed).toHaveBeenCalledTimes(1);
  expect(screen.queryByRole("listbox")).toBeNull();
  await user.keyboard("o");
  expect(changed).toHaveBeenLastCalledWith("overwrite");
  expect(document.activeElement).toBe(trigger);
});

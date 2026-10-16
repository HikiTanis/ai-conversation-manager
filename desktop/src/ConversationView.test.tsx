// @vitest-environment jsdom
import { afterEach, beforeEach, expect, test, vi } from "vitest";
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { ConversationView } from "./ConversationView";
import type { Preview, Message } from "./types";

beforeEach(() => {
  vi.spyOn(HTMLElement.prototype, "offsetHeight", "get").mockImplementation(
    function (this: HTMLElement) {
      return this.classList.contains("messages") ? 600 : 240;
    },
  );
  vi.spyOn(HTMLElement.prototype, "offsetWidth", "get").mockReturnValue(900);
  vi.spyOn(HTMLElement.prototype, "scrollHeight", "get").mockReturnValue(28800);
  HTMLElement.prototype.scrollTo = function (
    options: ScrollToOptions | number = {},
  ) {
    if (typeof options !== "number") {
      this.scrollTop = options.top ?? 0;
      this.dispatchEvent(new Event("scroll"));
    }
  };
});
afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});
const messages: Message[] = Array.from({ length: 120 }, (_, i) => ({
  role: i % 2 ? "assistant" : "user",
  time: "12:00",
  text: `message-${i}`,
  truncated: false,
}));
const navigation = messages.flatMap((m, i) =>
  m.role === "user"
    ? [
        {
          index: i,
          number: i / 2 + 1,
          question: `question-${i}`,
          answer: `answer-${i + 1}`,
        },
      ]
    : [],
);
const page = (offset: number): Preview => ({
  messages: messages.slice(offset, offset + 20),
  total: messages.length,
  navigation,
  revision: "snapshot-one",
  nextOffset: offset + 20,
  hasMore: offset + 20 < messages.length,
});

test("full navigation previews both sides and jumps directly to a distant page", async () => {
  const load = vi.fn(async (offset: number) => page(offset)),
    user = userEvent.setup();
  render(
    <ConversationView
      initial={page(0)}
      loadPage={load}
      provider="codex"
      lang="en"
    />,
  );
  expect(screen.queryByText("Load more messages")).toBeNull();
  const last = screen.getByRole("button", { name: "Jump to user message 60" });
  await user.hover(last);
  expect(screen.getByRole("tooltip").textContent).toContain("question-118");
  expect(screen.getByRole("tooltip").textContent).toContain("answer-119");
  await user.click(last);
  await waitFor(() => expect(load).toHaveBeenCalledWith(100));
  await screen.findByText("message-118");
  expect(document.querySelectorAll("article").length).toBeLessThan(25);
  await user.click(
    screen.getByRole("button", { name: "Jump to user message 1" }),
  );
  await screen.findByText("message-0");
  expect(load.mock.calls.filter(([offset]) => offset === 0)).toHaveLength(0);
});

test("scrolling fetches nearby pages automatically and a changed snapshot is never mixed", async () => {
  const load = vi.fn(async (offset: number) => ({
    ...page(offset),
    revision: "changed",
  }));
  render(
    <ConversationView
      initial={page(0)}
      loadPage={load}
      provider="codex"
      lang="en"
    />,
  );
  const scroller = screen.getByRole("region", {
    name: "Conversation messages",
  });
  fireEvent.scroll(scroller, { target: { scrollTop: 9600 } });
  await waitFor(() => expect(load).toHaveBeenCalled());
  expect((await screen.findByRole("alert")).textContent).toContain(
    "Conversation changed",
  );
  expect(screen.queryByText("message-40")).toBeNull();
});

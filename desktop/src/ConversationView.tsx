import { useEffect, useRef, useState } from "react";
import { useVirtualizer } from "@tanstack/react-virtual";
import type { Language, Preview, Provider } from "./types";
import { Button } from "./components/ui";

export function ConversationView({
  initial,
  loadPage,
  lang,
  provider,
}: {
  initial: Preview;
  loadPage: (offset: number) => Promise<Preview>;
  lang: Language;
  provider: Provider;
}) {
  const t = (zh: string, en: string) => (lang === "zh" ? zh : en);
  const scroll = useRef<HTMLDivElement>(null);
  const pages = useRef(new Map<number, Preview>([[0, initial]]));
  const pending = useRef(new Set<number>());
  const alive = useRef(true);
  const [version, redraw] = useState(0);
  const [error, setError] = useState("");
  const [hover, setHover] = useState<number | null>(null);
  const [hoverPosition, setHoverPosition] = useState({ left: 20, bottom: 62 });
  const showPreview = (index: number, button: HTMLElement) => {
    const reader = button
      .closest(".conversation-reader")!
      .getBoundingClientRect();
    const bounds = button.getBoundingClientRect();
    setHoverPosition({
      left: Math.max(
        12,
        Math.min(
          bounds.left - reader.left,
          reader.width - Math.min(420, reader.width - 40) - 12,
        ),
      ),
      bottom: reader.bottom - bounds.top + 8,
    });
    setHover(index);
  };
  const [jump, setJump] = useState<number | null>(null);
  const navigation =
    initial.navigation ??
    initial.messages.flatMap((m, i) =>
      m.role === "user"
        ? [
            {
              index: i,
              number: i + 1,
              question: m.text.slice(0, 180),
              answer:
                initial.messages
                  .slice(i + 1)
                  .find((m) => m.role === "assistant")
                  ?.text.slice(0, 300) ?? "",
            },
          ]
        : [],
    );
  const virtual = useVirtualizer({
    count: initial.total,
    getScrollElement: () => scroll.current,
    estimateSize: () => 240,
    scrollPaddingStart: 20,
    overscan: 4,
    initialRect: { width: 900, height: 600 },
  });
  const rows = virtual.getVirtualItems();
  const start = rows[0]?.index ?? 0,
    end = rows.at(-1)?.index ?? 0;
  const activeIndex =
    rows.find((r) => r.end > (virtual.scrollOffset ?? 0) + 1)?.index ?? 0;
  const active = [...navigation]
    .reverse()
    .find((n) => n.index <= activeIndex)?.index;
  const loader = useRef(loadPage);
  loader.current = loadPage;
  const ensure = useRef<(offset: number) => void>(() => {});
  ensure.current = (offset) => {
    if (
      offset < 0 ||
      offset >= initial.total ||
      pages.current.has(offset) ||
      pending.current.has(offset)
    )
      return;
    pending.current.add(offset);
    void loader
      .current(offset)
      .then((page) => {
        if (!alive.current) return;
        if (initial.revision && page.revision !== initial.revision) {
          setError(
            t(
              "会话已更新，请关闭后重新打开以查看最新内容。",
              "Conversation changed. Reopen it to see the latest messages.",
            ),
          );
          return;
        }
        pages.current.set(offset, page);
        // Retain the first page plus a bounded working set for back-scrolling.
        while (pages.current.size > 16) {
          const victim = [...pages.current.keys()].find(
            (k) => k !== 0 && k !== offset && (k + 20 <= start || k > end),
          );
          if (victim === undefined) break;
          pages.current.delete(victim);
        }
        redraw((n) => n + 1);
      })
      .catch((e) => {
        if (alive.current) setError(String(e));
      })
      .finally(() => pending.current.delete(offset));
  };
  useEffect(() => {
    alive.current = true;
    return () => {
      alive.current = false;
    };
  }, []);
  useEffect(() => {
    if (error) return;
    for (let page = Math.floor(start / 20) * 20; page <= end + 20; page += 20)
      ensure.current(page);
  }, [start, end, error, version]);
  useEffect(() => {
    if (jump === null || !pages.current.has(Math.floor(jump / 20) * 20)) return;
    let frame = 0,
      attempts = 0;
    const settle = () => {
      const target = document
        .getElementById(`message-${jump}`)
        ?.getBoundingClientRect();
      const viewport = scroll.current?.getBoundingClientRect();
      if (target && viewport && viewport.height > 0 && target.height > 0) {
        const delta = target.top - viewport.top - 20;
        if (Math.abs(delta) > 1)
          virtual.scrollToOffset((scroll.current?.scrollTop ?? 0) + delta);
      }
      if (++attempts < 4) frame = requestAnimationFrame(settle);
    };
    frame = requestAnimationFrame(() => {
      virtual.scrollToIndex(jump, { align: "start" });
      frame = requestAnimationFrame(settle);
    });
    return () => cancelAnimationFrame(frame);
  }, [jump, version, virtual]);
  const hovered = navigation.find((n) => n.index === hover);
  return (
    <div className="conversation-reader">
      <div
        className="messages virtual-messages"
        ref={scroll}
        role="region"
        tabIndex={0}
        aria-label={t("对话消息", "Conversation messages")}
        onWheel={() => setJump(null)}
        onTouchStart={() => setJump(null)}
        onPointerDown={() => setJump(null)}
        onKeyDown={() => setJump(null)}
      >
        <div
          style={{
            height: virtual.getTotalSize(),
            position: "relative",
            width: "100%",
          }}
        >
          {rows.map((row) => {
            const page = pages.current.get(Math.floor(row.index / 20) * 20);
            const message = page?.messages[row.index % 20];
            return (
              <div
                key={row.key}
                data-index={row.index}
                ref={virtual.measureElement}
                style={{
                  position: "absolute",
                  top: 0,
                  left: 0,
                  width: "100%",
                  transform: `translateY(${row.start}px)`,
                }}
              >
                {message ? (
                  <article
                    className={`message ${message.role}`}
                    id={`message-${row.index}`}
                  >
                    <small>
                      {message.role === "user"
                        ? t("你", "You")
                        : message.role === "notice"
                          ? t("提示", "Notice")
                          : provider === "codex"
                            ? "Codex"
                            : "DeepSeek"}{" "}
                      · {message.time}
                    </small>
                    <pre>{message.text}</pre>
                    {message.truncated && (
                      <p className="muted">
                        {t(
                          "此条消息超过预览长度上限，已截断。",
                          "This message exceeds the preview limit and is truncated.",
                        )}
                      </p>
                    )}
                  </article>
                ) : (
                  <div className="message message-placeholder" aria-busy="true">
                    {t("正在读取消息…", "Reading messages…")}
                  </div>
                )}
              </div>
            );
          })}
        </div>
      </div>
      {error && (
        <div className="conversation-error" role="alert">
          {error}{" "}
          <Button
            onClick={() => {
              setError("");
              redraw((n) => n + 1);
            }}
          >
            {t("重试", "Retry")}
          </Button>
        </div>
      )}
      <nav
        className="message-navigation"
        aria-label={t("对话导航", "Conversation navigation")}
        onMouseLeave={() => setHover(null)}
      >
        {navigation.map((n) => (
          <Button
            key={n.index}
            aria-label={`${t("跳转用户消息", "Jump to user message")} ${n.number}`}
            aria-current={active === n.index ? "location" : undefined}
            aria-describedby={
              hover === n.index ? "conversation-hover" : undefined
            }
            onMouseEnter={(e) => showPreview(n.index, e.currentTarget)}
            onFocus={(e) => showPreview(n.index, e.currentTarget)}
            onBlur={() => setHover(null)}
            onClick={() => {
              setJump(n.index);
              ensure.current(Math.floor(n.index / 20) * 20);
              virtual.scrollToIndex(n.index, { align: "start" });
            }}
          >
            {n.number}
          </Button>
        ))}
      </nav>
      {hovered && (
        <aside
          className="conversation-hover"
          id="conversation-hover"
          role="tooltip"
          style={hoverPosition}
        >
          <strong>
            {t("提问", "Question")} {hovered.number}
          </strong>
          <p>{hovered.question}</p>
          <strong>{t("回答摘要", "Answer summary")}</strong>
          <p>{hovered.answer || t("暂无回答", "No answer yet")}</p>
        </aside>
      )}
    </div>
  );
}

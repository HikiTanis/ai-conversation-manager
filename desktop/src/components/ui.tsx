import {
  useEffect,
  useId,
  useLayoutEffect,
  useRef,
  useState,
  type ComponentProps,
  type ComponentPropsWithRef,
  type ReactNode,
} from "react";

// Shared primitives for both providers and languages. Feature CSS may arrange them,
// but their colors, shape and interaction states come from tokens.css and style.css.
export function Button({
  className = "",
  variant = "default",
  ...props
}: ComponentProps<"button"> & {
  variant?: "default" | "primary" | "danger" | "text";
}) {
  return (
    <button
      type="button"
      {...props}
      className={`ui-button ${variant === "default" ? "" : variant === "text" ? "text-button" : variant} ${className}`}
    />
  );
}
export function Input(props: ComponentProps<"input">) {
  return (
    <input
      {...props}
      className={`${props.type === "checkbox" ? "ui-checkbox" : "ui-input"} ${props.className || ""}`}
    />
  );
}
export function Menu({
  label,
  children,
  icon,
}: {
  label: string;
  children: ReactNode;
  icon?: ReactNode;
}) {
  const id = useId();
  const panel = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const [expanded, setExpanded] = useState(false);
  function close() {
    if (panel.current?.hidePopover) panel.current.hidePopover();
    else if (panel.current) panel.current.style.display = "none";
    setExpanded(false);
  }
  useEffect(() => {
    if (!expanded) return;
    panel.current
      ?.querySelector<HTMLButtonElement>("button:not(:disabled)")
      ?.focus();
    const outside = (event: PointerEvent) => {
      if (
        !panel.current?.contains(event.target as Node) &&
        !trigger.current?.contains(event.target as Node)
      )
        close();
    };
    const scrolled = (event: Event) => {
      if (!panel.current?.contains(event.target as Node)) close();
    };
    const dismiss = (event: KeyboardEvent) => {
      if (event.key === "Escape") {
        close();
        trigger.current?.focus();
      }
    };
    window.addEventListener("keydown", dismiss);
    window.addEventListener("resize", close);
    document.addEventListener("pointerdown", outside);
    document.addEventListener("scroll", scrolled, true);
    return () => {
      window.removeEventListener("keydown", dismiss);
      window.removeEventListener("resize", close);
      document.removeEventListener("pointerdown", outside);
      document.removeEventListener("scroll", scrolled, true);
    };
  }, [expanded]);
  return (
    <>
      <Button
        ref={trigger}
        variant="text"
        className="menu-trigger"
        aria-label={label}
        aria-expanded={expanded}
        aria-controls={id}
        title={label}
        onClick={() => {
          if (expanded) {
            close();
            return;
          }
          const rect = trigger.current!.getBoundingClientRect();
          const el = panel.current!;
          el.style.top = `${Math.max(8, Math.min(rect.bottom + 6, window.innerHeight - 240))}px`;
          el.style.left = `${Math.max(8, Math.min(rect.right - 224, window.innerWidth - 232))}px`;
          if (el.showPopover) el.showPopover();
          else el.style.display = "block";
          setExpanded(true);
        }}
      >
        {icon || label}
      </Button>
      <div
        id={id}
        ref={panel}
        popover="auto"
        className="action-menu"
        data-open={expanded}
        onKeyDown={(event) => {
          if (!["ArrowDown", "ArrowUp", "Home", "End"].includes(event.key))
            return;
          event.preventDefault();
          const buttons = [
            ...panel.current!.querySelectorAll<HTMLButtonElement>(
              "button:not(:disabled)",
            ),
          ];
          const index = buttons.indexOf(
            document.activeElement as HTMLButtonElement,
          );
          const next =
            event.key === "Home"
              ? 0
              : event.key === "End"
                ? buttons.length - 1
                : (index +
                    (event.key === "ArrowDown" ? 1 : -1) +
                    buttons.length) %
                  buttons.length;
          buttons[next]?.focus();
        }}
        onToggle={(e) => setExpanded(e.newState === "open")}
        onClick={(e) => {
          if ((e.target as HTMLElement).closest("button")) close();
        }}
      >
        {children}
      </div>
    </>
  );
}
export function Select({
  value,
  onValueChange,
  options,
  label,
  disabled = false,
}: {
  value: string;
  onValueChange: (value: string) => void;
  options: readonly { value: string; label: string; disabled?: boolean }[];
  label: string;
  disabled?: boolean;
}) {
  const id = useId();
  const trigger = useRef<HTMLButtonElement>(null),
    popup = useRef<HTMLDivElement>(null);
  const [expanded, setExpanded] = useState(false);
  const [active, setActive] = useState(0);
  const search = useRef({ text: "", time: 0 });
  const selected = options.findIndex((o) => o.value === value);
  function close() {
    if (popup.current?.hidePopover) popup.current.hidePopover();
    else if (popup.current) popup.current.style.display = "none";
    setExpanded(false);
  }
  function show(index = selected) {
    if (disabled) return;
    setActive(Math.max(0, index));
    if (popup.current?.showPopover) popup.current.showPopover();
    else if (popup.current) popup.current.style.display = "block";
    setExpanded(true);
  }
  function choose(index: number) {
    const option = options[index];
    if (!option || option.disabled) return;
    onValueChange(option.value);
    close();
    trigger.current?.focus();
  }
  useEffect(() => {
    if (disabled) close();
  }, [disabled]);
  useEffect(() => {
    if (!expanded) return;
    function outside(event: PointerEvent) {
      if (
        !trigger.current?.contains(event.target as Node) &&
        !popup.current?.contains(event.target as Node)
      )
        close();
    }
    document.addEventListener("pointerdown", outside);
    return () => document.removeEventListener("pointerdown", outside);
  }, [expanded]);
  useLayoutEffect(() => {
    if (!expanded) return;
    function position() {
      if (!trigger.current || !popup.current) return;
      const box = trigger.current.getBoundingClientRect();
      const width = Math.min(Math.max(box.width, 280), window.innerWidth - 24);
      const below = window.innerHeight - box.bottom - 14;
      const above = box.top - 14;
      const upwards = below < 160 && above > below;
      Object.assign(popup.current.style, {
        width: `${width}px`,
        left: `${Math.max(12, Math.min(box.left, window.innerWidth - width - 12))}px`,
        top: upwards ? "auto" : `${box.bottom + 6}px`,
        bottom: upwards ? `${window.innerHeight - box.top + 6}px` : "auto",
        maxHeight: `${Math.min(300, Math.max(80, upwards ? above : below))}px`,
      });
    }
    position();
    window.addEventListener("resize", position);
    window.addEventListener("scroll", position, true);
    return () => {
      window.removeEventListener("resize", position);
      window.removeEventListener("scroll", position, true);
    };
  }, [expanded]);
  useEffect(() => {
    if (expanded)
      popup.current?.children[active]?.scrollIntoView?.({ block: "nearest" });
  }, [active, expanded]);
  return (
    <span className="ui-select">
      <Button
        ref={trigger}
        role="combobox"
        aria-label={label}
        aria-haspopup="listbox"
        aria-expanded={expanded}
        aria-controls={id}
        aria-activedescendant={expanded ? `${id}-${active}` : undefined}
        className="ui-select-trigger"
        disabled={disabled}
        onClick={() => (expanded ? close() : show())}
        onKeyDown={(e) => {
          if (["ArrowDown", "ArrowUp", "Home", "End"].includes(e.key)) {
            e.preventDefault();
            const enabled = options
              .map((o, i) => (o.disabled ? -1 : i))
              .filter((i) => i >= 0);
            if (!enabled.length) return;
            let next =
              e.key === "Home"
                ? enabled[0]
                : e.key === "End"
                  ? enabled.at(-1)!
                  : enabled[
                      (enabled.indexOf(expanded ? active : selected) +
                        (e.key === "ArrowDown" ? 1 : -1) +
                        enabled.length) %
                        enabled.length
                    ];
            if (!expanded) show(next);
            else setActive(next);
          } else if (e.key === "Escape" && expanded) {
            e.preventDefault();
            e.stopPropagation();
            close();
          } else if (e.key === "Tab") close();
          else if ((e.key === "Enter" || e.key === " ") && expanded) {
            e.preventDefault();
            choose(active);
          } else if (
            e.key.length === 1 &&
            e.key !== " " &&
            !e.ctrlKey &&
            !e.altKey &&
            !e.metaKey
          ) {
            const text =
              (Date.now() - search.current.time > 800
                ? ""
                : search.current.text) + e.key.toLocaleLowerCase();
            search.current = { text, time: Date.now() };
            const match = options.findIndex(
              (o) =>
                !o.disabled && o.label.toLocaleLowerCase().startsWith(text),
            );
            if (match >= 0) {
              e.preventDefault();
              if (expanded) setActive(match);
              else choose(match);
            }
          }
        }}
      >
        <span>{options[selected]?.label || "—"}</span>
        <span aria-hidden="true" className="ui-select-chevron">
          ⌄
        </span>
      </Button>
      <div
        ref={popup}
        id={id}
        role="listbox"
        aria-label={label}
        aria-hidden={!expanded}
        popover="auto"
        className="ui-select-popup"
        data-open={expanded}
        onToggle={(e) => setExpanded(e.newState === "open")}
      >
        {options.map((option, index) => (
          <div
            key={option.value}
            id={`${id}-${index}`}
            role="option"
            aria-selected={option.value === value}
            aria-disabled={option.disabled || undefined}
            className={`ui-select-option ${active === index ? "active" : ""}`}
            onMouseDown={(e) => e.preventDefault()}
            onMouseMove={() => !option.disabled && setActive(index)}
            onClick={(event) => {
              event.preventDefault();
              event.stopPropagation();
              choose(index);
            }}
          >
            <span>{option.label}</span>
            <span aria-hidden="true">{option.value === value ? "✓" : ""}</span>
          </div>
        ))}
      </div>
    </span>
  );
}
export function Card({
  as: Tag = "section",
  className = "",
  children,
  ...props
}: {
  as?: "section" | "aside";
  className?: string;
  children: ReactNode;
} & ComponentProps<"section">) {
  return (
    <Tag {...props} className={`card ${className}`}>
      {children}
    </Tag>
  );
}
export function Dialog({
  className = "",
  ...props
}: ComponentPropsWithRef<"dialog">) {
  return <dialog {...props} className={`ui-dialog ${className}`} />;
}

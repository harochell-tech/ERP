"use client";

import { useId, useMemo, useRef, useState, type KeyboardEvent } from "react";

// UX5-01 (E-UX5-1…4): a picker you type into. The list filters on every word typed — anywhere in the name, the RNC or the code, with
// no regard to accents or capitals — and is driven by the keyboard (↑ ↓, Enter, Escape) or by touch. It replaces the long <select> of
// customers, suppliers, items, accounts and expense categories; the input keeps the aria-label, so getByLabel finds it.

export type SearchOption = {
  value: string;
  label: string;
  /** Shown after the label and searched too: an RNC, a country, a code. */
  hint?: string | null;
  /** Searched but not shown. */
  keywords?: string | null;
};

/** At most this many options are drawn; typing narrows the rest. */
const SHOWN = 60;

export function fold(text: string): string {
  return text.normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase();
}

export function matches(option: SearchOption, query: string): boolean {
  const haystack = fold(`${option.label} ${option.hint ?? ""} ${option.keywords ?? ""}`);
  return fold(query)
    .split(/\s+/)
    .filter(Boolean)
    .every((word) => haystack.includes(word));
}

export function SearchSelect({
  value,
  onChange,
  options,
  placeholder = "Escriba para buscar…",
  disabled = false,
  clearable = true,
  ...aria
}: {
  value: string;
  onChange: (value: string) => void;
  options: readonly SearchOption[];
  placeholder?: string;
  disabled?: boolean;
  clearable?: boolean;
  "aria-label"?: string;
  "aria-required"?: boolean;
  "aria-invalid"?: boolean;
  "aria-describedby"?: string;
}) {
  const listId = useId();
  const input = useRef<HTMLInputElement>(null);
  const selected = options.find((o) => o.value === value) ?? null;
  const [query, setQuery] = useState<string | null>(null);
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);

  const found = useMemo(() => (query ? options.filter((o) => matches(o, query)) : options), [options, query]);
  const shown = found.slice(0, SHOWN);
  const text = query ?? (selected ? selected.label + (selected.hint ? ` · ${selected.hint}` : "") : "");

  function choose(option: SearchOption | undefined) {
    if (option) {
      onChange(option.value);
    }
    setQuery(null);
    setOpen(false);
  }

  function onKeyDown(e: KeyboardEvent<HTMLInputElement>) {
    if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      e.preventDefault();
      setOpen(true);
      setActive((i) => Math.max(0, Math.min(shown.length - 1, i + (e.key === "ArrowDown" ? 1 : -1))));
    } else if (e.key === "Enter" && open) {
      e.preventDefault();
      choose(shown[active]);
    } else if (e.key === "Escape") {
      setQuery(null);
      setOpen(false);
    }
  }

  return (
    <span className="search-select">
      <input
        ref={input}
        type="text"
        role="combobox"
        autoComplete="off"
        aria-expanded={open}
        aria-controls={listId}
        aria-autocomplete="list"
        aria-activedescendant={open && shown[active] ? `${listId}-${active}` : undefined}
        {...aria}
        value={text}
        placeholder={placeholder}
        disabled={disabled}
        onFocus={() => setOpen(true)}
        onClick={() => setOpen(true)}
        onChange={(e) => {
          setQuery(e.target.value);
          setActive(0);
          setOpen(true);
        }}
        onKeyDown={onKeyDown}
        onBlur={() => {
          setQuery(null);
          setOpen(false);
        }}
      />
      {clearable && value && !disabled ? (
        <button
          type="button"
          className="search-select-clear"
          aria-label="Borrar la selección"
          onMouseDown={(e) => e.preventDefault()}
          onClick={(e) => {
            e.preventDefault();
            onChange("");
            setQuery(null);
            input.current?.focus();
          }}
        >
          ×
        </button>
      ) : null}
      {open && !disabled ? (
        <ul id={listId} role="listbox" className="search-select-list">
          {shown.length === 0 ? (
            <li className="search-select-empty">Nada coincide con «{query}»</li>
          ) : (
            shown.map((o, i) => (
              <li
                key={o.value}
                id={`${listId}-${i}`}
                role="option"
                aria-selected={o.value === value}
                className={i === active ? "is-active" : undefined}
                onMouseDown={(e) => e.preventDefault()}
                onMouseEnter={() => setActive(i)}
                onClick={(e) => {
                  // Inside the Field's <label>, a click would also "click" the input and open the list again.
                  e.preventDefault();
                  choose(o);
                }}
              >
                <span>{o.label}</span>
                {o.hint ? <span className="search-select-hint">{o.hint}</span> : null}
              </li>
            ))
          )}
          {found.length > SHOWN ? <li className="search-select-more">{found.length - SHOWN} más: siga escribiendo para acotar</li> : null}
        </ul>
      ) : null}
    </span>
  );
}

/** A customer or supplier: its legal name, and its RNC (or its country when it is from abroad). */
export function partyOption(id: string, legalName: string, rnc?: string | null, country?: string | null): SearchOption {
  return { value: id, label: legalName, hint: rnc ?? (country ? `Exterior · ${country}` : null) };
}

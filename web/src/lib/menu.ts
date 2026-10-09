// NAV-01 (E-NAV-12/13): the menu's «Ir a…» search and the user's favourites.

export interface MenuEntry {
  href: string;
  label: string;
  group: string;
}

/** Lower case without accents, so «balanza», «Balanza» and «balánza» are the same word. */
export function fold(text: string): string {
  return text.normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase().trim();
}

/** The entries whose label or group holds every typed word; labels starting with the text first. */
export function searchMenu(entries: readonly MenuEntry[], text: string): MenuEntry[] {
  const words = fold(text).split(/\s+/).filter(Boolean);
  if (words.length === 0) {
    return [];
  }
  const found = entries.filter((e) => {
    const hay = `${fold(e.label)} ${fold(e.group)}`;
    return words.every((w) => hay.includes(w));
  });
  const first = fold(text);
  return [...found].sort((a, b) => Number(fold(b.label).startsWith(first)) - Number(fold(a.label).startsWith(first)));
}

const FAVORITES_KEY = "rochell.menu.favorites";

/** The favourite routes kept in this browser; none when storage is unavailable. */
export function loadFavorites(): string[] {
  try {
    const parsed: unknown = JSON.parse(window.localStorage.getItem(FAVORITES_KEY) ?? "[]");
    return Array.isArray(parsed) ? parsed.filter((h): h is string => typeof h === "string") : [];
  } catch {
    return [];
  }
}

let memory: string[] | null = null;
const listeners = new Set<() => void>();
const NONE: readonly string[] = [];

export function saveFavorites(hrefs: readonly string[]): void {
  memory = [...hrefs];
  try {
    window.localStorage.setItem(FAVORITES_KEY, JSON.stringify(hrefs));
  } catch {
    // A private window: favourites last until the page closes.
  }
  listeners.forEach((notify) => notify());
}

/** For `useSyncExternalStore`: the same array until the favourites change; none while exporting the page. */
export const favoritesStore = {
  subscribe(notify: () => void): () => void {
    listeners.add(notify);
    return () => listeners.delete(notify);
  },
  snapshot(): readonly string[] {
    memory ??= loadFavorites();
    return memory;
  },
  serverSnapshot(): readonly string[] {
    return NONE;
  },
};

/** Adds the route at the end, or removes it. */
export function toggleFavorite(hrefs: readonly string[], href: string): string[] {
  return hrefs.includes(href) ? hrefs.filter((h) => h !== href) : [...hrefs, href];
}

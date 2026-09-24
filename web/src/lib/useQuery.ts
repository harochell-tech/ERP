"use client";

import { useEffect, useState } from "react";

/** Loads data once per change of `deps`; `reload` runs it again (e.g. after a command). */
export function useLoad<T>(load: (() => Promise<T>) | null, deps: readonly unknown[]) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [generation, setGeneration] = useState(0);

  useEffect(() => {
    if (load === null) {
      return;
    }
    let cancelled = false;
    load()
      .then((value) => {
        if (!cancelled) {
          setData(value);
          setError(null);
        }
      })
      .catch((caught: unknown) => {
        if (!cancelled) {
          setError(caught);
        }
      });
    return () => {
      cancelled = true;
    };
    // The caller lists what `load` depends on.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, generation]);

  return { data, error, reload: () => setGeneration((g) => g + 1) };
}

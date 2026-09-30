"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";

// E-UX1-01-9: a green notice for a few seconds after each command, naming the document and the result. It lives above the pages,
// so a notice survives the client-side navigation that often follows a command (e.g. create → detail).

interface Toast {
  id: number;
  message: string;
}

interface ToastContextValue {
  notify: (message: string) => void;
}

const ToastContext = createContext<ToastContextValue | null>(null);
/** How long a notice stays (ms). */
export const TOAST_MS = 6000;

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([]);
  const next = useRef(1);
  const timers = useRef(new Map<number, ReturnType<typeof setTimeout>>());

  const dismiss = useCallback((id: number) => {
    setToasts((current) => current.filter((t) => t.id !== id));
    const timer = timers.current.get(id);
    if (timer) {
      clearTimeout(timer);
      timers.current.delete(id);
    }
  }, []);

  const notify = useCallback(
    (message: string) => {
      const id = next.current++;
      setToasts((current) => [...current.slice(-2), { id, message }]);
      timers.current.set(
        id,
        setTimeout(() => dismiss(id), TOAST_MS),
      );
    },
    [dismiss],
  );

  useEffect(() => {
    const pending = timers.current;
    return () => pending.forEach((timer) => clearTimeout(timer));
  }, []);

  const value = useMemo(() => ({ notify }), [notify]);
  return (
    <ToastContext.Provider value={value}>
      {children}
      <div className="toasts" role="status" aria-live="polite" data-testid="toasts">
        {toasts.map((t) => (
          <div key={t.id} className="toast" data-testid="toast">
            <span>{t.message}</span>
            <button type="button" className="toast-close" aria-label="Cerrar aviso" onClick={() => dismiss(t.id)}>
              ×
            </button>
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  );
}

/** `notify` shows a success notice; outside the provider (unit tests) it does nothing. */
export function useToast(): ToastContextValue {
  return useContext(ToastContext) ?? { notify: () => undefined };
}

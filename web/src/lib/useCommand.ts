"use client";

import { useCallback, useState } from "react";
import { ApiError, runCommand, stepUpUrl, type CommandBody, type CommandResponse, type CompanyPostPath } from "@/api/client";
import { clearDraft, peekDraft, saveDraft } from "./drafts";
import { useSession } from "./session";

function newKey(): string {
  return crypto.randomUUID();
}

function sessionStore(): Storage | null {
  try {
    return window.sessionStorage;
  } catch {
    return null;
  }
}

/**
 * One command intent (E-03): its Idempotency-Key is created when the form opens and reused on every retry until the command
 * succeeds. On STEP_UP_REQUIRED the form values and the key are kept and the user re-authenticates (E-PR18b-6).
 * `formId` must be unique per form instance (e.g. "approve-po:<id>").
 */
export function useCommand<P extends CompanyPostPath, V = unknown>(formId: string, path: P) {
  const { companyId } = useSession();
  const [restored] = useState(() => {
    const storage = sessionStore();
    return storage ? peekDraft<V>(storage, formId) : null;
  });
  const [key, setKey] = useState(() => restored?.key ?? newKey());
  const [error, setError] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  const run = useCallback(
    async (body: CommandBody<P>, values?: V): Promise<CommandResponse | undefined> => {
      setBusy(true);
      setError(null);
      try {
        const response = await runCommand(path, companyId, body, key);
        const storage = sessionStore();
        if (storage) {
          clearDraft(storage, formId);
        }
        setKey(newKey());
        return response;
      } catch (caught) {
        if (caught instanceof ApiError && caught.code === "STEP_UP_REQUIRED") {
          const storage = sessionStore();
          if (storage) {
            saveDraft(storage, formId, { key, values: values ?? null });
          }
          window.location.assign(stepUpUrl(window.location.pathname + window.location.search));
          return undefined;
        }
        setError(caught);
        return undefined;
      } finally {
        setBusy(false);
      }
    },
    [companyId, formId, key, path],
  );

  return { run, busy, error, restored: restored?.values ?? null, wasRestored: restored !== null };
}

// E-PR18b-6: before re-authenticating, a form keeps its data and its Idempotency-Key in sessionStorage; when the user comes
// back the form is filled again and the user presses the button again (no automatic resubmission), with the same key.

export interface Draft<T = unknown> {
  key: string;
  values: T;
}

const PREFIX = "rochell.draft.";

export function saveDraft<T>(storage: Storage, formId: string, draft: Draft<T>): void {
  storage.setItem(PREFIX + formId, JSON.stringify(draft));
}

/** Reads without removing: React may run initializers twice; the draft is cleared only after the command succeeds. */
export function peekDraft<T>(storage: Storage, formId: string): Draft<T> | null {
  const text = storage.getItem(PREFIX + formId);
  if (text === null) {
    return null;
  }
  try {
    const draft = JSON.parse(text) as Draft<T>;
    return typeof draft.key === "string" ? draft : null;
  } catch {
    return null;
  }
}

export function clearDraft(storage: Storage, formId: string): void {
  storage.removeItem(PREFIX + formId);
}

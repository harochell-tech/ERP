import { describe, expect, it } from "vitest";
import { clearDraft, peekDraft, saveDraft } from "@/lib/drafts";

class MemoryStorage implements Storage {
  private items = new Map<string, string>();
  get length() {
    return this.items.size;
  }
  clear() {
    this.items.clear();
  }
  getItem(key: string) {
    return this.items.get(key) ?? null;
  }
  key(index: number) {
    return [...this.items.keys()][index] ?? null;
  }
  removeItem(key: string) {
    this.items.delete(key);
  }
  setItem(key: string, value: string) {
    this.items.set(key, value);
  }
}

describe("step-up drafts (E-PR18b-6)", () => {
  it("keep the idempotency key and the values until the command succeeds", () => {
    const storage = new MemoryStorage();
    saveDraft(storage, "approve-po:1", { key: "k-1", values: { reason: "x" } });

    expect(peekDraft(storage, "approve-po:1")).toEqual({ key: "k-1", values: { reason: "x" } });
    expect(peekDraft(storage, "approve-po:1")?.key).toBe("k-1"); // peeking twice (React strict mode) keeps it
    clearDraft(storage, "approve-po:1");
    expect(peekDraft(storage, "approve-po:1")).toBeNull();
  });

  it("ignore corrupted entries", () => {
    const storage = new MemoryStorage();
    storage.setItem("rochell.draft.f", "{not json");
    expect(peekDraft(storage, "f")).toBeNull();
  });
});

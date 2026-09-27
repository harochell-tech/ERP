import { describe, expect, it } from "vitest";
import { CASES_TEMPLATE, DEFINITION_TEMPLATES, initialPolicyValues, parseJson, sha256Hex } from "@/lib/configuration";

describe("configuration screens (E-B03-15)", () => {
  it("hashes a file with SHA-256 in hex", async () => {
    const bytes = new TextEncoder().encode("abc");
    expect(await sha256Hex(bytes.buffer)).toBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
  });

  it("templates are valid JSON of the expected shape", () => {
    expect(JSON.parse(DEFINITION_TEMPLATES.PURCHASE_ITBIS)).toMatchObject({ tax_code: "ITBIS", rate: "0.18" });
    expect(JSON.parse(DEFINITION_TEMPLATES.PURCHASE_WITHHOLDING)).toMatchObject({ base: "ITBIS", party_types: ["INDIVIDUAL"] });
    expect(JSON.parse(CASES_TEMPLATE)[0]).toMatchObject({ caseId: "caso-1", netAmount: "1000.00" });
  });

  it("reports invalid JSON in Spanish instead of throwing", () => {
    expect(parseJson("{").error).toMatch(/^JSON inválido/);
    expect(parseJson<number[]>("[1]").value).toEqual([1]);
  });

  it("a new policy version starts from the active one, every parameter present", () => {
    const definitions = [{ paramCode: "a" }, { paramCode: "b" }, { paramCode: "c" }];
    const versions = [
      { status: "DRAFT", version: 3, parameters: { a: "9", b: "9" } },
      { status: "ACTIVE", version: 2, parameters: { a: "0.05", b: "10" } },
    ];
    expect(initialPolicyValues(definitions, versions)).toEqual({ a: "0.05", b: "10", c: "" });
    expect(initialPolicyValues(definitions, [])).toEqual({ a: "", b: "", c: "" });
  });
});

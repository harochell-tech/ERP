import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { ApiError } from "@/api/client";
import { ERROR_MESSAGES, describeError } from "@/lib/errors";

/** Every code the C# sources declare as a string constant (domain, authorization and transport errors). */
function declaredCodes(): Set<string> {
  const codes = new Set<string>();
  const walk = (dir: string) => {
    for (const name of readdirSync(dir)) {
      const path = join(dir, name);
      if (name === "bin" || name === "obj") {
        continue;
      }
      if (statSync(path).isDirectory()) {
        walk(path);
      } else if (name.endsWith(".cs")) {
        for (const match of readFileSync(path, "utf8").matchAll(/const string \w+ = "([A-Z_]+)"/g)) {
          codes.add(match[1]!);
        }
      }
    }
  };
  walk(join(__dirname, "..", "..", "..", "src"));
  return codes;
}

describe("Spanish error messages (E-PR18b-11)", () => {
  it("only name codes the API can return", () => {
    const declared = declaredCodes();
    const unknown = Object.keys(ERROR_MESSAGES).filter((code) => !declared.has(code));
    expect(unknown).toEqual([]);
  });

  it("fall back to the API message and correlation id", () => {
    expect(describeError(new ApiError(422, "APPROVER_IS_CREATOR", "x", "c1"))).toEqual({ message: "No puede aprobar un documento que usted creó.", correlationId: "c1" });
    expect(describeError(new ApiError(422, "SOMETHING_NEW", "Detail.", "c2"))).toEqual({ message: "Error SOMETHING_NEW", detail: "Detail.", correlationId: "c2" });
  });
});

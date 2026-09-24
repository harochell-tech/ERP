// E-PR18b-3: fails when src/api/schema.d.ts is not what openapi-typescript generates from src/Rochell.Api/openapi.json.
import { execFileSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

const dir = mkdtempSync(join(tmpdir(), "rochell-api-types-"));
const generated = join(dir, "schema.d.ts");
try {
  execFileSync(process.execPath, ["node_modules/openapi-typescript/bin/cli.js", "../src/Rochell.Api/openapi.json", "--default-non-nullable", "false", "--output", generated], { stdio: "pipe" });
  if (readFileSync(generated, "utf8") !== readFileSync("src/api/schema.d.ts", "utf8")) {
    console.error("src/api/schema.d.ts is out of date with src/Rochell.Api/openapi.json. Run: npm run gen:api");
    process.exit(1);
  }
  console.log("API types match openapi.json.");
} finally {
  rmSync(dir, { recursive: true, force: true });
}

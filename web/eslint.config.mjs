import nextVitals from "eslint-config-next/core-web-vitals";
import nextTypeScript from "eslint-config-next/typescript";

const config = [
  ...nextVitals,
  ...nextTypeScript,
  { ignores: ["node_modules/**", ".next/**", "out/**", "src/api/schema.d.ts", "next-env.d.ts", "playwright-report/**", "test-results/**"] },
];

export default config;

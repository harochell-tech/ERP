// UX3-02 (E-UX3-2/3): reconciliations in words. The names, the guidance and the readable keys are the server's; these helpers only
// choose what to show when one is missing. Pure, unit-tested.
import type { Schemas } from "@/api/client";

type Exception = Schemas["ReconciliationExceptionView"];

const SEVERITIES: Readonly<Record<string, string>> = { ERROR: "Error", WARNING: "Aviso" };

/** ERROR → "Error", WARNING → "Aviso". */
export function severityLabel(severity: string): string {
  return SEVERITIES[severity] ?? severity;
}

/** The status tone of a severity (the colour always comes with the label). */
export function severityTone(severity: string): "error" | "attention" | "neutral" {
  return severity === "ERROR" ? "error" : severity === "WARNING" ? "attention" : "neutral";
}

/** E-UX3-3: the readable key of an exception, or the raw key when the server has no label for its shape. */
export function exceptionKey(exception: Pick<Exception, "matchKey" | "matchLabel">): string {
  return exception.matchLabel?.trim() ? exception.matchLabel : exception.matchKey;
}

/** E-UX3-2: the classification's Spanish name, or its code when the server has none. */
export function classificationText(exception: Pick<Exception, "classification" | "classificationName">): string {
  return exception.classificationName?.trim() ? exception.classificationName : exception.classification;
}

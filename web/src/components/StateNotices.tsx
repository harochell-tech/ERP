"use client";

import Link from "next/link";
import type { ReactNode } from "react";
import { ErrorBox } from "./ui";
import "./states.css";

/**
 * UX4-03 (G-31): a loading indicator that looks like one (a spinner beside the text, announced politely to screen readers),
 * or the error when the load failed.
 */
export function LoadingIndicator({ error, children }: { error?: unknown; children?: ReactNode }) {
  if (error) {
    return <ErrorBox error={error} />;
  }
  return (
    <div className="ux4-loading" role="status" aria-live="polite" data-testid="loading">
      <span className="ux4-spinner" aria-hidden="true" />
      <span>{children ?? "Cargando…"}</span>
    </div>
  );
}

export type NextStep = { href: string; label: string };

/**
 * UX4-03 (G-31, V-32/V-33, A-20): an empty list says what the screen is for and what to do next, never a bare "Ninguno.".
 * `steps` are links shown only when given (the caller filters them by permission).
 */
export function EmptyState({
  title,
  children,
  steps = [],
  testId,
}: {
  title: string;
  children?: ReactNode;
  steps?: readonly (NextStep | null | false | undefined | "" | 0)[];
  testId?: string;
}) {
  const links = steps.filter((step): step is NextStep => Boolean(step));
  return (
    <div className="ux4-empty" data-testid={testId ?? "empty-state"}>
      <strong>{title}</strong>
      {children}
      {links.length > 0 ? (
        <div className="ux4-empty-actions">
          {links.map((step) => (
            <Link key={step.href} href={step.href}>
              {step.label}
            </Link>
          ))}
        </div>
      ) : null}
    </div>
  );
}

/** UX4-03 (P-14): a short numbered help ("1. … 2. … 3. …"). */
export function StepsHelp({ steps, testId }: { steps: readonly ReactNode[]; testId?: string }) {
  return (
    <ol className="ux4-help" data-testid={testId}>
      {steps.map((step, index) => (
        <li key={index}>{step}</li>
      ))}
    </ol>
  );
}

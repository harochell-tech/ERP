import { personLabel } from "@/lib/identities";

/**
 * E-UX1-01-3: a person as the Google name with the e-mail as secondary text; the visible text equals personLabel(…) so a table row
 * and a select option read alike. Long names and e-mails wrap.
 */
export function Person({ name, email, fallback }: { name: string | null | undefined; email: string | null | undefined; fallback?: string }) {
  const trimmed = name?.trim();
  if (trimmed && email && trimmed !== email) {
    return (
      <span className="wrap" title={email}>
        <strong>{trimmed}</strong>
        <span className="muted"> · {email}</span>
      </span>
    );
  }
  return <span className="wrap">{personLabel(name, email, fallback)}</span>;
}

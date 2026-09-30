import { statusTone } from "@/lib/labels";
import { quoteStatusLabel } from "@/lib/quotes";

/** QUO1-04 (E-QUO1-04-2/4): a quote's status as a coloured label; a SENT quote past its validity reads "Vencida" (attention). */
export function QuoteStatusBadge({ status, expired, testId }: { status: string; expired: boolean; testId?: string }) {
  const tone = status === "SENT" && expired ? "attention" : statusTone(status);
  return (
    <span className={`badge tone-${tone}`} data-testid={testId}>
      {quoteStatusLabel(status, expired)}
    </span>
  );
}

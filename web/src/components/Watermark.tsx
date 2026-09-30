/** UX3-02 (E-UX3-7, E-UX3-10): a diagonal mark over a printable document, visible on screen and on paper. */
export function Watermark({ text }: { text: string | null }) {
  return text ? (
    <div className="watermark" data-testid="watermark" aria-label={`Marca de agua: ${text}`} role="note">
      <span>{text}</span>
    </div>
  ) : null;
}

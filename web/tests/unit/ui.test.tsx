import { renderToStaticMarkup } from "react-dom/server";
import { describe, expect, it } from "vitest";
import { ConfirmDialog, Field, FieldMessage, fieldAria } from "@/components/ui";

// UX1-01b (E-UX1-01-6 / E-UX1-01-8): the shared form field and the confirmation dialog, rendered to markup (no DOM in these
// tests; the dialog's focus handling and Escape are the browser's native <dialog> behaviour, exercised by the Playwright journeys).

describe("Field (E-UX1-01-6)", () => {
  it("marks a required field and ties its error to the input", () => {
    const html = renderToStaticMarkup(
      <Field label="Cliente" required error="Elija el cliente.">
        <select aria-label="Cliente" />
      </Field>,
    );
    expect(html).toContain('aria-required="true"');
    expect(html).toContain('aria-invalid="true"');
    const describedBy = /aria-describedby="([^"]+)"/.exec(html)?.[1];
    expect(describedBy).toBeTruthy();
    expect(html).toContain(`<span id="${describedBy}" class="field-error">Elija el cliente.</span>`);
    // The asterisk is CSS content with empty alternative text: the label text and accessible name stay "Cliente".
    expect(html).toContain('<span class="field-label is-required">Cliente</span>');
    expect(html).toContain("has-error");
  });

  it("adds nothing to an optional field without error", () => {
    const html = renderToStaticMarkup(
      <Field label="Referencia">
        <input />
      </Field>,
    );
    expect(html).not.toContain("aria-required");
    expect(html).not.toContain("aria-invalid");
    expect(html).not.toContain("aria-describedby");
  });

  it("describes a hint when there is no error", () => {
    const html = renderToStaticMarkup(
      <Field label="Monto" hint="Hasta 2 decimales.">
        <input />
      </Field>,
    );
    const describedBy = /aria-describedby="([^"]+)"/.exec(html)?.[1];
    expect(html).toContain(`<span id="${describedBy}" class="field-hint">Hasta 2 decimales.</span>`);
  });

  it("gives a line cell the same wiring", () => {
    expect(fieldAria("Cantidad inválida.", "m1", true)).toEqual({ "aria-required": true, "aria-invalid": true, "aria-describedby": "m1" });
    expect(fieldAria(null, "m1")).toEqual({ "aria-required": undefined, "aria-invalid": undefined, "aria-describedby": undefined });
    expect(renderToStaticMarkup(<FieldMessage id="m1" error="Cantidad inválida." />)).toBe('<span id="m1" class="field-error">Cantidad inválida.</span>');
    expect(renderToStaticMarkup(<FieldMessage id="m1" error={null} />)).toBe("");
  });
});

describe("ConfirmDialog (E-UX1-01-8)", () => {
  const noop = () => undefined;

  it("states the consequence, offers cancel first and warns about re-authentication", () => {
    const html = renderToStaticMarkup(
      <ConfirmDialog open title="¿Liberar el pago?" confirmLabel="Confirmar: Liberar" stepUp onConfirm={noop} onCancel={noop}>
        <p>El banco recibirá la orden de pago.</p>
      </ConfirmDialog>,
    );
    expect(html).toContain("¿Liberar el pago?");
    expect(html).toContain("El banco recibirá la orden de pago.");
    expect(html).toContain("autenticación reciente");
    expect(html.indexOf("Cancelar")).toBeLessThan(html.indexOf("Confirmar: Liberar"));
    expect(html).toMatch(/aria-labelledby="[^"]+"/);
  });

  it("renders no content while closed and disables confirmation while busy or invalid", () => {
    expect(renderToStaticMarkup(<ConfirmDialog open={false} title="x" confirmLabel="Confirmar: x" onConfirm={noop} onCancel={noop} />)).not.toContain("Confirmar: x");
    const busy = renderToStaticMarkup(<ConfirmDialog open busy title="x" confirmLabel="Confirmar: x" onConfirm={noop} onCancel={noop} />);
    expect(busy).toMatch(/<button type="submit" class="primary" disabled="">Confirmar: x<\/button>/);
    const invalid = renderToStaticMarkup(<ConfirmDialog open danger confirmDisabled title="x" confirmLabel="Confirmar: x" onConfirm={noop} onCancel={noop} />);
    expect(invalid).toMatch(/<button type="submit" class="danger-solid" disabled="">Confirmar: x<\/button>/);
    expect(invalid).not.toContain("autenticación reciente");
  });
});

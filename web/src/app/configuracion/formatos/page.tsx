"use client";

import { useEffect, useState } from "react";
import { query, type Schemas } from "@/api/client";
import { DocumentView } from "@/components/PrintedDocument";
import { LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, Field, NoPermission } from "@/components/ui";
import { formatDateTime } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { previewQuery } from "@/lib/ux4a";

// PRT-02 (E-PRT-3…10, E-PRT-02-1…7): Configuración › Formatos de impresión. Per document: a draft edited on the simple screen (paper,
// margins, font, rows, colour, logo, columns, fixed texts) or in advanced mode (Liquid template and CSS), previewed with a real
// document or an example, and activated with step-up once the server finds what is mandatory. Versions stay; any can be restored.
// The Director changes them (print_format:manage); whoever reads the configuration sees them.

type Settings = Schemas["PrintSettings"];
type Column = Schemas["PrintColumnSetting"];
type TypeView = Schemas["PrintFormatTypeView"];

const PAPERS: Record<string, string> = { CARTA: "Carta", MEDIA_CARTA: "Media carta", TICKET_80: "Ticket de 80 mm", ETIQUETA_100X150: "Etiqueta de 100 × 150 mm" };
const ALIGN: Record<string, string> = { IZQUIERDA: "Izquierda", CENTRO: "Centro", DERECHA: "Derecha" };
const STATUS: Record<string, string> = { DRAFT: "Borrador", ACTIVE: "Activo", RETIRED: "Retirado" };

function readBase64(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result).replace(/^data:[^,]*,/, ""));
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });
}

function Logo({ list, canManage, onDone }: { list: Schemas["PrintFormatList"]; canManage: boolean; onDone: () => void }) {
  const set = useCommand("set-company-logo", "/api/v1/companies/{companyId}/sales/set-company-logo", "Logo guardado. Cada formato decide si lo muestra.");
  return (
    <section className="card" data-testid="company-logo">
      <h2 style={{ marginTop: 0 }}>Logo de la empresa</h2>
      {list.logo ? (
        // eslint-disable-next-line @next/next/no-img-element
        <img src={`data:${list.logo.contentType};base64,${list.logo.contentBase64}`} alt="Logo de la empresa" style={{ maxWidth: 220, maxHeight: 120 }} />
      ) : (
        <p className="muted">Todavía no hay logo.</p>
      )}
      {list.logo ? <p className="muted">Cargado el {formatDateTime(list.logo.setAt)}{list.logo.setBy ? ` por ${list.logo.setBy}` : ""}.</p> : null}
      {canManage ? (
        <Field label="Cargar logo (PNG o JPEG, hasta 1 MB)">
          <input
            type="file"
            accept="image/png,image/jpeg"
            onChange={async (e) => {
              const file = e.target.files?.[0];
              if (file && (await set.run({ contentBase64: await readBase64(file) }))) {
                onDone();
              }
            }}
          />
        </Field>
      ) : null}
      <ErrorBox error={set.error} />
    </section>
  );
}

function ColumnsEditor({ columns, onChange }: { columns: Column[]; onChange: (c: Column[]) => void }) {
  const move = (i: number, d: number) => {
    const next = [...columns];
    const [c] = next.splice(i, 1);
    if (c) {
      next.splice(i + d, 0, c);
    }
    onChange(next);
  };
  const patch = (i: number, p: Partial<Column>) => onChange(columns.map((c, j) => (j === i ? { ...c, ...p } : c)));
  return (
    <div className="table-wrap">
      <table data-testid="format-columns">
        <thead>
          <tr>
            <th>Mostrar</th>
            <th>Título</th>
            <th>Ancho (%)</th>
            <th>Alineación</th>
            <th>Orden</th>
          </tr>
        </thead>
        <tbody>
          {columns.map((c, i) => (
            <tr key={c.clave}>
              <td>
                <input type="checkbox" aria-label={`Mostrar ${c.titulo}`} checked={c.mostrar} onChange={(e) => patch(i, { mostrar: e.target.checked })} />
              </td>
              <td>
                <input aria-label={`Título de ${c.clave}`} value={c.titulo} maxLength={60} onChange={(e) => patch(i, { titulo: e.target.value })} />
              </td>
              <td>
                <input
                  aria-label={`Ancho de ${c.titulo}`}
                  inputMode="numeric"
                  value={c.ancho ?? ""}
                  placeholder="auto"
                  onChange={(e) => patch(i, { ancho: e.target.value === "" ? null : Number(e.target.value.replace(/\D/g, "")) })}
                />
              </td>
              <td>
                <select aria-label={`Alineación de ${c.titulo}`} value={c.alineacion} onChange={(e) => patch(i, { alineacion: e.target.value })}>
                  {Object.entries(ALIGN).map(([k, v]) => (
                    <option key={k} value={k}>
                      {v}
                    </option>
                  ))}
                </select>
              </td>
              <td className="actions">
                <button type="button" disabled={i === 0} aria-label={`Subir ${c.titulo}`} onClick={() => move(i, -1)}>
                  ↑
                </button>
                <button type="button" disabled={i === columns.length - 1} aria-label={`Bajar ${c.titulo}`} onClick={() => move(i, 1)}>
                  ↓
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function Editor({ type, canManage, onDone }: { type: TypeView; canManage: boolean; onDone: () => void }) {
  const { companyId } = useSession();
  const start = type.draftVersion ?? type.activeVersion;
  const detail = useLoad(
    () => query("/api/v1/companies/{companyId}/sales/print-formats/{documentType}/{version}", { path: { companyId, documentType: type.documentType, version: String(start) } }),
    [companyId, type.documentType, start],
  );
  const save = useCommand(`save-format:${type.documentType}`, "/api/v1/companies/{companyId}/sales/save-print-format-draft", "Borrador guardado. Revíselo en la vista previa y actívelo.");
  const activate = useCommand(`activate-format:${type.documentType}`, "/api/v1/companies/{companyId}/sales/activate-print-format", "Formato activado: los documentos ya se imprimen así.");
  const restore = useCommand(`restore-format:${type.documentType}`, "/api/v1/companies/{companyId}/sales/restore-print-format", "Versión copiada al borrador.");
  const [settings, setSettings] = useState<Settings | null>(null);
  const [body, setBody] = useState("");
  const [css, setCss] = useState("");
  const [note, setNote] = useState("");
  const [documentNo, setDocumentNo] = useState("");
  const [preview, setPreview] = useState<Schemas["PrintFormatPreview"] | null>(null);
  const [previewError, setPreviewError] = useState<unknown>(null);
  useEffect(() => {
    if (detail.data) {
      // eslint-disable-next-line react-hooks/set-state-in-effect -- the editor starts from the loaded version, once per load
      setSettings(detail.data.settings);
      setBody(detail.data.body);
      setCss(detail.data.css);
      setPreview(null);
    }
  }, [detail.data]);
  if (detail.data === null || settings === null) {
    return <LoadingIndicator error={detail.error} />;
  }
  const advanced = settings.modo === "AVANZADO";
  const set = (p: Partial<Settings>) => setSettings({ ...settings, ...p });
  const texts = (p: Partial<Settings["textos"]>) => set({ textos: { ...settings.textos, ...p } });
  const show = async () => {
    setPreviewError(null);
    try {
      setPreview(
        await previewQuery("/api/v1/companies/{companyId}/sales/print-formats/preview", companyId, {
          documentType: type.documentType,
          settings,
          body: advanced ? body : null,
          css: advanced ? css : null,
          documentNo: documentNo.trim() || null,
        }),
      );
    } catch (e) {
      setPreviewError(e);
    }
  };
  return (
    <section className="card" data-testid="format-editor">
      <h2 style={{ marginTop: 0 }}>
        {type.label} · {start === 0 ? "formato incluido" : `versión ${start} (${STATUS[detail.data.status] ?? detail.data.status})`}
      </h2>
      <fieldset className="checks">
        <legend>Modo</legend>
        <label>
          <input type="radio" name="modo" checked={!advanced} disabled={!canManage} onChange={() => set({ modo: "SENCILLO" })} /> Sencillo
        </label>
        <label>
          <input
            type="radio"
            name="modo"
            checked={advanced}
            disabled={!canManage}
            onChange={() => {
              set({ modo: "AVANZADO" });
              if (body.trim() === "") {
                setBody(detail.data?.builtInBody ?? "");
              }
            }}
          />{" "}
          Avanzado (plantilla y CSS)
        </label>
      </fieldset>
      <div className="form-grid">
        <Field label="Papel">
          <select value={settings.papel} disabled={!canManage} onChange={(e) => set({ papel: e.target.value })}>
            {type.papers.map((p) => (
              <option key={p} value={p}>
                {PAPERS[p] ?? p}
              </option>
            ))}
          </select>
        </Field>
        <Field label="Márgenes (mm)">
          <input inputMode="numeric" value={settings.margen_mm} disabled={!canManage} onChange={(e) => set({ margen_mm: Number(e.target.value.replace(/\D/g, "")) })} />
        </Field>
        {!advanced ? (
          <>
            <Field label="Letra (px)">
              <input inputMode="numeric" value={settings.letra_px} disabled={!canManage} onChange={(e) => set({ letra_px: Number(e.target.value.replace(/\D/g, "")) })} />
            </Field>
            <Field label="Alto de fila (px)">
              <input inputMode="numeric" value={settings.alto_fila_px} disabled={!canManage} onChange={(e) => set({ alto_fila_px: Number(e.target.value.replace(/\D/g, "")) })} />
            </Field>
            <Field label="Color de títulos">
              <input type="color" value={settings.color} disabled={!canManage} onChange={(e) => set({ color: e.target.value })} />
            </Field>
            <Field label="Mostrar el logo">
              <input type="checkbox" checked={settings.logo} disabled={!canManage} onChange={(e) => set({ logo: e.target.checked })} />
            </Field>
            <Field label="Ancho del logo (mm)">
              <input inputMode="numeric" value={settings.logo_ancho_mm} disabled={!canManage} onChange={(e) => set({ logo_ancho_mm: Number(e.target.value.replace(/\D/g, "")) })} />
            </Field>
            <Field label="Posición del logo">
              <select value={settings.logo_posicion} disabled={!canManage} onChange={(e) => set({ logo_posicion: e.target.value })}>
                {Object.entries(ALIGN).map(([k, v]) => (
                  <option key={k} value={k}>
                    {v}
                  </option>
                ))}
              </select>
            </Field>
          </>
        ) : null}
      </div>
      {!advanced ? (
        <>
          {settings.columnas.length > 0 ? (
            <>
              <h3>Columnas</h3>
              <ColumnsEditor columns={settings.columnas} onChange={(columnas) => set({ columnas })} />
            </>
          ) : null}
          <h3>Textos fijos</h3>
          <Field label="Encabezado" wide>
            <textarea value={settings.textos.encabezado ?? ""} maxLength={1000} disabled={!canManage} onChange={(e) => texts({ encabezado: e.target.value || null })} />
          </Field>
          {type.documentType === "QUOTE" ? (
            <Field label="Condiciones (en blanco: las de siempre)" wide>
              <textarea value={settings.textos.condiciones ?? ""} maxLength={1000} disabled={!canManage} onChange={(e) => texts({ condiciones: e.target.value || null })} />
            </Field>
          ) : null}
          <Field label="Cuentas bancarias para pagos" wide>
            <textarea value={settings.textos.cuentas ?? ""} maxLength={1000} disabled={!canManage} onChange={(e) => texts({ cuentas: e.target.value || null })} />
          </Field>
          <Field label="Pie" wide>
            <textarea value={settings.textos.pie ?? ""} maxLength={1000} disabled={!canManage} onChange={(e) => texts({ pie: e.target.value || null })} />
          </Field>
        </>
      ) : (
        <>
          <p className="muted">
            Plantilla en Liquid: los datos del documento se escriben como {"{{ cliente.nombre }}"}; no se permiten scripts, eventos ni archivos externos. Lo obligatorio se revisa al
            activar.
          </p>
          <Field label="Plantilla" wide>
            <textarea value={body} rows={18} className="mono" disabled={!canManage} onChange={(e) => setBody(e.target.value)} data-testid="format-body" />
          </Field>
          <Field label="CSS" wide>
            <textarea value={css} rows={8} className="mono" disabled={!canManage} onChange={(e) => setCss(e.target.value)} />
          </Field>
        </>
      )}
      <div className="inline-form">
        <Field label="Documento para la vista previa (número; en blanco: el último)">
          <input value={documentNo} onChange={(e) => setDocumentNo(e.target.value)} placeholder="CD-000123" />
        </Field>
        <button type="button" onClick={show} data-testid="format-preview-button">
          Vista previa
        </button>
        {canManage ? (
          <>
            <Field label="Nota del cambio">
              <input value={note} maxLength={500} onChange={(e) => setNote(e.target.value)} />
            </Field>
            <button
              type="button"
              className="primary"
              disabled={save.busy}
              onClick={async () => {
                if (await save.run({ documentType: type.documentType, settings, body: advanced ? body : null, css: advanced ? css : null, note: note.trim() || null })) {
                  onDone();
                }
              }}
            >
              Guardar borrador
            </button>
            {type.draftVersion ? (
              <ConfirmAction
                label={`Activar la versión ${type.draftVersion}`}
                busy={activate.busy}
                consequence={`Desde ahora ${type.label.toLowerCase()} se imprime y se envía con esta versión. Se pedirá confirmar su identidad.`}
                onConfirm={async () => (await activate.run({ documentType: type.documentType, version: type.draftVersion! })) && onDone()}
              />
            ) : null}
          </>
        ) : null}
      </div>
      <ErrorBox error={save.error ?? activate.error ?? previewError} />
      {preview ? (
        <div className="format-preview" data-testid="format-preview">
          <p className="muted">
            {preview.example ? "Documento de ejemplo (todavía no hay uno real de este tipo)." : `Con el documento ${preview.documentNo}.`}
          </p>
          <DocumentView css={preview.document.css} body={preview.document.body} testId="format-preview-document" />
        </div>
      ) : null}
      <h3>Versiones</h3>
      <div className="table-wrap">
        <table data-testid="format-versions">
          <thead>
            <tr>
              <th>Versión</th>
              <th>Estado</th>
              <th>Modo</th>
              <th>Nota</th>
              <th>Creada</th>
              <th>Activada</th>
              <th />
            </tr>
          </thead>
          <tbody>
            <tr>
              <td>0</td>
              <td>{type.activeVersion === 0 ? "Activo" : "Incluido"}</td>
              <td>Sencillo</td>
              <td>Formato «Rochell» incluido</td>
              <td>—</td>
              <td>—</td>
              <td>
                {canManage ? (
                  <button type="button" disabled={restore.busy} onClick={async () => (await restore.run({ documentType: type.documentType, version: 0 })) && onDone()}>
                    Volver al incluido
                  </button>
                ) : null}
              </td>
            </tr>
            {type.versions.map((v) => (
              <tr key={v.version}>
                <td>{v.version}</td>
                <td>{STATUS[v.status] ?? v.status}</td>
                <td>{v.mode === "AVANZADO" ? "Avanzado" : "Sencillo"}</td>
                <td>{v.note ?? ""}</td>
                <td>
                  {formatDateTime(v.createdAt)}
                  {v.createdBy ? ` · ${v.createdBy}` : ""}
                </td>
                <td>{v.activatedAt ? `${formatDateTime(v.activatedAt)}${v.activatedBy ? ` · ${v.activatedBy}` : ""}` : "—"}</td>
                <td>
                  {canManage && v.status !== "DRAFT" ? (
                    <button type="button" disabled={restore.busy} onClick={async () => (await restore.run({ documentType: type.documentType, version: v.version })) && onDone()}>
                      Restaurar
                    </button>
                  ) : null}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <ErrorBox error={restore.error} />
    </section>
  );
}

export default function Page() {
  const { companyId, can } = useSession();
  const allowed = can("configuration:read");
  const { data, error, reload } = useLoad(allowed ? () => query("/api/v1/companies/{companyId}/sales/print-formats", { path: { companyId } }) : null, [companyId]);
  const [selected, setSelected] = useState("DELIVERY_NOTE");
  if (!allowed) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  const canManage = can("print_format:manage");
  const type = data.types.find((t) => t.documentType === selected) ?? data.types[0];
  if (!type) {
    return null;
  }
  return (
    <>
      <h1>Formatos de impresión</h1>
      <p className="muted">
        Cada documento se imprime, se envía por correo y se reimprime con su formato activo. Cambie el borrador, mírelo en la vista previa y actívelo; las versiones anteriores
        quedan y se pueden restaurar.
      </p>
      <Logo list={data} canManage={canManage} onDone={reload} />
      <div className="actions" role="tablist" aria-label="Documentos">
        {data.types.map((t) => (
          <button
            key={t.documentType}
            type="button"
            role="tab"
            aria-selected={t.documentType === type.documentType}
            className={t.documentType === type.documentType ? "primary" : undefined}
            onClick={() => setSelected(t.documentType)}
          >
            {t.label}
            {t.draftVersion ? " · borrador" : ""}
          </button>
        ))}
      </div>
      <Editor key={`${type.documentType}:${type.draftVersion ?? type.activeVersion}`} type={type} canManage={canManage} onDone={reload} />
    </>
  );
}

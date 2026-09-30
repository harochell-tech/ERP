"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { ConfirmAction, ErrorBox, Loading, NoPermission, StatusBadge } from "@/components/ui";
import { formatQuantity } from "@/lib/decimal";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";

// MFG1-07 (E-MFG1-07-4): a recipe version with its materials per batch; the Gerente de planta approves a draft (four eyes: not the
// preparer — the API is the authority).

function Approve({ plantId, recipeVersionId, name, onDone }: { plantId: string; recipeVersionId: string; name: string; onDone: () => void }) {
  const approve = useCommand(`approve-recipe:${recipeVersionId}`, "/api/v1/companies/{companyId}/manufacturing/approve-recipe", `Receta ${name} aprobada y activa.`);
  return (
    <div className="actions">
      <ConfirmAction
        label="Aprobar receta"
        title={`¿Aprobar la receta ${name}?`}
        consequence="La receta queda activa para producir en esa máquina y reemplaza a la versión activa anterior; las corridas nuevas usan estos materiales. No se puede deshacer: un cambio necesita una nueva versión."
        className="primary"
        busy={approve.busy}
        onConfirm={async () => (await approve.run({ plantId, recipeVersionId })) && onDone()}
      />
      <ErrorBox error={approve.error} />
    </div>
  );
}

function RecipeDetail() {
  const id = useSearchParams().get("id") ?? "";
  const { companyId, can } = useSession();
  const { data, error, reload } = useLoad(
    can("production:read") && id
      ? () => query("/api/v1/companies/{companyId}/manufacturing/recipes/{recipeVersionId}", { path: { companyId, recipeVersionId: id } })
      : null,
    [companyId, id],
  );
  if (!can("production:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <Loading error={error} />;
  }
  const r = data.recipe;
  return (
    <>
      <p>
        <Link href="/produccion/recetas/">← Recetas</Link>
      </p>
      <h1>
        Receta {r.itemCode} en {r.machineCode} (versión {r.version})
      </h1>
      <p>
        <StatusBadge status={r.status} testId="recipe-status" /> · {r.itemName} · vigente desde {formatDate(r.effectiveFrom)}
      </p>
      <dl className="cards">
        <div className="stat">
          <dt className="muted">Unidades por tanda</dt>
          <dd className="mono">{formatQuantity(r.unitsPerBatch)}</dd>
        </div>
        <div className="stat">
          <dt className="muted">Unidades por ciclo</dt>
          <dd className="mono">{formatQuantity(r.unitsPerCycle)}</dd>
        </div>
        <div className="stat">
          <dt className="muted">Unidades por rack</dt>
          <dd className="mono">{formatQuantity(r.unitsPerRack)}</dd>
        </div>
        <div className="stat">
          <dt className="muted">Curado</dt>
          <dd>
            mínimo {r.minCuringHours} h, máximo {r.maxCuringHours} h
          </dd>
        </div>
      </dl>
      <h2>Materiales por tanda</h2>
      <div className="table-wrap"><table>
        <thead>
          <tr>
            <th>Material</th>
            <th className="num">Cantidad por tanda</th>
            <th>Unidad</th>
          </tr>
        </thead>
        <tbody>
          {data.lines.map((l) => (
            <tr key={l.materialItemId}>
              <td>
                {l.materialCode} — {l.materialName}
              </td>
              <td className="num">{formatQuantity(l.qtyPerBatch)}</td>
              <td>{l.baseUom}</td>
            </tr>
          ))}
        </tbody>
      </table></div>
      {r.status === "DRAFT" && can("recipe:approve") ? <Approve plantId={r.plantId} recipeVersionId={r.recipeVersionId} name={`${r.itemCode} en ${r.machineCode} (v${r.version})`} onDone={reload} /> : null}
    </>
  );
}

export default function Page() {
  return (
    <Suspense>
      <RecipeDetail />
    </Suspense>
  );
}

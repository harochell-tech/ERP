"use client";

import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Suspense } from "react";
import { query } from "@/api/client";
import { ProductionBadge, Quantity } from "@/components/Production";
import { LoadingIndicator } from "@/components/StateNotices";
import { ConfirmAction, ErrorBox, NoPermission } from "@/components/ui";
import { formatQuantity } from "@/lib/decimal";
import { personLabel } from "@/lib/identities";
import { formatDate } from "@/lib/labels";
import { useSession } from "@/lib/session";
import { useCommand } from "@/lib/useCommand";
import { useLoad } from "@/lib/useQuery";
import { compareRecipeLines, compareRecipeParameters, previousRecipeVersion, quantityWithUnit } from "@/lib/ux4bProduction";

// MFG1-07 (E-MFG1-07-4): a recipe version with its materials per batch; the Gerente de planta approves a draft (four eyes: not the
// preparer — the API is the authority).
// UX4-03 (P-36): who prepared and approved it, and the comparison with the previous version (text comparison of the figures only).

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

/** A user id as a person: "usted" for the reader, the name for whoever may read the users (iam:read), "otra persona" otherwise. */
function usePersonName() {
  const { companyId, can, isMyUserId } = useSession();
  const allowed = can("iam:read");
  const users = useLoad(
    allowed ? () => query("/api/v1/companies/{companyId}/identity/users", { path: { companyId } }).catch(() => ({ items: [] as never[] })) : null,
    [companyId, allowed],
  );
  return (userId: string | null | undefined): string => {
    if (!userId) {
      return "—";
    }
    if (isMyUserId(userId)) {
      return "Usted";
    }
    const user = users.data?.items.find((u) => u.userId === userId);
    return user ? personLabel(user.displayName, user.email) : "Otra persona";
  };
}

function RecipeDetail() {
  const id = useSearchParams().get("id") ?? "";
  const { companyId, can } = useSession();
  const person = usePersonName();
  const { data, error, reload } = useLoad(
    can("production:read") && id
      ? async () => {
          const detail = await query("/api/v1/companies/{companyId}/manufacturing/recipes/{recipeVersionId}", { path: { companyId, recipeVersionId: id } });
          const r = detail.recipe;
          const siblings = await query("/api/v1/companies/{companyId}/manufacturing/recipes", { path: { companyId }, query: { plantId: r.plantId, itemId: r.itemId } });
          const before = previousRecipeVersion(siblings.items, r);
          const previous = before
            ? await query("/api/v1/companies/{companyId}/manufacturing/recipes/{recipeVersionId}", { path: { companyId, recipeVersionId: before.recipeVersionId } })
            : null;
          return { detail, previous };
        }
      : null,
    [companyId, id],
  );
  if (!can("production:read")) {
    return <NoPermission />;
  }
  if (data === null) {
    return <LoadingIndicator error={error} />;
  }
  const { detail, previous } = data;
  const r = detail.recipe;
  const lines = previous ? compareRecipeLines(detail.lines, previous.lines) : [];
  const parameters = previous ? compareRecipeParameters(r, previous.recipe) : [];
  return (
    <>
      <p>
        <Link href="/produccion/recetas/">← Recetas</Link>
      </p>
      <h1>
        Receta {r.itemCode} en {r.machineCode} (versión {r.version})
      </h1>
      <p>
        <ProductionBadge kind="recipe" status={r.status} testId="recipe-status" /> · {r.itemName} · vigente desde {formatDate(r.effectiveFrom)}
      </p>
      <p data-testid="recipe-people">
        Preparada por: <strong>{person(r.preparedBy)}</strong>
        {r.approvedBy ? (
          <>
            {" "}
            · Aprobada por: <strong>{person(r.approvedBy)}</strong>
          </>
        ) : (
          " · Pendiente de aprobación por el Gerente de planta (otra persona que quien la preparó)."
        )}
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
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th>Material</th>
              <th className="num">Cantidad por tanda</th>
            </tr>
          </thead>
          <tbody>
            {detail.lines.map((l) => (
              <tr key={l.materialItemId}>
                <td>{l.materialCode === l.materialName ? l.materialCode : `${l.materialCode} — ${l.materialName}`}</td>
                <td className="num">
                  <Quantity value={l.qtyPerBatch} uom={l.baseUom} />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {previous ? (
        <section data-testid="recipe-comparison">
          <h2>Cambios frente a la versión {previous.recipe.version}</h2>
          <p className="muted">
            <Link href={`/produccion/receta/?id=${previous.recipe.recipeVersionId}`}>Ver la versión {previous.recipe.version}</Link>. Resaltado lo que cambia.
          </p>
          <div className="table-wrap">
            <table className="ux4b-compare">
              <thead>
                <tr>
                  <th>Dato</th>
                  <th className="num">Versión {previous.recipe.version}</th>
                  <th className="num">Esta versión</th>
                </tr>
              </thead>
              <tbody>
                {parameters.map((p) => (
                  <tr key={p.label} className={p.changed ? "changed" : undefined}>
                    <td>{p.label}</td>
                    <td className="num">{p.previous}</td>
                    <td className="num">{p.current}</td>
                  </tr>
                ))}
                {lines.map((l) => (
                  <tr key={l.materialItemId} className={l.changed ? "changed" : undefined}>
                    <td>{l.materialCode} por tanda</td>
                    <td className="num">{l.previous === null ? "No estaba" : quantityWithUnit(l.previous, l.baseUom)}</td>
                    <td className="num">{l.current === null ? "Se quita" : quantityWithUnit(l.current, l.baseUom)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      ) : (
        <p className="muted">Es la primera versión de la receta de este producto en esta máquina.</p>
      )}
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

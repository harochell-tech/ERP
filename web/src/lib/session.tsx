"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { ApiError, query, type Schemas } from "@/api/client";
import { can as canIn, hasPlantScope, queryPlant, scopeOf, type PermissionScope, type SessionCompany } from "./scope";

type SessionDescription = Schemas["SessionDescription"];

export type SessionState =
  | { status: "loading" }
  | { status: "anonymous" }
  | { status: "error"; error: unknown }
  | { status: "ready"; session: SessionDescription };

interface SessionContextValue {
  state: SessionState;
  company: SessionCompany | undefined;
  companyId: string;
  selectCompany: (companyId: string) => void;
  plantId: string | null;
  selectPlant: (plantId: string | null) => void;
  can: (permission: string) => boolean;
  scope: (permission: string) => PermissionScope;
  /** plantId to send on a query guarded by `permission` (E-PR18b-8). */
  plantFor: (permission: string) => string | undefined;
  plantScoped: boolean;
  reload: () => void;
}

const SessionContext = createContext<SessionContextValue | null>(null);
const COMPANY_KEY = "rochell.company";
const PLANT_KEY = "rochell.plant";

function stored(key: string): string | null {
  try {
    return window.localStorage.getItem(key);
  } catch {
    return null;
  }
}

function store(key: string, value: string | null): void {
  try {
    if (value === null) {
      window.localStorage.removeItem(key);
    } else {
      window.localStorage.setItem(key, value);
    }
  } catch {
    // Private windows: the choice simply is not remembered.
  }
}

export function SessionProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<SessionState>({ status: "loading" });
  const [companyId, setCompanyId] = useState<string | null>(null);
  const [plantId, setPlantId] = useState<string | null>(null);
  const [generation, setGeneration] = useState(0);

  useEffect(() => {
    let cancelled = false;
    query("/api/v1/session")
      .then((session) => {
        if (!cancelled) {
          setState({ status: "ready", session });
          setCompanyId((current) => current ?? stored(COMPANY_KEY));
          setPlantId((current) => current ?? stored(PLANT_KEY));
        }
      })
      .catch((error: unknown) => {
        if (!cancelled) {
          setState(error instanceof ApiError && error.status === 401 ? { status: "anonymous" } : { status: "error", error });
        }
      });
    return () => {
      cancelled = true;
    };
  }, [generation]);

  const companies = state.status === "ready" ? state.session.companies : [];
  const company = companies.find((c) => c.companyId === companyId) ?? companies[0];

  const selectCompany = useCallback((id: string) => {
    store(COMPANY_KEY, id);
    setCompanyId(id);
  }, []);
  const selectPlant = useCallback((id: string | null) => {
    store(PLANT_KEY, id);
    setPlantId(id);
  }, []);

  const value = useMemo<SessionContextValue>(
    () => ({
      state,
      company,
      companyId: company?.companyId ?? "",
      selectCompany,
      plantId,
      selectPlant,
      can: (permission) => canIn(company, permission),
      scope: (permission) => scopeOf(company, permission),
      plantFor: (permission) => queryPlant(company, permission, plantId),
      plantScoped: hasPlantScope(company),
      reload: () => setGeneration((g) => g + 1),
    }),
    [state, company, selectCompany, plantId, selectPlant],
  );

  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession(): SessionContextValue {
  const value = useContext(SessionContext);
  if (value === null) {
    throw new Error("useSession outside SessionProvider");
  }
  return value;
}

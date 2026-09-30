// E-PR18b-8: where a permission can be used. A company-wide assignment works in every plant; otherwise only in the plants of
// the plant-scoped assignments that grant it. Mirrors the API rule, which stays the authority.
import type { Schemas } from "@/api/client";

export type SessionCompany = Schemas["SessionCompany"];

export interface PermissionScope {
  companyWide: boolean;
  plants: string[];
}

export function scopeOf(company: SessionCompany | undefined, permission: string): PermissionScope {
  const granting = (company?.assignments ?? []).filter((a) => a.permissions.includes(permission));
  return {
    companyWide: granting.some((a) => a.plantId === null),
    plants: [...new Set(granting.flatMap((a) => (a.plantId === null ? [] : [a.plantId])))],
  };
}

export function can(company: SessionCompany | undefined, permission: string): boolean {
  const scope = scopeOf(company, permission);
  return scope.companyWide || scope.plants.length > 0;
}

/**
 * The plantId a query must send: none for a company-wide reader, otherwise the selected plant when it is one of the
 * reader's plants (or its first plant).
 */
export function queryPlant(company: SessionCompany | undefined, permission: string, selectedPlant: string | null): string | undefined {
  const scope = scopeOf(company, permission);
  if (scope.companyWide) {
    return undefined;
  }
  return selectedPlant !== null && scope.plants.includes(selectedPlant) ? selectedPlant : scope.plants[0];
}

/** Whether the user holds any plant-scoped assignment (the header then offers the plant selector). */
export function hasPlantScope(company: SessionCompany | undefined): boolean {
  return (company?.assignments ?? []).some((a) => a.plantId !== null);
}

/**
 * UX3-02 (E-UX3-9): whether a user id the server returns (e.g. who issued an invoice) is the signed-in user, to hide a decision the
 * server would refuse. As `isMine`, never true for a superadministrator, whose four-eyes controls are waived (E-ADM-2-4).
 */
export function isOwnUserId(userId: string | null | undefined, sessionUserId: string | null | undefined, superadmin: boolean): boolean {
  return !superadmin && !!userId && !!sessionUserId && userId === sessionUserId;
}

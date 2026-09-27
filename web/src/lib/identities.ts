// E-B03-14: test identities (TEST databases only). Pure helpers so the selector's wording is unit-tested.
import { ROLES } from "./labels";

export const ACT_AS_PERMISSION = "identity:act_as";

export interface TestIdentityOption {
  userId: string;
  email: string;
  roles: readonly string[];
}

/** "comprador — Comprador, Aprobador de compras": the mailbox name and the roles in Spanish. */
export function identityLabel(identity: TestIdentityOption): string {
  const name = identity.email.split("@")[0] ?? identity.email;
  return `${name} — ${identity.roles.map((code) => ROLES[code] ?? code).join(", ")}`;
}

/**
 * Whether to show the selector: while acting (to switch or return), or when the signed-in person holds identity:act_as in the
 * selected company.
 */
export function showIdentitySelector(permissions: readonly string[], authenticatedEmail: string | null | undefined): boolean {
  return Boolean(authenticatedEmail) || permissions.includes(ACT_AS_PERMISSION);
}

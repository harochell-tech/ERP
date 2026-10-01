// IMP-02 (E-IMP-02-1): the bulk load brings hundreds of suppliers and customers, more than one page of the API (200). Lists and
// pickers read every page instead of silently showing the first 200.
import { query } from "@/api/client";

const PAGE = 200;

/** Safety stop: 50 pages (10,000 rows) are more than a picker can show anyway. */
const MAX_PAGES = 50;

async function everyPage<T>(load: (offset: number) => Promise<{ items: T[] }>): Promise<{ items: T[] }> {
  const items: T[] = [];
  for (let page = 0; page < MAX_PAGES; page++) {
    const batch = (await load(page * PAGE)).items;
    items.push(...batch);
    if (batch.length < PAGE) {
      break;
    }
  }
  return { items };
}

export function allSuppliers(companyId: string, filter: { status?: string; plantId?: string } = {}) {
  return everyPage((offset) => query("/api/v1/companies/{companyId}/master-data/suppliers", { path: { companyId }, query: { ...filter, limit: PAGE, offset } }));
}

export function allCustomers(companyId: string, filter: { status?: string; search?: string } = {}) {
  return everyPage((offset) => query("/api/v1/companies/{companyId}/sales/customers", { path: { companyId }, query: { ...filter, limit: PAGE, offset } }));
}

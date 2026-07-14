import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { environment } from '../../../environments/environment';
import { SkuAvailability } from '../models/models';

/** Thin HTTP client over the Inventory API. Read-only — the browser never writes stock. */
@Injectable({ providedIn: 'root' })
export class InventoryService {
  private readonly http = inject(HttpClient);

  /**
   * Availability per SKU. Serves two screens: the ops stock panel, and the order form's catalogue —
   * where it is the *only* source of price, since the customer is not allowed to state one (ADR-006).
   */
  listSkus(): Observable<SkuAvailability[]> {
    return this.http.get<SkuAvailability[]>(`${environment.inventoryApi}/api/Inventory`);
  }
}

import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { environment } from '../../../environments/environment';
import { PaymentAttempt } from '../models/models';

/** Thin HTTP client over the Payments API. Read-only — the browser never initiates a charge. */
@Injectable({ providedIn: 'root' })
export class PaymentService {
  private readonly http = inject(HttpClient);

  /**
   * Every payment attempt for one order. Expect exactly one row: two means the idempotency guard failed
   * and the customer was charged twice, which is the failure this endpoint exists to make visible.
   */
  getByOrder(orderId: string): Observable<PaymentAttempt[]> {
    return this.http.get<PaymentAttempt[]>(`${environment.paymentApi}/api/Payments/order/${orderId}`);
  }
}

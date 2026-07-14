import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';

import { environment } from '../../../environments/environment';
import { StuckOrder } from '../models/models';

/**
 * Thin HTTP client over the Fulfillment ops endpoint.
 *
 * Not named in G2's list of services — it is required by G5's stuck panel, which reads "the fulfillment
 * ops endpoint", so the client for it has to exist somewhere.
 */
@Injectable({ providedIn: 'root' })
export class FulfillmentService {
  private readonly http = inject(HttpClient);

  /**
   * Dispatches the broker gave up on. Note what is NOT here: a hard carrier failure (retries exhausted,
   * or a permanent rejection) does not land in the dead-letter queue — it is a business outcome that was
   * *answered*, so the consumer publishes FulfillmentFailed and the saga refunds and releases. These rows
   * are the ones nobody was told about, where money is captured and stock is held with no reply coming.
   */
  listStuck(): Observable<StuckOrder[]> {
    return this.http
      .get<Omit<StuckOrder, 'source' | 'messageType'>[]>(`${environment.fulfillmentApi}/api/Fulfillment/stuck`)
      .pipe(
        // This endpoint reports only its own queue, so it sends no `source`. The ops table merges these
        // rows with the Orders dead-letter feed, which does — so name it here rather than render a blank.
        map((rows) => rows.map((row) => ({ ...row, source: 'dispatch-fulfillment', messageType: 'DispatchFulfillment' }))),
      );
  }
}

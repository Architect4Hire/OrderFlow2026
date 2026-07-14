import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { environment } from '../../../environments/environment';
import { OrderStatus, PlaceOrder, StuckOrder } from '../models/models';

/**
 * Thin HTTP client over the Orders API. No polling in here (G2 [R]2) — cadence is a component concern,
 * because the two surfaces want different ones (2s for one order, 5s for the ops lists) and a service
 * that owned a timer would have to guess.
 */
@Injectable({ providedIn: 'root' })
export class OrderService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${environment.orderApi}/api/Orders`;

  /**
   * Starts the saga. Returns as soon as the order is recorded and ReserveInventory is away — it does NOT
   * wait for inventory, payment, or fulfillment. The 201 means *accepted*, not *fulfilled*, and the
   * returned order is `Placed` every time. Everything after that arrives on {@link getStatus}.
   */
  place(order: PlaceOrder): Observable<OrderStatus> {
    return this.http.post<OrderStatus>(this.baseUrl, order);
  }

  /** One order's current state. This is what the customer view polls. */
  getStatus(id: string): Observable<OrderStatus> {
    return this.http.get<OrderStatus>(`${this.baseUrl}/${id}`);
  }

  /** Orders still in flight — not yet Confirmed or Failed. */
  listActive(): Observable<OrderStatus[]> {
    return this.http.get<OrderStatus[]>(`${this.baseUrl}/active`);
  }

  /**
   * Every dead-letter queue in the system, not just Fulfillment's.
   *
   * Beyond G2's list, and deliberately: G5's stuck panel names the Fulfillment endpoint, which reports
   * only failed dispatches. But a dead-lettered ReleaseInventory means stock is stranded, and a
   * dead-lettered RefundPayment means a customer is out of pocket for an order that already failed —
   * those are the rows that cost real money, and the Fulfillment endpoint cannot see them. Showing only
   * failed dispatches would give an ops lead a screen that looks clean while money is stuck.
   */
  listDeadLetters(): Observable<StuckOrder[]> {
    return this.http.get<StuckOrder[]>(`${this.baseUrl}/dead-letters`);
  }
}

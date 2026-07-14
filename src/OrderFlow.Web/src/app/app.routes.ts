import { Routes } from '@angular/router';

/**
 * Two surfaces, and only two (G3 [R]2): what a customer sees, and what an operator sees. Both are lazy —
 * neither is on the critical path to the other.
 */
export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'order' },
  {
    path: 'order',
    title: 'OrderFlow — Place Order',
    loadComponent: () =>
      import('./features/order/customer-order.component').then((m) => m.CustomerOrderComponent),
  },
  {
    path: 'ops',
    title: 'OrderFlow — Ops',
    loadComponent: () => import('./features/ops/ops.component').then((m) => m.OpsComponent),
  },
  { path: '**', redirectTo: 'order' },
];

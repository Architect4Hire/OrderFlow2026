import { Routes } from '@angular/router';

/**
 * The two product surfaces (G3 [R]2): what a customer sees, and what an operator sees. A third —
 * the failure lab — is a demo/diagnostic surface, not a product one: it injects failures to drive the
 * matrix from the browser, kept deliberately separate from the read-only ops view. All lazy; none is on
 * the critical path to another.
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
  {
    path: 'lab',
    title: 'OrderFlow — Failure Lab',
    loadComponent: () => import('./features/lab/failure-lab.component').then((m) => m.FailureLabComponent),
  },
  { path: '**', redirectTo: 'order' },
];

import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

import { environment, isConfigured } from '../environments/environment';

/** The shell. Stays mounted across navigation; the two surfaces swap beneath it. */
@Component({
  selector: 'of-root',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="topbar">
      <span class="brand">Order<span class="brand-accent">Flow</span></span>
      <nav>
        <a routerLink="/order" routerLinkActive="active">Place Order</a>
        <a routerLink="/ops" routerLinkActive="active">Ops</a>
      </nav>
    </header>

    <main class="page">
      @if (!configured) {
        <!--
          Served outside Aspire, so the app has no idea where the APIs live. Saying so beats letting every
          request fail as an opaque network error against a URL of "" — which looks like the backend is
          down when in fact it was never located.
        -->
        <div class="banner banner-danger">
          <strong>Not configured.</strong> No API URLs were injected, so nothing on these screens can load.
          Start the system with <code>dotnet run --project src/OrderFlow.AppHost</code> and open the
          <code>web</code> resource from the Aspire dashboard.
        </div>
      }
      <router-outlet />
    </main>
  `,
  styles: `
    .topbar {
      position: sticky;
      top: 0;
      z-index: 10;
      display: flex;
      align-items: center;
      gap: 2rem;
      padding: 0.75rem 1.25rem;
      background: var(--bg-surface);
      border-bottom: 1px solid var(--border);
    }

    .brand {
      font-weight: 700;
      font-size: 1.05rem;
      letter-spacing: -0.01em;
    }

    .brand-accent {
      color: var(--accent);
    }

    nav {
      display: flex;
      gap: 1.25rem;
    }

    nav a {
      color: var(--text-muted);
      font-size: 0.9rem;
      font-weight: 500;
      padding: 0.2rem 0;
      border-bottom: 2px solid transparent;
    }

    nav a:hover {
      color: var(--text-primary);
    }

    nav a.active {
      color: var(--text-primary);
      border-bottom-color: var(--accent);
    }
  `,
})
export class AppComponent {
  protected readonly configured = isConfigured();
  protected readonly orderApi = environment.orderApi;
}

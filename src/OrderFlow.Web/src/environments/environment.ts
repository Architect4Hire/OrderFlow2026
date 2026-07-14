/**
 * The API URLs, read at run time from the config.js that scripts/write-config.mjs generates from
 * Aspire's environment variables.
 *
 * Deliberately NOT compile-time constants. Aspire assigns each API a different port on every run, so a
 * value baked into the bundle would be stale the moment the AppHost restarted. Reading it from a global
 * that index.html loads first means the same built bundle works against any environment — and it is how
 * the nginx image gets configured without rebuilding (docker-entrypoint.sh writes the same file).
 */
export interface OrderFlowConfig {
  orderApi: string;
  inventoryApi: string;
  paymentApi: string;
  fulfillmentApi: string;
}

declare global {
  interface Window {
    __ORDERFLOW__?: Partial<OrderFlowConfig>;
  }
}

const runtime: Partial<OrderFlowConfig> = globalThis.window?.__ORDERFLOW__ ?? {};

export const environment: OrderFlowConfig = {
  orderApi: runtime.orderApi ?? '',
  inventoryApi: runtime.inventoryApi ?? '',
  paymentApi: runtime.paymentApi ?? '',
  fulfillmentApi: runtime.fulfillmentApi ?? '',
};

/**
 * False when the app was served outside Aspire and has no idea where the APIs are. The shell renders a
 * banner rather than letting every call fail as an inscrutable network error.
 */
export const isConfigured = (): boolean => environment.orderApi.length > 0;

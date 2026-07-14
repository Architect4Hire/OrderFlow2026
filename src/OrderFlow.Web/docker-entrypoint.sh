#!/bin/sh
# Runs inside nginx:alpine before the server starts (it drops scripts in /docker-entrypoint.d).
#
# The nginx image has no Node, so it cannot run scripts/write-config.mjs — but it writes the identical
# file, and for the identical reason: the bundle is built once and must be pointable at any environment
# afterwards. Baking API URLs into the image at build time would mean a separate image per environment.
set -eu

cat >/usr/share/nginx/html/config.js <<EOF
// GENERATED at container start by docker-entrypoint.sh. Do not edit.
window.__ORDERFLOW__ = {
  "orderApi": "${ORDER_API_URL:-}",
  "inventoryApi": "${INVENTORY_API_URL:-}",
  "paymentApi": "${PAYMENT_API_URL:-}",
  "fulfillmentApi": "${FULFILLMENT_API_URL:-}"
};
EOF

echo "[orderflow] config.js written for order=${ORDER_API_URL:-<unset>}"

# Build the bundle, then serve it as static files. Two stages so nginx never ships a toolchain.

FROM node:22-alpine AS build
WORKDIR /app

# Copy the manifests alone first: this layer only invalidates when dependencies change, so day-to-day
# source edits reuse the cached npm ci instead of re-downloading the world.
COPY package.json package-lock.json* ./

# `npm ci` refuses to run without a lockfile, and this repo may not have one committed yet — fall back to
# `npm install` so a fresh clone still builds. In a real pipeline the lockfile is mandatory and this
# fallback should be deleted, because it is precisely what lets a transitive dependency drift between
# the build that was tested and the build that ships.
RUN npm ci --no-audit --no-fund || npm install --no-audit --no-fund

COPY . .

# postinstall already wrote a config.js; it is a build-time artifact with empty URLs and is deliberately
# NOT what the container serves. The entrypoint overwrites it at start-up (see docker-entrypoint.sh).
RUN npm run build

FROM nginx:alpine AS runtime

COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/orderflow-web/browser /usr/share/nginx/html
COPY docker-entrypoint.sh /docker-entrypoint.d/40-orderflow-config.sh

# nginx:alpine runs every executable script in /docker-entrypoint.d before starting the server, so the
# API URLs are injected at container start rather than baked into the image. The same image can therefore
# be pointed at a different environment by changing env vars — no rebuild.
RUN chmod +x /docker-entrypoint.d/40-orderflow-config.sh

EXPOSE 80

# syntax=docker/dockerfile:1
# AIDE web image (FR-103): the Next.js standalone output run by the custom server (server.ts), which serves the app,
# proxies /api (incl. SignalR WebSockets) to BACKEND_URL (read at runtime), sets the security headers and writes
# redacted JSON request logs. Non-root, PORT=8080, works with a read-only root filesystem (/tmp writable).
# Build context: frontend/   (docker build -f frontend/Dockerfile frontend)

FROM node:25-slim AS deps
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund

FROM node:25-slim AS build
WORKDIR /app
ENV CI=true NEXT_TELEMETRY_DISABLED=1
COPY --from=deps /app/node_modules ./node_modules
COPY . .
RUN npm run build

FROM node:25-slim AS runtime
WORKDIR /app
ENV NODE_ENV=production \
    PORT=8080 \
    NEXT_TELEMETRY_DISABLED=1
# Standalone server files (traced node_modules incl. next), static assets and public files.
COPY --from=build /app/.next/standalone ./
COPY --from=build /app/.next/static ./.next/static
COPY --from=build /app/public ./public
# The custom server runs with Node's type stripping; it needs its own sources, package.json ("type": "module") and zod.
COPY --from=build /app/package.json ./package.json
COPY --from=build /app/server.ts ./server.ts
COPY --from=build /app/src/lib/env.ts /app/src/lib/log-redaction.ts ./src/lib/
COPY --from=build /app/src/server/api-proxy.ts /app/src/server/request-log.ts /app/src/server/security-headers.ts ./src/server/
COPY --from=build /app/node_modules/zod ./node_modules/zod
# Read-only root FS: Next's runtime cache lives in /tmp (an emptyDir in Kubernetes).
RUN rm -rf .next/cache && ln -s /tmp .next/cache
USER node
EXPOSE 8080
CMD ["node", "server.ts"]

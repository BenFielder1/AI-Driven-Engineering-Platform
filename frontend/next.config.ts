import path from 'node:path'

import type { NextConfig } from 'next'

/**
 * AIDE web (FR-94, FR-103). The app is served by the custom server in server.ts (the /api proxy, security headers and
 * request logs live there); `output: 'standalone'` gives the minimal runtime that the Docker image copies.
 */
const root = path.resolve('.')

const nextConfig: NextConfig = {
  output: 'standalone',
  reactStrictMode: true,
  poweredByHeader: false,
  // The dev-mode indicator badge is not part of the v2 UI (and would show up in dev-loop screenshots and axe scans).
  devIndicators: false,
  outputFileTracingRoot: root,
  turbopack: { root },
  // FR-100: Next's dev request logging prints raw URLs (approval tokens); server.ts writes redacted JSON lines instead.
  logging: { incomingRequests: false },
}

export default nextConfig

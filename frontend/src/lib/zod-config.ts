import { z } from 'zod'

// FR-99: the CSP has no 'unsafe-eval'. Zod 4 probes `new Function('')` when it builds an object schema to decide whether
// to JIT, and the browser reports that blocked probe as a `securitypolicyviolation` even though Zod catches it. Jitless
// mode skips the probe. The browser gets this first from src/instrumentation-client.ts (before hydration); this import
// in the client providers keeps it on for Vitest and any other entry point.
z.config({ jitless: true })

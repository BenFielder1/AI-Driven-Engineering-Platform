import { z } from 'zod'

// FR-99: the CSP has no 'unsafe-eval'. Zod 4 probes `new Function('')` on first object parse to decide whether to JIT,
// and the browser reports that blocked probe as a `securitypolicyviolation` even though Zod catches it. Jitless mode
// skips the probe. Imported first by the client providers, before any schema parses.
z.config({ jitless: true })

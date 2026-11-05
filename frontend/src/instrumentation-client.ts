import { config } from 'zod/v4/core'

// FR-99: the CSP has no 'unsafe-eval'. Zod 4 decides whether to JIT when an object schema is *constructed* (module
// scope in most of our schema files), probing `new Function('')`; the blocked probe raises a `securitypolicyviolation`.
// Next.js runs this file before hydration, so before any page or layout module evaluates: unlike the import in
// providers.tsx, it can't lose a race with a chunk that defines a schema first. `zod/v4/core` keeps it out of the
// classic bundle (the setting lives in a global shared by every Zod copy).
config({ jitless: true })

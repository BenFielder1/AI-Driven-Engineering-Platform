Rules for Next.js App Router pages and Tailwind components on data from external APIs or mocks: rendering strategy,
forms, loading/error/empty states and accessibility. Read the specification's pages/UI section, Routes & Rendering
Strategy and the real data shapes first; never invent fields.

### Architecture
- **Server Components by default.** First render calls the server-only functions in `lib/api/` directly (never
  `fetch('/api/...')` from a Server Component).
- **Client components** (`"use client"`) only for interactivity and forms; leaf-level, with server data passed in as
  props. Client-side reads and writes go to the app's `/api` routes. Client files read only `NEXT_PUBLIC_` env vars
  and never import `lib/api/client.ts`.
- **Rendering per route** as the specification says: `export const revalidate = n`, `export const dynamic =
  'force-dynamic'`, or `generateStaticParams` for known paths. User-specific pages are dynamic.
- **Route files**: every data-driven route has `loading.tsx` and `error.tsx` (a client component with a reset button).
  Use `notFound()` for missing resources, and `not-found.tsx` for a helpful page.

### Styling
- Tailwind only, mobile-first: design at 360px, then `sm:`/`md:`/`lg:`.
- Tap targets at least 44px (`min-h-11`), no hover-only affordances, no horizontal page scroll.
- Shared primitives in `components/ui/` (Button, Input, Dialog/Sheet, Tabs, Toast, EmptyState), reused everywhere.
- `next/image` for images, with `alt` text and sizes; remote hosts listed in `next.config.ts`.
- Dark mode only if the specification asks for it, then with the `dark:` variant consistently.

### Forms
- Client validation with the same Zod schema as the BFF route, exported from `lib/`.
- Submit disabled while pending or invalid, field-level errors, user input kept when a submit fails.
- API error codes map to human copy in one table, `lib/ui/error-messages.ts`, never inline strings.

### Upstream failures
- An upstream error shows the mapped copy with a retry (`router.refresh()` or `reset()` in `error.tsx`), never a raw
  message or stack.
- Empty upstream results show an EmptyState with a next action.

### Permissions (only if the specification has roles or signed-in states)
- Pure `can*` helpers in `lib/ui/permissions.ts`; hide or disable what the user can't do and still handle 401/403/409.

### Data freshness
After a mutation, `router.refresh()` or re-query. Optimistic updates only for cheap, reversible actions, rolled back
with a toast on error.

### Accessibility
Semantic elements, a label on every input, `aria-label` on icon buttons, focus trapped in dialogs and sheets,
everything keyboard-reachable, colour contrast WCAG AA. Never render upstream HTML unsanitised.

### Tests
Testing Library tests for form validation, error and empty states (MSW handlers from `mocks/`) and non-trivial display
logic (sorting, grouping, formatting).

### Checklist
- [ ] Loading, empty, error and not-found states exist.
- [ ] Rendering mode matches the specification.
- [ ] Works at 360px with no horizontal scroll.
- [ ] Error codes map to copy.

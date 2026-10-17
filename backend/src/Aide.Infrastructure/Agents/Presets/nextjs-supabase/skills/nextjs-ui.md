Rules for Next.js App Router pages and Tailwind components: forms, permission-aware controls, loading/error/empty
states and Supabase Realtime. Read the specification's pages/UI section and the real API response shapes first;
never invent fields.

### Architecture
- **Server Components by default.** First render fetches on the server with the user-scoped Supabase client (or the
  same `lib/` functions the API uses).
- **Client components** (`"use client"`) only for interactivity, forms and Realtime; leaf-level, with server data
  passed in as props. They read only `NEXT_PUBLIC_` env vars and use `lib/supabase/client.ts`.
- **Writes** go through the app's `/api` routes (or Server Actions if the specification says so; never mix).
- **Route files**: every data-driven route has `loading.tsx` and `error.tsx` (a client component with a reset button).
  Use `notFound()` for missing or forbidden resources.
- **Auth-gated pages** redirect to sign-in with `?next=<path>` and return there afterwards.

### Styling
- Tailwind only, mobile-first: design at 360px, then `sm:`/`md:`/`lg:`.
- Tap targets at least 44px (`min-h-11`), no hover-only affordances, no horizontal page scroll.
- Shared primitives in `components/ui/` (Button, Input, Dialog/Sheet, Tabs, Toast, EmptyState), reused everywhere.
- Dark mode only if the specification asks for it, then with the `dark:` variant consistently.

### Forms
- Client validation with the same Zod schema as the API, exported from `lib/`.
- Submit disabled while pending or invalid, field-level errors, user input kept when a submit fails.
- API error codes map to human copy in one table, `lib/ui/error-messages.ts`, never inline strings.

### Permissions in the UI
- The specification's permission matrix becomes small pure helpers (`canEdit(user, thing)`) in `lib/ui/permissions.ts`.
- Hide or disable what the user can't do, and still handle 401/403/409 responses: the server is the real check.
- Read-only states (archived, ended, locked) come from one flag applied everywhere.

### Data freshness
- After a mutation, `router.refresh()` or re-query. Optimistic updates only for cheap, reversible actions, rolled back
  with a toast on error.
- **Realtime (if used):**
  ```ts
  useEffect(() => {
    const ch = supabase.channel(`scope:${id}`)
      .on('postgres_changes', { event: '*', schema: 'public', table: 't', filter: `parent_id=eq.${id}` }, refreshDebounced)
      .subscribe();
    return () => { void supabase.removeChannel(ch); };
  }, [id]);
  ```
  Debounce about 250 ms and refetch; don't patch state by hand. Also refetch on `visibilitychange` and `online`.

### Accessibility
Semantic elements, a label on every input, `aria-label` on icon buttons, focus trapped in dialogs and sheets,
everything keyboard-reachable, colour contrast WCAG AA.

### Tests
Testing Library tests for form validation, permission-dependent rendering and non-trivial display logic (sorting,
grouping, formatting).

### Checklist
- [ ] Loading, empty, error, forbidden and read-only states exist.
- [ ] Each role's view is handled.
- [ ] Works at 360px with no horizontal scroll.
- [ ] Error codes map to copy.
- [ ] Realtime subscriptions are removed on unmount (if used).

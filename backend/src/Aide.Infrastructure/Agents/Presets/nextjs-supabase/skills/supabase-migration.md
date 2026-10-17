Rules for Supabase tables, columns, views, SQL functions and RPCs, triggers, RLS policies, seed data and generated types.
The specification's Data Model & RLS Policies section is the target schema.

### Files
- One new migration per step: `supabase/migrations/<YYYYMMDDHHMMSS>_<snake_case_description>.sql`, with a timestamp
  later than every existing migration. Never edit an existing migration; add a new one.
- `supabase/seed.sql` runs after the migrations on `supabase db reset` (local only, never production).
- `lib/database.types.ts` is the output of `supabase gen types typescript --local`.

### Tables
- `id uuid primary key default gen_random_uuid()`, `created_at timestamptz not null default now()`, and `updated_at`
  (set by a `before update` trigger) where rows change.
- Every foreign key states `on delete` (`cascade` for owned children, `restrict` otherwise).
- Invariants in the database: `check` constraints, unique and partial unique indexes
  (`unique (org_id, lower(name))`, `where status = 'active'`), `generated always as (...) stored` derived columns.
- Postgres enums (`create type x as enum (...)`) for small, stable status sets.
- `alter table <t> enable row level security;` for every table, with no exceptions.
- Index every foreign key and every column used in an RLS predicate.

### Profiles
An `after insert` trigger on `auth.users` (`security definer set search_path = public`) creates a `profiles` row. It
must tolerate a null email and null metadata (OAuth and anonymous users).

### Membership helpers (avoid recursive RLS)
```sql
create or replace function is_member(p_parent uuid) returns boolean
language sql stable security definer set search_path = public as $$
  select exists (select 1 from memberships where parent_id = p_parent and user_id = auth.uid())
$$;
```
Policies call helpers like this instead of querying the same table inside its own policy.

### Policies
- One policy per operation and role, named for what it allows (`"members can read boards"`).
- `using` for rows a role may see or target; `with check` for rows it may write.
- Update policies restrict which columns change; use an RPC or column grants where a policy can't.
- Tables written only through RPCs have no insert/update/delete policies.

### Security definer RPCs
For multi-row or privileged writes RLS can't express safely (create-with-owner, join-by-code, state transitions,
transfers):
- `language plpgsql security definer set search_path = public`.
- The first statement rejects anonymous callers where appropriate:
  `if auth.uid() is null then raise exception 'NOT_AUTHENTICATED' using errcode = 'P0001'; end if;`
- **Re-check every permission inside**: definer rights bypass RLS.
- Raise the specification's error codes as the message: `raise exception 'NOT_OWNER' using errcode = 'P0001';`.
- `revoke execute on function f(...) from public; grant execute on function f(...) to authenticated;`

### Anonymous (guest) auth, if the specification uses it
Anonymous users have the `authenticated` role with `(auth.jwt() ->> 'is_anonymous')::boolean = true`. Add
`is_anonymous` checks only where the specification treats guests differently, and enable
`[auth] enable_anonymous_sign_ins = true` in `supabase/config.toml`.

### Realtime, if used
`alter publication supabase_realtime add table <t>;` only for tables the UI subscribes to. Realtime respects the
select policies.

### Seed
A user for every role in the specification (owner, member, non-member, guest...) inserted into `auth.users` with fixed
UUIDs and known emails, plus data in every important state (empty, active, archived...).

### RLS checklist
- [ ] Every table has RLS enabled and at least one select policy, or is deliberately private.
- [ ] Non-members can't read or write other groups' rows.
- [ ] Update policies restrict which columns change.
- [ ] RPC-only tables have no direct write policies.
- [ ] Every security definer function re-checks permissions and sets `search_path`.
- [ ] State rules (archived/ended is read-only) are enforced in the database.
- [ ] Realtime tables are in the publication with correct select policies.

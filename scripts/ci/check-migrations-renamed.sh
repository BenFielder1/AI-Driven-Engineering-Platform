#!/usr/bin/env bash
# FR-324, D-0.8.0-9 (0.8.0): the Aide → Tevrin rename may change a committed migration (or the model snapshot) only by
# the mechanical substitutions: the namespaces and CLR type names Aide.Api/Aide.Domain/Aide.Infrastructure and the
# context class AideDbContext (AideDbContextModelSnapshot). For every file under the migrations folder at the pre-rename
# base, this reverses those substitutions in the current file and requires the result to equal the base file byte for
# byte (only CRLF is normalised). Tevrin.Engine.* and Tevrin.Pack.* are never touched: migrations reference them legitimately.
# Usage: scripts/ci/check-migrations-renamed.sh   (run from the repo root; fetches the base commit if it's missing)
#        MIGRATIONS_BASE=<sha> overrides the base (used by check-migrations-renamed.test.sh).
set -euo pipefail

base="${MIGRATIONS_BASE:-9a79474259e6aa1370ebc0fbc93b627e0b831cff}"
old_dir=backend/src/Aide.Infrastructure/Migrations
new_dir=backend/src/Tevrin.Infrastructure/Migrations

if ! git cat-file -e "${base}^{commit}" 2>/dev/null; then
  git fetch --quiet --depth=1 origin "$base"
fi

# The reverse substitutions, as whole identifiers: Tevrin.Api|Domain|Infrastructure (so never Tevrin.Engine.Api or
# Tevrin.Pack.*), then the context and snapshot class names.
reverse() {
  sed -E \
    -e 's/(^|[^A-Za-z0-9_.])Tevrin\.(Api|Domain|Infrastructure)([^A-Za-z0-9_]|$)/\1Aide.\2\3/g' \
    -e 's/(^|[^A-Za-z0-9_.])Tevrin\.(Api|Domain|Infrastructure)([^A-Za-z0-9_]|$)/\1Aide.\2\3/g' \
    -e 's/(^|[^A-Za-z0-9_])TevrinDbContext(ModelSnapshot)?([^A-Za-z0-9_]|$)/\1AideDbContext\2\3/g' \
    -e 's/(^|[^A-Za-z0-9_])TevrinDbContext(ModelSnapshot)?([^A-Za-z0-9_]|$)/\1AideDbContext\2\3/g'
}

work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT

mapfile -t files < <(git ls-tree -r --name-only "$base" -- "$old_dir/")
if [ "${#files[@]}" -eq 0 ]; then
  echo "::error::No migrations under $old_dir at $base." >&2
  exit 1
fi

failures=0
for old in "${files[@]}"; do
  name="${old#"$old_dir"/}"
  new="$new_dir/${name/AideDbContextModelSnapshot/TevrinDbContextModelSnapshot}"
  if [ ! -f "$new" ]; then
    echo "::error file=$new::Missing: $old (at $base) has no renamed file $new." >&2
    failures=$((failures + 1))
    continue
  fi
  git show "$base:$old" | tr -d '\r' > "$work/expected"
  tr -d '\r' < "$new" | reverse > "$work/actual"
  if ! cmp -s "$work/expected" "$work/actual"; then
    echo "::error file=$new::$new differs from $old at $base in more than the rename substitutions (FR-324):" >&2
    diff "$work/expected" "$work/actual" | head -20 >&2 || true
    failures=$((failures + 1))
  fi
done

if [ "$failures" -ne 0 ]; then
  echo "$failures migration file(s) changed beyond the rename (FR-324, D-0.8.0-9)." >&2
  exit 1
fi
echo "All ${#files[@]} committed migration files match $base apart from the rename substitutions (FR-324)"

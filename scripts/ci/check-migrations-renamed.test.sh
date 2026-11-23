#!/usr/bin/env bash
# FR-324: tests for scripts/ci/check-migrations-renamed.sh against a throwaway Git repo with a pre-rename base commit.
# Usage: bash scripts/ci/check-migrations-renamed.test.sh (from the repo root; CI runs it in the `scripts` job).
set -euo pipefail

script="$(cd "$(dirname "$0")" && pwd)/check-migrations-renamed.sh"
work="$(mktemp -d)"
trap 'rm -rf -- "$work"' EXIT
failures=0

old=backend/src/Aide.Infrastructure/Migrations
new=backend/src/Tevrin.Infrastructure/Migrations

# The base: one migration and the model snapshot, as the pre-rename files look.
repo="$work/repo"
mkdir -p "$repo/$old"
cat > "$repo/$old/20260101000000_Init.cs" <<'EOF'
using Aide.Infrastructure.Persistence;
using Tevrin.Engine.Domain.Entities;
namespace Aide.Infrastructure.Migrations;
[DbContext(typeof(AideDbContext))]
public partial class Init { void Up() { b.Entity("Aide.Domain.Entities.Project", e => e.ToTable("projects")); b.Entity("Tevrin.Engine.Domain.Entities.PipelineRun"); } }
EOF
cat > "$repo/$old/AideDbContextModelSnapshot.cs" <<'EOF'
namespace Aide.Infrastructure.Migrations;
partial class AideDbContextModelSnapshot { string a = "Aide.Api.Jobs.Job"; string b = "Tevrin.Pack.Sdlc"; }
EOF
git -C "$repo" init --quiet
git -C "$repo" -c core.autocrlf=false add -A
git -C "$repo" -c user.email=ci@example.invalid -c user.name=ci commit --quiet -m base
base="$(git -C "$repo" rev-parse HEAD)"

# renamed: writes the correctly renamed tree (old folder gone, new folder with the substitutions applied).
renamed() {
  rm -rf -- "$repo/backend/src"
  mkdir -p "$repo/$new"
  git -C "$repo" show "$base:$old/20260101000000_Init.cs" \
    | sed -e 's/Aide\.Infrastructure/Tevrin.Infrastructure/g; s/Aide\.Domain/Tevrin.Domain/g; s/AideDbContext/TevrinDbContext/g' \
    > "$repo/$new/20260101000000_Init.cs"
  git -C "$repo" show "$base:$old/AideDbContextModelSnapshot.cs" \
    | sed -e 's/Aide\.Infrastructure/Tevrin.Infrastructure/g; s/Aide\.Api/Tevrin.Api/g; s/AideDbContext/TevrinDbContext/g' \
    > "$repo/$new/TevrinDbContextModelSnapshot.cs"
}

# expect pass|fail NAME
expect() {
  local want="$1" name="$2" got
  if (cd "$repo" && MIGRATIONS_BASE="$base" bash "$script") >/dev/null 2>&1; then got=pass; else got=fail; fi
  if [ "$got" = "$want" ]; then
    echo "ok    $name"
  else
    echo "FAIL  $name (expected $want, got $got)"
    failures=$((failures + 1))
  fi
}

renamed
expect pass FR324_rename_substitutions_only

renamed
sed -i 's/$/\r/' "$repo/$new/20260101000000_Init.cs"
expect pass FR324_crlf_line_endings_are_normalised

renamed
sed -i 's/"projects"/"project"/' "$repo/$new/20260101000000_Init.cs"
expect fail FR324_up_body_changed

renamed
sed -i 's/Tevrin\.Engine\.Domain\.Entities\.PipelineRun/Tevrin.Engine.Domain.Entities.Run/' "$repo/$new/20260101000000_Init.cs"
expect fail FR324_engine_type_name_changed

renamed
sed -i 's/"Tevrin\.Pack\.Sdlc"/"Aide.Pack.Sdlc"/' "$repo/$new/TevrinDbContextModelSnapshot.cs"
expect fail FR324_pack_name_is_never_reversed

renamed
sed -i 's/Init {/Initial {/' "$repo/$new/20260101000000_Init.cs"
expect fail FR324_migration_class_renamed

renamed
rm "$repo/$new/TevrinDbContextModelSnapshot.cs"
expect fail FR324_snapshot_missing

renamed
mv "$repo/$new/20260101000000_Init.cs" "$repo/$new/20260101000001_Init.cs"
expect fail FR324_migration_id_changed

[ "$failures" = 0 ] || { echo "$failures check-migrations-renamed test(s) failed"; exit 1; }
echo "check-migrations-renamed: all tests passed"

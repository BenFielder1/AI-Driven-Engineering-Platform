using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aide.Infrastructure.Migrations
{
    /// <summary>
    /// FR-129, §4.23: converts the release versions pinned to a retired v2 preset (<c>nextjs-supabase</c>,
    /// <c>nextjs-frontend</c>) into the equivalent stack composition. Data only: <c>preset_id</c>, <c>preset_version</c> and
    /// <c>preset_hash</c> stay as history. The compositions are pinned to catalog <c>1.1.0</c> with <c>catalog_hash</c> NULL
    /// (in the column and in the document): the app re-pins the hash on the version's next stack write. Converted rows are
    /// recognised by preset id + catalog <c>1.1.0</c> + NULL hash (the app never pins a NULL hash), which is what Down undoes.
    /// The four documents are exactly what <c>StackCompositionJson.Serialize</c> writes for the composition (checked by
    /// <c>PresetMigrationCompositionTests</c> against the shipped catalog).
    /// </summary>
    public partial class RetirePresets : Migration
    {
        /// <summary>The catalog version the converted compositions are pinned to.</summary>
        public const string CatalogVersion = "1.1.0";

        /// <summary><c>nextjs-supabase</c> on a project with Vercel deployment settings.</summary>
        public const string SupabaseVercelJson =
            """{"catalogVersion":"1.1.0","catalogHash":null,"frontend":{"pack":"fe-nextjs","path":"."},"backend":null,"data":{"pack":"data-supabase","mode":"baas"},"vcs":{"pack":"vcs-github","instanceId":null,"selfManaged":false},"deploy":{"frontendHost":"deploy-vercel","backendHost":null,"databaseHost":"supabase","local":"none"},"originModel":"same-origin","derived":{"nextjsMode":"full-stack","agents":["scaffolder","data-engineer","frontend-engineer","test-engineer","ci-engineer","deploy-engineer","docs-writer"],"originModelOptions":["same-origin"],"specSections":["Routes \u0026 Rendering Strategy","Data Model \u0026 RLS Policies","Auth Flows","Environments \u0026 Configuration"],"envNames":["NEXT_PUBLIC_SUPABASE_URL","NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY","SUPABASE_URL","SUPABASE_PUBLISHABLE_KEY","SUPABASE_SECRET_KEY"],"gates":["cd {fe} \u0026\u0026 npm ci \u0026\u0026 npm run typecheck \u0026\u0026 npm run lint \u0026\u0026 npm test \u0026\u0026 npm run build"]},"caveats":[]}""";

        /// <summary><c>nextjs-supabase</c> on a project without Vercel deployment settings.</summary>
        public const string SupabaseJson =
            """{"catalogVersion":"1.1.0","catalogHash":null,"frontend":{"pack":"fe-nextjs","path":"."},"backend":null,"data":{"pack":"data-supabase","mode":"baas"},"vcs":{"pack":"vcs-github","instanceId":null,"selfManaged":false},"deploy":{"frontendHost":null,"backendHost":null,"databaseHost":"supabase","local":"none"},"originModel":"same-origin","derived":{"nextjsMode":"full-stack","agents":["scaffolder","data-engineer","frontend-engineer","test-engineer","ci-engineer","deploy-engineer","docs-writer"],"originModelOptions":["same-origin"],"specSections":["Routes \u0026 Rendering Strategy","Data Model \u0026 RLS Policies","Auth Flows"],"envNames":["NEXT_PUBLIC_SUPABASE_URL","NEXT_PUBLIC_SUPABASE_PUBLISHABLE_KEY","SUPABASE_URL","SUPABASE_PUBLISHABLE_KEY","SUPABASE_SECRET_KEY"],"gates":["cd {fe} \u0026\u0026 npm ci \u0026\u0026 npm run typecheck \u0026\u0026 npm run lint \u0026\u0026 npm test \u0026\u0026 npm run build"]},"caveats":[{"layer":"databaseHost","option":"supabase","rule":"R11","text":"Database only used by the local dev loop"}]}""";

        /// <summary><c>nextjs-frontend</c> on a project with Vercel deployment settings.</summary>
        public const string FrontendVercelJson =
            """{"catalogVersion":"1.1.0","catalogHash":null,"frontend":{"pack":"fe-nextjs","path":"."},"backend":null,"data":null,"vcs":{"pack":"vcs-github","instanceId":null,"selfManaged":false},"deploy":{"frontendHost":"deploy-vercel","backendHost":null,"databaseHost":null,"local":"none"},"originModel":"same-origin","derived":{"nextjsMode":"full-stack","agents":["scaffolder","frontend-engineer","test-engineer","ci-engineer","deploy-engineer","docs-writer"],"originModelOptions":["same-origin"],"specSections":["Routes \u0026 Rendering Strategy","Data Sources \u0026 Consumed APIs","Environments \u0026 Configuration"],"envNames":[],"gates":["cd {fe} \u0026\u0026 npm ci \u0026\u0026 npm run typecheck \u0026\u0026 npm run lint \u0026\u0026 npm test \u0026\u0026 npm run build"]},"caveats":[]}""";

        /// <summary><c>nextjs-frontend</c> on a project without Vercel deployment settings.</summary>
        public const string FrontendJson =
            """{"catalogVersion":"1.1.0","catalogHash":null,"frontend":{"pack":"fe-nextjs","path":"."},"backend":null,"data":null,"vcs":{"pack":"vcs-github","instanceId":null,"selfManaged":false},"deploy":{"frontendHost":null,"backendHost":null,"databaseHost":null,"local":"none"},"originModel":"same-origin","derived":{"nextjsMode":"full-stack","agents":["scaffolder","frontend-engineer","test-engineer","ci-engineer","docs-writer"],"originModelOptions":["same-origin"],"specSections":["Routes \u0026 Rendering Strategy","Data Sources \u0026 Consumed APIs"],"envNames":[],"gates":["cd {fe} \u0026\u0026 npm ci \u0026\u0026 npm run typecheck \u0026\u0026 npm run lint \u0026\u0026 npm test \u0026\u0026 npm run build"]},"caveats":[]}""";

        /// <summary><c>projects.stack_packs</c> (<c>StackPinning.PackIds</c>) of each document, as SQL array literals.</summary>
        public const string SupabaseVercelPacks = "{fe-nextjs,data-supabase,vcs-github,deploy-vercel}";

        public const string SupabasePacks = "{fe-nextjs,data-supabase,vcs-github}";

        public const string FrontendVercelPacks = "{fe-nextjs,vcs-github,deploy-vercel}";

        public const string FrontendPacks = "{fe-nextjs,vcs-github}";

        /// <summary>A release version this migration converted (alias <c>v</c>).</summary>
        private const string Converted =
            "v.preset_id IN ('nextjs-supabase', 'nextjs-frontend') AND v.stack_composition IS NOT NULL "
            + "AND v.catalog_version = '" + CatalogVersion + "' AND v.catalog_hash IS NULL";

        /// <summary>The project's current release version (alias <c>p</c>), as <c>CurrentReleaseVersionAsync</c> reads it.</summary>
        private const string CurrentVersionOfProject =
            "COALESCE(p.current_release_version_id, (SELECT l.id FROM release_versions AS l WHERE l.project_id = p.id "
            + "ORDER BY l.created_at DESC, l.id DESC LIMIT 1))";

        /// <summary>
        /// 1. The compositions: VCS is <c>vcs-github</c> for every Git provider (github_app, github_pat, local, or none);
        /// the frontend host is Vercel only when the project has Vercel deployment settings.
        /// </summary>
        public const string ConvertVersionsSql =
            $$"""
            UPDATE release_versions AS v
            SET stack_composition = (CASE
                    WHEN v.preset_id = 'nextjs-supabase' AND x.vercel THEN '{{SupabaseVercelJson}}'
                    WHEN v.preset_id = 'nextjs-supabase' THEN '{{SupabaseJson}}'
                    WHEN x.vercel THEN '{{FrontendVercelJson}}'
                    ELSE '{{FrontendJson}}'
                END)::jsonb,
                catalog_version = '{{CatalogVersion}}',
                catalog_hash = NULL
            FROM (
                SELECT p.id AS project_id,
                       EXISTS (SELECT 1 FROM project_deployment_settings AS d
                               WHERE d.project_id = p.id AND d.provider = 'vercel') AS vercel
                FROM projects AS p) AS x
            WHERE x.project_id = v.project_id
              AND v.preset_id IN ('nextjs-supabase', 'nextjs-frontend')
              AND v.stack_composition IS NULL;
            """;

        /// <summary>2. <c>projects.stack_packs</c> of projects whose current version was converted.</summary>
        public const string SyncPacksSql =
            $$"""
            UPDATE projects AS p
            SET stack_packs = (CASE
                    WHEN v.preset_id = 'nextjs-supabase' AND v.stack_composition -> 'deploy' ->> 'frontendHost' = 'deploy-vercel' THEN '{{SupabaseVercelPacks}}'
                    WHEN v.preset_id = 'nextjs-supabase' THEN '{{SupabasePacks}}'
                    WHEN v.stack_composition -> 'deploy' ->> 'frontendHost' = 'deploy-vercel' THEN '{{FrontendVercelPacks}}'
                    ELSE '{{FrontendPacks}}'
                END)::text[]
            FROM release_versions AS v
            WHERE v.id = {{CurrentVersionOfProject}}
              AND {{Converted}};
            """;

        /// <summary>
        /// 3. §4.23 agent keys of the steps of open (in development, awaiting merge) converted versions: the v2 key moves to
        /// <c>legacy_agent_key</c>. <c>test-engineer</c> is the same key in v4, so it is left as is (no legacy key).
        /// <c>devops-engineer</c> runs as <c>ci-engineer</c> when it declares files and every one (normalised as
        /// <c>PathRules.Normalize</c>) is under <c>.github/</c>, else <c>deploy-engineer</c>.
        /// </summary>
        public const string MapAgentKeysSql =
            $$"""
            UPDATE execution_steps AS s
            SET legacy_agent_key = s.agent_key,
                agent_key = CASE s.agent_key
                    WHEN 'project-scaffolder' THEN 'scaffolder'
                    WHEN 'db-engineer' THEN 'data-engineer'
                    WHEN 'domain-logic-engineer' THEN 'frontend-engineer'
                    WHEN 'api-engineer' THEN 'frontend-engineer'
                    WHEN 'ui-engineer' THEN 'frontend-engineer'
                    WHEN 'devops-engineer' THEN CASE
                        WHEN jsonb_typeof(s.declared_files) = 'array' AND jsonb_array_length(s.declared_files) > 0
                             AND NOT EXISTS (
                                 SELECT 1 FROM jsonb_array_elements(s.declared_files) AS f(file)
                                 WHERE NOT starts_with(
                                     rtrim(regexp_replace(regexp_replace(replace(btrim(COALESCE(f.file ->> 'path', '')), '\', '/'),
                                         '/{2,}', '/', 'g'), '^(\./)+', ''), '/'),
                                     '.github/'))
                        THEN 'ci-engineer'
                        ELSE 'deploy-engineer'
                    END
                END
            FROM release_versions AS v
            WHERE v.id = s.release_version_id
              AND {{Converted}}
              AND v.status IN ('in_development', 'awaiting_merge')
              AND s.legacy_agent_key IS NULL
              AND s.agent_key IN ('project-scaffolder', 'db-engineer', 'domain-logic-engineer', 'api-engineer', 'ui-engineer', 'devops-engineer');
            """;

        /// <summary>Down: restores the v2 agent keys, empties the packs and removes the compositions this migration wrote.</summary>
        public const string RevertSql =
            $$"""
            UPDATE execution_steps AS s
            SET agent_key = s.legacy_agent_key,
                legacy_agent_key = NULL
            FROM release_versions AS v
            WHERE v.id = s.release_version_id
              AND {{Converted}}
              AND s.legacy_agent_key IN ('project-scaffolder', 'db-engineer', 'domain-logic-engineer', 'api-engineer', 'ui-engineer', 'devops-engineer');

            UPDATE projects AS p
            SET stack_packs = '{}'::text[]
            FROM release_versions AS v
            WHERE v.id = {{CurrentVersionOfProject}}
              AND {{Converted}};

            UPDATE release_versions AS v
            SET stack_composition = NULL,
                catalog_version = NULL
            WHERE {{Converted}};
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(ConvertVersionsSql);
            migrationBuilder.Sql(SyncPacksSql);
            migrationBuilder.Sql(MapAgentKeysSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(RevertSql);
        }
    }
}

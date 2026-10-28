namespace Aide.Domain.Stack;

/// <summary>
/// §4.16, §4.17: the choices of the Stack step, in evaluation order (Frontend → Backend → Data → VCS → Deployment, the
/// Deployment layer being the frontend, backend and database hosts, the local dev loop and the origin model). A rule on a
/// choice may only depend on earlier choices, so a later choice never makes an earlier one invalid: it becomes
/// <see cref="StackOptionState.Invalidated"/> instead.
/// </summary>
public static class StackChoices
{
    public const string Frontend = "frontend";
    public const string Backend = "backend";
    public const string Data = "data";
    public const string Vcs = "vcs";
    public const string FrontendHost = "frontendHost";
    public const string BackendHost = "backendHost";
    public const string DatabaseHost = "databaseHost";
    public const string Local = "local";
    public const string OriginModel = "originModel";

    /// <summary>Every choice, in evaluation order (catalog.json lists them in this order).</summary>
    public static IReadOnlyList<string> Order { get; } =
        [Frontend, Backend, Data, Vcs, FrontendHost, BackendHost, DatabaseHost, Local, OriginModel];

    /// <summary>The choices that must offer <see cref="StackIds.None"/>.</summary>
    public static IReadOnlyList<string> WithNone { get; } = [Frontend, Backend, Data, FrontendHost, BackendHost, DatabaseHost, Local];

    /// <summary>The catalog layer each choice belongs to.</summary>
    public static string LayerOf(string choice) => choice switch
    {
        Frontend => StackLayers.Frontend,
        Backend => StackLayers.Backend,
        Data => StackLayers.Data,
        Vcs => StackLayers.Vcs,
        _ => StackLayers.Deploy,
    };

    /// <summary>The position of <paramref name="choice"/> in <see cref="Order"/>, or -1.</summary>
    public static int IndexOf(string choice)
    {
        for (var i = 0; i < Order.Count; i++)
        {
            if (Order[i] == choice)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>
/// §4.17: the facts rule conditions read. Every choice is a fact (its option id), plus two derived ones:
/// <see cref="VcsHost"/> (<c>github</c>, <c>gitlab</c>, <c>gitlab-self-managed</c>, <c>bitbucket</c>, from the VCS option)
/// and <see cref="NextjsMode"/> (R2: <c>full-stack</c>, <c>ui</c>, or <c>none</c> when the frontend isn't Next.js).
/// </summary>
public static class StackFacts
{
    public const string VcsHost = "vcsHost";
    public const string NextjsMode = "nextjsMode";

    /// <summary>The choice position after which a fact is known (a rule on choice <c>c</c> may use facts with a lower position).</summary>
    public static int PositionOf(string fact) => fact switch
    {
        VcsHost => StackChoices.IndexOf(StackChoices.Vcs),
        NextjsMode => StackChoices.IndexOf(StackChoices.Backend),
        _ => StackChoices.IndexOf(fact),
    };

    /// <summary>Whether <paramref name="fact"/> is a known fact (a choice other than the origin model, or a derived fact).</summary>
    public static bool IsKnown(string fact) =>
        fact is VcsHost or NextjsMode || (StackChoices.IndexOf(fact) >= 0 && fact != StackChoices.OriginModel);

    /// <summary>R2: the values of <see cref="NextjsMode"/>.</summary>
    public static IReadOnlyList<string> NextjsModes { get; } = [StackIds.FullStack, StackIds.Ui, StackIds.None];
}

/// <summary>Pack layers (§4.16 <c>pack.json</c> <c>layer</c>) and catalog layers (the five Stack sub-steps).</summary>
public static class StackLayers
{
    public const string Frontend = "frontend";
    public const string Backend = "backend";
    public const string Data = "data";
    public const string Vcs = "vcs";
    public const string Deploy = "deploy";
    public const string Local = "local";

    /// <summary>The five catalog layers, in wizard order.</summary>
    public static IReadOnlyList<string> CatalogLayers { get; } = [Frontend, Backend, Data, Vcs, Deploy];

    /// <summary>Every pack layer.</summary>
    public static IReadOnlyList<string> PackLayers { get; } = [Frontend, Backend, Data, Vcs, Deploy, Local];
}

/// <summary>Well-known option ids and values.</summary>
public static class StackIds
{
    /// <summary>The "None" option of a choice.</summary>
    public const string None = "none";

    /// <summary>R2: the Next.js frontend pack, whose mode is derived.</summary>
    public const string NextJs = "fe-nextjs";

    /// <summary>R2: Next.js without a backend.</summary>
    public const string FullStack = "full-stack";

    /// <summary>R2: Next.js as the UI of a backend.</summary>
    public const string Ui = "ui";

    /// <summary>The Supabase data pack: its <c>host</c> mode applies when it is the database host of another data pack.</summary>
    public const string Supabase = "data-supabase";

    /// <summary>The Supabase pack's host mode (§4.18).</summary>
    public const string HostMode = "host";

    /// <summary>The Supabase pack's BaaS mode: Supabase in the Data layer (§4.17 R3).</summary>
    public const string BaasMode = "baas";

    /// <summary>The Supabase database host option (§4.17 R8).</summary>
    public const string SupabaseHost = "supabase";

    /// <summary>The Compose dev loop (<c>local-docker-desktop</c>).</summary>
    public const string Compose = "compose";
}

/// <summary>FR-120: the generic specialists (adapted from the stack-core agents), in catalogue order.</summary>
public static class StackAgents
{
    public const string Scaffolder = "scaffolder";
    public const string DataEngineer = "data-engineer";
    public const string BackendEngineer = "backend-engineer";
    public const string FrontendEngineer = "frontend-engineer";
    public const string TestEngineer = "test-engineer";
    public const string CiEngineer = "ci-engineer";
    public const string DeployEngineer = "deploy-engineer";
    public const string DocsWriter = "docs-writer";

    /// <summary>Every specialist key a composition can bring into play (the <c>generalist</c> is for "No stack" only).</summary>
    public static IReadOnlyList<string> All { get; } =
        [Scaffolder, DataEngineer, BackendEngineer, FrontendEngineer, TestEngineer, CiEngineer, DeployEngineer, DocsWriter];
}

/// <summary>§4.16 vocabularies of <c>pack.json</c> fields.</summary>
public static class PackVocabulary
{
    public static IReadOnlyList<string> Runtimes { get; } = ["node", "python", "dotnet", "java", "static"];

    public static IReadOnlyList<string> DefaultPaths { get; } = ["frontend", "backend", "."];

    public static IReadOnlyList<string> PublicEnvPrefixes { get; } = ["NEXT_PUBLIC_", "VITE_"];

    public static IReadOnlyList<string> GeneratedFileHandling { get; } = ["hand_written", "ci_checked", "ci_generated"];

    public static IReadOnlyList<string> EnvTargets { get; } = ["production", "preview", "development"];

    public static IReadOnlyList<string> Components { get; } = ["frontend", "backend", "database"];

    /// <summary>§4.18 deterministic review rule ids that packs may register.</summary>
    public static IReadOnlyList<string> DeterministicRules { get; } =
    [
        "rls_on_every_table", "grants_for_exposed_tables", "no_secret_in_client", "no_committed_secrets", "no_ensure_created",
        "no_ddl_auto_update", "no_create_all", "no_schema_push", "migration_backward_compatible", "container_non_root",
        "deploy_branch_control", "health_endpoints",
    ];

    /// <summary>§4.18: the layer-path placeholders of pack globs (<c>{app}</c> is the backend path, else the frontend path).</summary>
    public static IReadOnlyList<string> GlobPlaceholders { get; } = ["{fe}", "{be}", "{app}"];

    /// <summary>§4.16: the section no agent loads (AIDE's own rules replace it).</summary>
    public const string AgentAutonomySection = "Agent autonomy";

    /// <summary>The whole adapted skill, in <c>agentSections</c>.</summary>
    public const string WholeSkill = "*";
}

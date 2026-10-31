namespace Aide.Cli.Tests;

/// <summary>API response bodies shaped like backend/openapi/api.json.</summary>
internal static class Samples
{
    public const string ProjectId = "0199f2a0-1111-7000-8000-000000000001";
    public const string ArtifactId = "0199f2a0-2222-7000-8000-000000000002";
    public const string GateId = "0199f2a0-3333-7000-8000-000000000003";

    public const string User = """
        {"id":"0199f2a0-0000-7000-8000-000000000000","email":"ada@example.com","name":"Ada","systemRole":"admin","avatarUrl":null,"githubLinked":false}
        """;

    public const string State = """
        {"projectId":"0199f2a0-1111-7000-8000-000000000001","phase":"execution","status":"building","checkpoint":4,"releasedVersion":null,"developmentVersion":"0.1.0","executionPaused":false,
         "artifacts":[{"type":"technical_specification","latestVersion":2,"latestStatus":"locked","currentLockedVersion":2}],
         "execution":{"total":5,"pending":2,"inProgress":1,"completed":2,"rejected":0,"blocked":0},
         "review":{"cycle":0,"maxCycles":3,"latestScore":null,"latestDecision":null,"driftLevel":null,"status":"idle"},
         "pendingGates":[],"tokensUsed":1200,"tokenBudget":null,"executionStatus":"running","pullRequest":null,"latestRelease":null}
        """;

    public const string Project = """
        {"id":"0199f2a0-1111-7000-8000-000000000001","name":"Todo app","slug":"todo-app","idea":"A todo app","currentPhase":"refinement",
         "status":"active","checkpoint":1,"releasedVersion":null,"developmentVersion":"0.1.0","approvalMode":"advisory","complianceThreshold":0.8,"maxFilesPerStep":10,"maxReviewCycles":3,
         "tokenBudget":null,"tokensUsed":0,"executionPaused":false,"myRole":"owner","createdAt":"2026-10-10T10:00:00Z","updatedAt":"2026-10-10T10:00:00Z"}
        """;

    public const string EmptyStack = """
        {"releaseVersionId":"0199f2a0-6666-7000-8000-000000000001","version":"0.1.0","versionStatus":"in_development","composition":null,
         "catalogVersion":null,"catalogHash":null,"currentCatalogVersion":"2026.10.1","currentCatalogHash":"h1","locked":false,"lockedReason":null,
         "stackChange":{"baseHasCode":false,"layersChanged":[],"isStackChange":false},"legacyPreset":null,"repoVcs":null}
        """;

    /// <summary>The body `aide status` gets for each request: the state, the project, no stack and no migration runs (FR-156).</summary>
    public static string StatusBody(HttpRequestMessage request, string state, string project)
    {
        var path = request.RequestUri!.AbsolutePath;
        return path.EndsWith("/state", StringComparison.Ordinal) ? state
            : path.EndsWith("/stack", StringComparison.Ordinal) ? EmptyStack
            : path.EndsWith("/migrations", StringComparison.Ordinal) ? "[]"
            : project;
    }

    public const string ProjectsPage = """
        {"items":[{"id":"0199f2a0-1111-7000-8000-000000000001","name":"Todo app","slug":"todo-app","currentPhase":"review","status":"reviewing",
          "checkpoint":6,"releasedVersion":"1.1.0","developmentVersion":"1.2.0","approvalMode":"strict","myRole":"owner","createdAt":"2026-10-10T10:00:00Z","updatedAt":"2026-10-10T11:00:00Z"}],
         "page":2,"limit":10,"total":11}
        """;

    public static string Artifact(int version, string status = "draft", string content = "# Spec\n\nv1 content\n") => $$"""
        {"id":"{{ArtifactId}}","type":"technical_specification","version":{{version}},"parentVersion":null,"content":{{System.Text.Json.JsonSerializer.Serialize(content)}},
         "contentHash":"abc123","status":"{{status}}","checkpointAtCreation":2,"author":{"user":{"id":null,"name":"Ada","isSystem":false},"agentRunId":null},
         "approvedBy":null,"approvedAt":null,"rejectionComment":null,"createdAt":"2026-10-10T10:00:00Z","integrity":"ok"}
        """;

    public const string Activity = """
        {"items":[
          {"id":"0199f2a0-4444-7000-8000-000000000002","type":"GATE_APPROVED","category":"gate","occurredAt":"2026-10-10T10:05:00Z","actor":{"id":null,"name":"Ada","isSystem":false},"summary":"second event","payload":{}},
          {"id":"0199f2a0-4444-7000-8000-000000000001","type":"PHASE_TRANSITIONED","category":"transition","occurredAt":"2026-10-10T10:00:00Z","actor":{"id":null,"name":"aide-system","isSystem":true},"summary":"first event","payload":{}}
         ],"nextBefore":null}
        """;

    public const string PendingPreReleaseGate = """
        [{"id":"0199f2a0-3333-7000-8000-000000000003","type":"pre_release","status":"pending","artifactId":null,"artifactType":null,"artifactVersion":null,
          "escalationReason":null,"reason":null,"createdAt":"2026-10-10T10:00:00Z","decidedAt":null,"decidedBy":null,"comment":null,"decision":null,
          "targetCheckpoint":null,"overdue":false,"remindedAt":null,"executionStepId":null,"stepNumber":null}]
        """;

    public const string ApprovedGate = """
        {"id":"0199f2a0-3333-7000-8000-000000000003","type":"pre_release","status":"approved","createdAt":"2026-10-10T10:00:00Z"}
        """;

    public const string Release = """
        {"id":"0199f2a0-5555-7000-8000-000000000001","checkpoint":6,"status":"in_progress","tag":"v6-aide","mergeCommitSha":null,"pullRequestUrl":null,
         "conflictingFiles":[],"errorCode":null,"errorMessage":null,"attempt":2,"startedAt":"2026-10-10T10:00:00Z","completedAt":null}
        """;
}

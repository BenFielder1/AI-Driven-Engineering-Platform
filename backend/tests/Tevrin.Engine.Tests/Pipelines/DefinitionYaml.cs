using Tevrin.Engine.Domain.Pipelines;
using Tevrin.Engine.Runtime.Pipelines;

namespace Tevrin.Engine.Tests.Pipelines;

/// <summary>A small valid definition (draft → check → summarise) that tests edit one line at a time.</summary>
public static class DefinitionYaml
{
    public const string Minimal = """
        pipeline: minimal
        version: 1
        title: Minimal
        trigger: { kind: manual }
        approval_modes: [strict, advisory]
        default_approval_mode: strict
        start: [draft]
        settings: [redraft_cycles]
        artifacts:
          draft: { title: Draft, format: json, approval: auto_lock, cascade: downstream }
          report: { title: Report, format: markdown }
          summary: { title: Summary, format: markdown }
        statuses:
          working: { generic: active }
          done: { generic: completed }
        status_changes:
          finish: { stage: summarise, from: [working], to: done, completes_run: true, error_code: NOT_WORKING }
        stages:
          - id: draft
            title: Draft
            status: working
            agent: { key: drafter, tools: [fixture.lookup] }
            output: draft
          - id: check
            title: Check
            status: working
            agent: { key: checker }
            inputs: [draft]
            output: report
            validate:
              - { guard: fixture.report-consistent }
            on_invalid: { retry_with_feedback: true, max_attempts: 3, then: escalate }
          - id: summarise
            title: Summarise
            status: working
            inputs: [report]
            output: summary
            requires:
              - { artifact_locked: report }
            gate: { type: report_approval, modes: [strict], approves: report }

        """;

    public static PackCatalog Catalog => FixturePack.Catalog;

    /// <summary>Parse and validate <paramref name="yaml"/>; the errors as (code, path).</summary>
    public static List<(string Code, string Path)> Errors(string yaml, PackCatalog? catalog = null) =>
        [.. PipelineDefinitionLoader.ParseAndValidate(yaml, catalog ?? Catalog).Errors.Select(e => (e.Code, e.Path))];

    /// <summary><see cref="Minimal"/> with <paramref name="find"/> replaced (which must occur).</summary>
    public static string Edit(string find, string replace, string? yaml = null)
    {
        yaml ??= Minimal;
        Assert.Contains(find, yaml, StringComparison.Ordinal);
        return yaml.Replace(find, replace, StringComparison.Ordinal);
    }
}

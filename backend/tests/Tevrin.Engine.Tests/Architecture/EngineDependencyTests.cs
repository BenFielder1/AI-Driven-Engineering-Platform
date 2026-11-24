using System.Reflection;
using System.Xml.Linq;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain;
using Tevrin.Engine.Runtime;
using Tevrin.Pack.Coach;
using Tevrin.Pack.Sdlc;

namespace Tevrin.Engine.Tests.Architecture;

/// <summary>
/// FR-263: the engine never depends on the SDLC implementation or a pack (Pack → Engine; Tevrin.{Api,Domain,Infrastructure} → Engine and Pack;
/// Engine → nothing of ours), its domain stays pure, and its runtime stays free of ASP.NET Core. Each rule checks the
/// compiled references (<see cref="Assembly.GetReferencedAssemblies"/>, which lists only assemblies the code uses) and the
/// project file's ProjectReference, PackageReference and FrameworkReference items, so an unused reference fails too.
/// The Tevrin.Domain rule (d) stays in Tevrin.UnitTests (DomainDependencyTests).
/// </summary>
public class EngineDependencyTests
{
    /// <summary>Project files use Windows separators in <c>Include</c>; the tests also run on Linux.</summary>
    private const char WindowsSeparator = (char)92;

    /// <summary>FR-323: the host (SDLC implementation) assemblies, named explicitly: a <c>Tevrin.</c> prefix covers the
    /// engine too.</summary>
    private static readonly string[] HostAssemblies = ["Tevrin.Api", "Tevrin.Domain", "Tevrin.Infrastructure"];

    /// <summary>Pack assemblies share this prefix (<c>Tevrin.Pack.Sdlc</c>).</summary>
    private const string PackPrefix = "Tevrin.Pack.";

    private static readonly (string Project, Assembly Assembly)[] EngineProjects =
    [
        ("Tevrin.Engine.Domain", typeof(EngineDomainAssembly).Assembly),
        ("Tevrin.Engine.Runtime", typeof(EngineRuntimeAssembly).Assembly),
        ("Tevrin.Engine.Api", typeof(EngineApiAssembly).Assembly),
    ];

    [Fact]
    public void FR263_engine_assemblies_reference_no_host_or_pack_assembly()
    {
        var offending = EngineProjects
            .SelectMany(p => References(p.Project, p.Assembly).Where(IsOursButNotEngine).Select(r => $"{p.Project} → {r}"))
            .ToList();

        Assert.Empty(offending);
    }

    [Fact]
    public void FR263_engine_domain_references_no_ef_core_aspnet_core_or_npgsql()
    {
        string[] forbidden = ["Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql"];

        var offending = References("Tevrin.Engine.Domain", typeof(EngineDomainAssembly).Assembly)
            .Where(name => forbidden.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(offending);
    }

    [Fact]
    public void FR263_engine_domain_has_no_project_or_package_references()
    {
        // Pure: nothing to reference at all, so a new reference is a deliberate decision that updates this test.
        Assert.Empty(ProjectItems("Tevrin.Engine.Domain"));
    }

    [Fact]
    public void FR263_engine_runtime_references_no_aspnet_core()
    {
        var offending = References("Tevrin.Engine.Runtime", typeof(EngineRuntimeAssembly).Assembly)
            .Where(name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(offending);
    }

    [Fact]
    public void D_0_7_0_23_the_sdlc_pack_references_only_the_engine_domain()
    {
        // The pack is the bottom of the SDLC side: it references only the engine domain, and the host references it.
        var offending = References("Tevrin.Pack.Sdlc", typeof(SdlcPack).Assembly)
            .Where(name => IsHost(name) || name.Equals("tevrin", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(offending);
        Assert.Equal(["project:Tevrin.Engine.Domain"], ProjectItems("Tevrin.Pack.Sdlc"));
    }

    /// <summary>FR-338: every pack assembly, by project name.</summary>
    private static readonly (string Project, Assembly Assembly)[] PackProjects =
    [
        ("Tevrin.Pack.Sdlc", typeof(SdlcPack).Assembly),
        ("Tevrin.Pack.Coach", typeof(CoachPack).Assembly),
    ];

    [Fact]
    public void FR338_every_pack_references_only_the_engine_domain_never_another_pack_or_a_host()
    {
        var offending = PackProjects
            .SelectMany(p => References(p.Project, p.Assembly)
                .Where(r => IsOursButNotEngine(r) || (r.StartsWith("Tevrin.Engine.", StringComparison.OrdinalIgnoreCase) && r != "Tevrin.Engine.Domain"))
                .Select(r => $"{p.Project} → {r}"))
            .ToList();

        Assert.Empty(offending);
        Assert.All(PackProjects, p => Assert.Equal(["project:Tevrin.Engine.Domain"], ProjectItems(p.Project)));
    }

    [Fact]
    public void FR338_every_pack_project_under_src_is_checked()
    {
        var packs = Directory.EnumerateDirectories(SourceRoot(), PackPrefix + "*").Select(Path.GetFileName).Order(StringComparer.Ordinal);

        Assert.Equal(PackProjects.Select(p => p.Project).Order(StringComparer.Ordinal), packs);
    }

    [Fact]
    public void FR338_no_engine_assembly_references_the_coach_pack()
    {
        var offending = EngineProjects
            .Where(p => References(p.Project, p.Assembly).Contains("Tevrin.Pack.Coach", StringComparer.OrdinalIgnoreCase))
            .Select(p => p.Project)
            .ToList();

        Assert.Empty(offending);
    }

    [Fact]
    public void D_0_7_0_23_engine_sources_hold_no_sdlc_key_value()
    {
        // SDLC keys live in the pack (SdlcAgents, SdlcArtifacts, SdlcGates, SdlcJobTypes); an engine source that spells one
        // as a string literal has taken an SDLC rule into the engine.
        var keys = new[] { typeof(SdlcAgents), typeof(SdlcArtifacts), typeof(SdlcGates), typeof(SdlcJobTypes) }
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => "\"" + (string)f.GetRawConstantValue()! + "\"")
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var offending = EngineProjects
            .SelectMany(p => SourceFiles(p.Project))
            .SelectMany(path =>
            {
                var text = File.ReadAllText(path);
                return keys.Where(k => text.Contains(k, StringComparison.Ordinal)).Select(k => $"{Path.GetFileName(path)}: {k}");
            })
            .ToList();

        Assert.NotEmpty(keys);
        Assert.NotEmpty(EngineProjects.SelectMany(p => SourceFiles(p.Project)));
        Assert.Empty(offending);
    }

    [Fact]
    public void FR263_the_project_file_check_sees_project_and_package_references()
    {
        // Guards the guard: the project-file parsing finds the references the engine projects really have.
        Assert.Contains("project:Tevrin.Engine.Domain", ProjectItems("Tevrin.Engine.Runtime"));
        Assert.Contains("package:Npgsql.EntityFrameworkCore.PostgreSQL", ProjectItems("Tevrin.Engine.Runtime"));
        Assert.Contains("framework:Microsoft.AspNetCore.App", ProjectItems("Tevrin.Engine.Api"));
    }

    /// <summary>Ours but not the engine: a host assembly (<see cref="HostAssemblies"/>, or a pre-0.8.0 <c>Aide</c>/<c>Aide.*</c>
    /// name), a pack (<see cref="PackPrefix"/>), <c>tevrin</c>, or any other <c>Tevrin.*</c> outside <c>Tevrin.Engine.*</c>.</summary>
    private static bool IsOursButNotEngine(string reference) =>
        IsHost(reference)
        || reference.StartsWith(PackPrefix, StringComparison.OrdinalIgnoreCase)
        || (reference.StartsWith("Tevrin.", StringComparison.OrdinalIgnoreCase) && !reference.StartsWith("Tevrin.Engine.", StringComparison.OrdinalIgnoreCase))
        || reference.Equals("tevrin", StringComparison.Ordinal);

    /// <summary>FR-323: a host assembly by its explicit name (or its pre-0.8.0 <c>Aide</c> name).</summary>
    private static bool IsHost(string reference) =>
        HostAssemblies.Contains(reference, StringComparer.OrdinalIgnoreCase)
        || reference.Equals("Aide", StringComparison.OrdinalIgnoreCase)
        || reference.StartsWith("Aide.", StringComparison.OrdinalIgnoreCase);

    /// <summary>The compiled assembly references plus the project file's references (by name).</summary>
    private static IEnumerable<string> References(string project, Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Concat(ProjectItems(project).Select(item => item[(item.IndexOf(':', StringComparison.Ordinal) + 1)..]))
            .Distinct(StringComparer.Ordinal);

    /// <summary>The project file's references as <c>project:Name</c>, <c>package:Id</c> and <c>framework:Name</c>.</summary>
    private static List<string> ProjectItems(string project)
    {
        var path = Path.Combine(SourceRoot(), project, project + ".csproj");
        var doc = XDocument.Load(path);
        return
        [
            .. doc.Descendants("ProjectReference")
                .Select(e => "project:" + Path.GetFileNameWithoutExtension(((string?)e.Attribute("Include") ?? string.Empty).Replace(WindowsSeparator, '/'))),
            .. doc.Descendants("PackageReference").Select(e => "package:" + (string?)e.Attribute("Include")),
            .. doc.Descendants("FrameworkReference").Select(e => "framework:" + (string?)e.Attribute("Include")),
        ];
    }

    /// <summary>The project's C# sources, without build output.</summary>
    private static IEnumerable<string> SourceFiles(string project)
    {
        var root = Path.Combine(SourceRoot(), project);
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var relative = Path.GetRelativePath(root, path).Replace(WindowsSeparator, '/');
                return !relative.StartsWith("obj/", StringComparison.Ordinal) && !relative.StartsWith("bin/", StringComparison.Ordinal);
            });
    }

    /// <summary><c>backend/src</c>, found by walking up from the test binaries.</summary>
    private static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Tevrin.Engine.Domain", "Tevrin.Engine.Domain.csproj");
            if (File.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "src");
            }
        }

        throw new InvalidOperationException($"backend/src not found above {AppContext.BaseDirectory}.");
    }
}

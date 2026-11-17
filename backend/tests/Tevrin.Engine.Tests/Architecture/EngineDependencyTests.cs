using System.Reflection;
using System.Xml.Linq;
using Tevrin.Engine.Api;
using Tevrin.Engine.Domain;
using Tevrin.Engine.Runtime;

namespace Tevrin.Engine.Tests.Architecture;

/// <summary>
/// FR-263: the engine never depends on the SDLC implementation or a pack (Pack → Engine; Aide.* → Engine and Pack;
/// Engine → nothing of ours), its domain stays pure, and its runtime stays free of ASP.NET Core. Each rule checks the
/// compiled references (<see cref="Assembly.GetReferencedAssemblies"/>, which lists only assemblies the code uses) and the
/// project file's ProjectReference, PackageReference and FrameworkReference items, so an unused reference fails too.
/// The Aide.Domain rule (d) stays in Aide.UnitTests (DomainDependencyTests).
/// </summary>
public class EngineDependencyTests
{
    /// <summary>Project files use Windows separators in <c>Include</c>; the tests also run on Linux.</summary>
    private const char WindowsSeparator = (char)92;

    private static readonly (string Project, Assembly Assembly)[] EngineProjects =
    [
        ("Tevrin.Engine.Domain", typeof(EngineDomainAssembly).Assembly),
        ("Tevrin.Engine.Runtime", typeof(EngineRuntimeAssembly).Assembly),
        ("Tevrin.Engine.Api", typeof(EngineApiAssembly).Assembly),
    ];

    [Fact]
    public void FR263_engine_assemblies_reference_no_aide_or_pack_assembly()
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
    public void FR263_the_project_file_check_sees_project_and_package_references()
    {
        // Guards the guard: the project-file parsing finds the references the engine projects really have.
        Assert.Contains("project:Tevrin.Engine.Domain", ProjectItems("Tevrin.Engine.Runtime"));
        Assert.Contains("package:Npgsql.EntityFrameworkCore.PostgreSQL", ProjectItems("Tevrin.Engine.Runtime"));
        Assert.Contains("framework:Microsoft.AspNetCore.App", ProjectItems("Tevrin.Engine.Api"));
    }

    /// <summary>Ours but not the engine: <c>Aide</c>, <c>Aide.*</c>, <c>aide</c> (the CLI alias) or <c>Tevrin.*</c> outside
    /// <c>Tevrin.Engine.*</c> (packs, the CLI).</summary>
    private static bool IsOursButNotEngine(string reference) =>
        reference.Equals("Aide", StringComparison.OrdinalIgnoreCase)
        || reference.StartsWith("Aide.", StringComparison.OrdinalIgnoreCase)
        || (reference.StartsWith("Tevrin.", StringComparison.OrdinalIgnoreCase) && !reference.StartsWith("Tevrin.Engine.", StringComparison.OrdinalIgnoreCase))
        || reference.Equals("tevrin", StringComparison.Ordinal);

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

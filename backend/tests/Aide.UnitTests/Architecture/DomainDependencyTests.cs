using Aide.Domain;

namespace Aide.UnitTests.Architecture;

/// <summary>CLAUDE.md rule 3: business rules live in Aide.Domain, which stays free of EF Core and ASP.NET Core.</summary>
public class DomainDependencyTests
{
    [Fact]
    public void Domain_references_no_persistence_or_web_frameworks()
    {
        string[] forbidden = ["Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql", "Aide.Infrastructure", "Aide.Api"];

        var offending = typeof(DomainAssembly).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => forbidden.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(offending);
    }
}

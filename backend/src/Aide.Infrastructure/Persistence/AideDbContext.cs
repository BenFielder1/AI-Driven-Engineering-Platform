using Microsoft.EntityFrameworkCore;

namespace Aide.Infrastructure.Persistence;

/// <summary>The AIDE database (PostgreSQL, snake_case names). Entities are configured with
/// <see cref="IEntityTypeConfiguration{TEntity}"/> classes in this assembly.</summary>
public sealed class AideDbContext(DbContextOptions<AideDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AideDbContext).Assembly);
    }
}

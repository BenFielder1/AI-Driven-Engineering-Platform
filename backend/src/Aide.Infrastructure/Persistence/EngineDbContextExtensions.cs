using Tevrin.Engine.Runtime.Persistence;

namespace Aide.Infrastructure.Persistence;

/// <summary>D-0.7.0-20: engine callbacks (job hooks) get the engine's view of the context; in Tevrin it is always the
/// <see cref="AideDbContext"/> of the current scope.</summary>
public static class EngineDbContextExtensions
{
    public static AideDbContext AsAide(this IEngineDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db as AideDbContext
            ?? throw new InvalidOperationException($"The engine's DbContext is a {db.GetType().Name}, not the AideDbContext.");
    }
}

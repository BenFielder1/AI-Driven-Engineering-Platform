using System.Security.Cryptography;
using Aide.Domain.Databases;
using Aide.Domain.Deployments;
using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence.Conversion;
using Aide.Infrastructure.Security;

namespace Aide.Api.Features.Databases;

/// <summary>
/// FR-152, §5: a managed database's encrypted connection JSON and password (Data Protection,
/// <see cref="SecretPurposes.ManagedDatabase"/>), and the database as the §4.22 derivation sees it. Nothing here is logged.
/// </summary>
public sealed class ManagedDatabaseSecrets(ISecretProtectors protectors)
{
    private const string PasswordAlphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private ISecretProtector Protector => protectors.ForPurpose(SecretPurposes.ManagedDatabase);

    /// <summary>The stored connection details (empty when none or unreadable).</summary>
    public ManagedDatabaseConnection Connection(ManagedDatabase row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (string.IsNullOrEmpty(row.ConnectionEncrypted) || !Protector.TryUnprotect(row.ConnectionEncrypted, out var json))
        {
            return new ManagedDatabaseConnection();
        }

        try
        {
            return AideJson.Deserialize<ManagedDatabaseConnection>(json) ?? new ManagedDatabaseConnection();
        }
        catch (System.Text.Json.JsonException)
        {
            return new ManagedDatabaseConnection();
        }
    }

    public void SetConnection(ManagedDatabase row, ManagedDatabaseConnection connection)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.ConnectionEncrypted = Protector.Protect(AideJson.Serialize(connection));
    }

    public string? Password(ManagedDatabase row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.DbPasswordEncrypted is { } cipher && Protector.TryUnprotect(cipher, out var password) && password.Length > 0 ? password : null;
    }

    public void SetPassword(ManagedDatabase row, string? password)
    {
        ArgumentNullException.ThrowIfNull(row);
        row.DbPasswordEncrypted = password is null ? null : Protector.Protect(password);
    }

    /// <summary>A 32-character database password (letters and digits, URL-safe; Supabase and Railway accept it).</summary>
    public static string GeneratePassword() => RandomNumberGenerator.GetString(PasswordAlphabet, 32);

    /// <summary>
    /// §4.22: a usable database (available, expiring or paused, with connection details) as the derivation sees it:
    /// Supabase's session and transaction pooler URLs (with the password AIDE holds), project URL and keys; Railway's
    /// reference; otherwise the stored URI. <c>null</c> when the database isn't usable.
    /// </summary>
    public DerivedEnvDatabase? ForDerivation(ManagedDatabase row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Status is not (ManagedDatabaseStatus.Available or ManagedDatabaseStatus.Expiring or ManagedDatabaseStatus.Paused))
        {
            return null;
        }

        var connection = Connection(row);
        if (row.Provider == ManagedDatabaseProvider.Railway)
        {
            return new DerivedEnvDatabase(row.Environment, row.Provider, ManagedDatabaseRules.RailwayReference(connection.RailwayService));
        }

        if (connection.SupabaseRef is not null)
        {
            var password = Password(row);
            string? Pooler(int port) => connection.PoolerHost is { } host && connection.PoolerUser is { } user && password is not null
                ? ManagedDatabaseRules.PoolerUrl(host, user, password, port, connection.DatabaseName)
                : null;
            return new DerivedEnvDatabase(
                row.Environment,
                row.Provider,
                Pooler(ManagedDatabaseRules.SupabaseSessionPort),
                Pooler(ManagedDatabaseRules.SupabaseTransactionPort),
                connection.SupabaseUrl,
                connection.PublishableKey,
                connection.SecretKey);
        }

        return connection.Url is null ? null : new DerivedEnvDatabase(row.Environment, row.Provider, connection.Url);
    }
}

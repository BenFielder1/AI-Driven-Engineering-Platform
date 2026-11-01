using Aide.Domain.Entities;
using Aide.Infrastructure.Persistence.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aide.Infrastructure.Persistence.Configurations;

// NULL-safe uniqueness (§5): the unique indexes below are NULLS NOT DISTINCT (PostgreSQL 15+), so a NULL base_url or
// instance_id counts as one value: at most one gitlab.com row, one Bitbucket Cloud row, and one GitHub login per id.

internal sealed class GitHostInstanceConfiguration : IEntityTypeConfiguration<GitHostInstance>
{
    public void Configure(EntityTypeBuilder<GitHostInstance> builder)
    {
        // FR-133: Bitbucket is Cloud only, so its row never has a base URL.
        builder.ToTable("git_host_instances", t => t.HasCheckConstraint(
            "ck_git_host_instances_bitbucket_base_url", "kind <> 'bitbucket' OR base_url IS NULL"));
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.Kind).IsRequired();
        builder.Property(i => i.Name).HasMaxLength(200).IsRequired();
        builder.Property(i => i.BaseUrl).HasMaxLength(500);
        builder.Property(i => i.OauthClientId).HasColumnType("text").IsRequired();
        builder.Property(i => i.OauthClientSecretEncrypted).HasColumnType("text").IsRequired();
        builder.Property(i => i.AllowInsecureHttp).HasDefaultValue(false).IsRequired();
        builder.Property(i => i.Enabled).HasDefaultValue(true).IsRequired();
        builder.Property(i => i.Version).HasMaxLength(100);
        builder.Property(i => i.CreatedAt).IsRequired();
        builder.Property(i => i.UpdatedAt).IsRequired();
        builder.HasOne<AppUser>().WithMany().HasForeignKey(i => i.CreatedByUserId).OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(i => new { i.Kind, i.BaseUrl })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName("ux_git_host_instances_kind_base_url");
        builder.HasIndex(i => i.CreatedByUserId);
    }
}

internal sealed class UserExternalLoginConfiguration : IEntityTypeConfiguration<UserExternalLogin>
{
    public void Configure(EntityTypeBuilder<UserExternalLogin> builder)
    {
        builder.ToTable("user_external_logins");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Provider).IsRequired();
        builder.Property(l => l.ExternalUserId).HasMaxLength(100).IsRequired();
        builder.Property(l => l.Login).HasColumnType("text").IsRequired();
        builder.Property(l => l.CreatedAt).IsRequired();
        builder.HasOne<AppUser>().WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<GitHostInstance>().WithMany().HasForeignKey(l => l.InstanceId).OnDelete(DeleteBehavior.SetNull);

        // D-V4-18: sign-in matches on (provider, instance, external user id).
        builder.HasIndex(l => new { l.Provider, l.InstanceId, l.ExternalUserId })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName("ux_user_external_logins_provider_instance_external_user");
        builder.HasIndex(l => l.UserId);
        builder.HasIndex(l => l.InstanceId);
    }
}

internal sealed class GitConnectionConfiguration : IEntityTypeConfiguration<GitConnection>
{
    public void Configure(EntityTypeBuilder<GitConnection> builder)
    {
        builder.ToTable("git_connections");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Provider).IsRequired();
        builder.Property(c => c.AuthKind).IsRequired();
        builder.Property(c => c.ExternalUserId).HasMaxLength(100).IsRequired();
        builder.Property(c => c.Login).HasColumnType("text").IsRequired();
        builder.Property(c => c.AccessTokenEncrypted).HasColumnType("text").IsRequired();
        builder.Property(c => c.RefreshTokenEncrypted).HasColumnType("text");
        builder.Property(c => c.TokenEmailEncrypted).HasColumnType("text");
        builder.PrimitiveCollection(c => c.Scopes).HasColumnType("text[]").HasDefaultValueSql("'{}'::text[]").IsRequired();
        builder.Property(c => c.Status).IsRequired();
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.UpdatedAt).IsRequired();
        builder.Property(c => c.RowVersion).IsXminRowVersion();
        builder.HasOne<AppUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<GitHostInstance>().WithMany().HasForeignKey(c => c.InstanceId).OnDelete(DeleteBehavior.Restrict);

        // One connection per user and provider account on an instance. The leading user_id also serves user lookups.
        builder.HasIndex(c => new { c.UserId, c.Provider, c.InstanceId, c.ExternalUserId })
            .IsUnique()
            .HasDatabaseName("ux_git_connections_user_provider_instance_external_user");
        builder.HasIndex(c => c.InstanceId);
    }
}

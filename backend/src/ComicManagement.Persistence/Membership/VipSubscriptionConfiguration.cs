using ComicManagement.Identity.Users;
using ComicManagement.Membership.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class VipSubscriptionConfiguration :
    IEntityTypeConfiguration<VipSubscription>
{
    public void Configure(EntityTypeBuilder<VipSubscription> builder)
    {
        builder.ToTable("vip_subscriptions", DatabaseSchemas.Membership, table =>
        {
            table.HasCheckConstraint(
                "ck_vip_subscriptions_timeline",
                "expires_at > last_fulfilled_at");
            table.HasCheckConstraint("ck_vip_subscriptions_version", "version >= 1");
        });

        builder.HasKey(subscription => subscription.UserId)
            .HasName("pk_vip_subscriptions");
        builder.HasIndex(subscription => subscription.ExpiresAt)
            .HasDatabaseName("ix_vip_subscriptions_expires_at");

        builder.Property(subscription => subscription.UserId)
            .HasColumnName("user_id")
            .ValueGeneratedNever();
        builder.Property(subscription => subscription.ExpiresAt)
            .HasColumnName("expires_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(subscription => subscription.LastFulfilledAt)
            .HasColumnName("last_fulfilled_at")
            .HasColumnType("timestamp with time zone");
        builder.Property(subscription => subscription.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(subscription => subscription.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_vip_subscriptions_users");
    }
}

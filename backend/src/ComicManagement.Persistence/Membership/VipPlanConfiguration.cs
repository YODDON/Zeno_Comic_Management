using ComicManagement.Membership.Plans;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ComicManagement.Persistence;

internal sealed class VipPlanConfiguration : IEntityTypeConfiguration<VipPlan>
{
    private static readonly DateTimeOffset SeedUpdatedAt =
        new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);

    public void Configure(EntityTypeBuilder<VipPlan> builder)
    {
        builder.ToTable("vip_plans", DatabaseSchemas.Membership, table =>
        {
            table.HasCheckConstraint("ck_vip_plans_price", "price_vnd > 0");
            table.HasCheckConstraint(
                "ck_vip_plans_fixed_policy",
                "duration_hours = 720 AND discount_percent = 20");
            table.HasCheckConstraint("ck_vip_plans_version", "version >= 1");
        });

        builder.HasKey(plan => plan.Id).HasName("pk_vip_plans");
        builder.HasIndex(plan => plan.Code)
            .IsUnique()
            .HasDatabaseName("ux_vip_plans_code");
        builder.HasIndex(plan => plan.IsActive)
            .IsUnique()
            .HasFilter("is_active")
            .HasDatabaseName("ux_vip_plans_single_active");

        builder.Property(plan => plan.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(plan => plan.Code)
            .HasColumnName("code")
            .HasMaxLength(VipPlan.MaximumCodeLength)
            .IsRequired();
        builder.Property(plan => plan.Name)
            .HasColumnName("name")
            .HasMaxLength(VipPlan.MaximumNameLength)
            .IsRequired();
        builder.Property(plan => plan.PriceVnd).HasColumnName("price_vnd");
        builder.Property(plan => plan.DurationHours).HasColumnName("duration_hours");
        builder.Property(plan => plan.DiscountPercent).HasColumnName("discount_percent");
        builder.Property(plan => plan.IsActive).HasColumnName("is_active");
        builder.Property(plan => plan.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();
        builder.Property(plan => plan.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamp with time zone");

        builder.HasData(new
        {
            Id = VipPlan.InitialPlanId,
            Code = VipPlan.InitialCode,
            Name = VipPlan.InitialName,
            PriceVnd = VipPlan.InitialPriceVnd,
            DurationHours = VipPlan.FixedDurationHours,
            DiscountPercent = VipPlan.FixedDiscountPercent,
            IsActive = true,
            Version = 1L,
            UpdatedAt = SeedUpdatedAt,
        });
    }
}

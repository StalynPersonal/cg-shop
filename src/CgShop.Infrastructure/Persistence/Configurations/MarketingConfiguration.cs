using CgShop.Domain.Marketing;
using CgShop.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CgShop.Infrastructure.Persistence.Configurations;

internal sealed class StoreBannerConfiguration : IEntityTypeConfiguration<StoreBanner>
{
    public void Configure(EntityTypeBuilder<StoreBanner> b)
    {
        b.ToTable("StoreBanners");
        b.HasKey(x => x.Id);
        b.HasOne<Tenant>().WithMany().HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.StoragePath).HasMaxLength(500).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(50).IsRequired();
        b.Property(x => x.Title).HasMaxLength(StoreBanner.TitleMax);
        b.Property(x => x.Subtitle).HasMaxLength(StoreBanner.SubtitleMax);
        b.Property(x => x.ButtonText).HasMaxLength(StoreBanner.ButtonMax);
        b.Property(x => x.LinkUrl).HasMaxLength(StoreBanner.LinkMax);
        b.HasIndex(x => new { x.TenantId, x.IsActive, x.SortOrder });
    }
}

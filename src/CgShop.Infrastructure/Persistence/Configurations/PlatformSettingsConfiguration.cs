using CgShop.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CgShop.Infrastructure.Persistence.Configurations;

internal sealed class PlatformSettingsConfiguration : IEntityTypeConfiguration<PlatformSettings>
{
    public void Configure(EntityTypeBuilder<PlatformSettings> b)
    {
        b.ToTable("PlatformSettings", t =>
            t.HasCheckConstraint("CK_PlatformSettings_Singleton", "[Id] = '00000000-0000-0000-0000-00000000c0f1'"));
        b.HasKey(s => s.Id);
        b.Property(s => s.Id).ValueGeneratedNever();
        b.Property(s => s.UpdatedBy).HasMaxLength(150);
    }
}

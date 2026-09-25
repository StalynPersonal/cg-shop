using CgShop.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CgShop.Infrastructure.Persistence.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> b)
    {
        b.ToTable("Tenants");
        b.HasKey(t => t.Id);
        b.Property(t => t.Name).HasMaxLength(150).IsRequired();
        b.Property(t => t.Slug).HasMaxLength(40).IsRequired();
        b.HasIndex(t => t.Slug).IsUnique();
        b.Property(t => t.PrimaryColor).HasMaxLength(7).IsFixedLength().IsRequired();
        b.Property(t => t.SecondaryColor).HasMaxLength(7).IsFixedLength();
        b.Property(t => t.LogoUrl).HasMaxLength(500);
        b.Property(t => t.ContactEmail).HasMaxLength(200);
        b.Property(t => t.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        b.Property(t => t.TaxRate).HasPrecision(5, 4);
        b.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
        b.Ignore(t => t.IsActive);

        b.OwnsOne(t => t.PaymentSettings, ps =>
        {
            ps.ToJson();
            ps.Property(p => p.PaymentLinkUrl).HasMaxLength(500);
            ps.Property(p => p.Instructions).HasMaxLength(2000);
            ps.OwnsMany(p => p.BankAccounts);
        });
    }
}

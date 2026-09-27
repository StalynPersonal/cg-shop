using System.Text.Json;
using CgShop.Domain.Catalog;
using CgShop.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CgShop.Infrastructure.Persistence.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

    public void Configure(EntityTypeBuilder<Product> b)
    {
        b.ToTable("Products");
        b.HasKey(p => p.Id);
        b.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.Property(p => p.Name).HasMaxLength(200).IsRequired();
        b.Property(p => p.Slug).HasMaxLength(220).IsRequired();
        b.Property(p => p.Brand).HasMaxLength(100);
        b.Property(p => p.Description).HasMaxLength(4000);
        b.Property(p => p.ImageUrl).HasMaxLength(500);
        b.Property(p => p.Category).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(p => new { p.TenantId, p.Slug }).IsUnique();
        b.HasIndex(p => new { p.TenantId, p.Category, p.IsActive });

        // Atributos específicos por categoría (notas olfativas, material...) como JSON.
        b.Property(p => p.Attributes)
            .HasColumnType("nvarchar(max)")
            .HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, Json) ?? new Dictionary<string, string>(),
                new ValueComparer<Dictionary<string, string>>(
                    (a, c) => a!.Count == c!.Count && !a.Except(c).Any(),
                    v => v.Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key.GetHashCode(), kv.Value.GetHashCode())),
                    v => new Dictionary<string, string>(v)));

        b.HasMany(p => p.Variants).WithOne(v => v.Product).HasForeignKey(v => v.ProductId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Navigation(p => p.Variants).UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasMany(p => p.Images).WithOne().HasForeignKey(i => i.ProductId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(p => p.Images).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Ignore(p => p.MainImage);

        b.Ignore(p => p.MinPrice);
        b.Ignore(p => p.TotalAvailable);
    }
}

internal sealed class ProductImageConfiguration : IEntityTypeConfiguration<ProductImage>
{
    public void Configure(EntityTypeBuilder<ProductImage> b)
    {
        b.ToTable("ProductImages");
        b.HasKey(i => i.Id);
        b.Property(i => i.StoragePath).HasMaxLength(500).IsRequired();
        b.Property(i => i.ContentType).HasMaxLength(50).IsRequired();
        b.Property(i => i.OriginalFileName).HasMaxLength(260).IsRequired();
        b.HasIndex(i => new { i.ProductId, i.SortOrder });
    }
}

internal sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> b)
    {
        b.ToTable("StockMovements");
        b.HasKey(m => m.Id);
        b.Property(m => m.Sku).HasMaxLength(60).IsRequired();
        b.Property(m => m.ProductName).HasMaxLength(200).IsRequired();
        b.Property(m => m.Type).HasConversion<string>().HasMaxLength(30);
        b.Property(m => m.Reason).HasMaxLength(500);
        b.Property(m => m.OrderNumber).HasMaxLength(30);
        b.Property(m => m.UserId).HasMaxLength(450).IsRequired();
        b.Property(m => m.UserName).HasMaxLength(150).IsRequired();
        b.Property(m => m.UserRole).HasMaxLength(30);
        b.HasIndex(m => new { m.TenantId, m.CreatedAtUtc });
        b.HasIndex(m => new { m.TenantId, m.VariantId, m.CreatedAtUtc });
        b.HasIndex(m => new { m.TenantId, m.Type, m.CreatedAtUtc });
        // Sin FK a la variante: el historial se conserva aunque la variante se elimine.
    }
}

internal sealed class ProductVariantConfiguration : IEntityTypeConfiguration<ProductVariant>
{
    public void Configure(EntityTypeBuilder<ProductVariant> b)
    {
        b.ToTable("ProductVariants", t =>
        {
            t.HasCheckConstraint("CK_ProductVariants_Stock", "[StockOnHand] >= 0 AND [StockReserved] >= 0 AND [StockReserved] <= [StockOnHand]");
            t.HasCheckConstraint("CK_ProductVariants_Price", "[Price] > 0");
        });
        b.HasKey(v => v.Id);
        b.Property(v => v.Sku).HasMaxLength(60).IsRequired();
        b.HasIndex(v => new { v.TenantId, v.Sku }).IsUnique();
        b.Property(v => v.Size).HasMaxLength(20);
        b.Property(v => v.Color).HasMaxLength(40);
        b.Property(v => v.Price).HasPrecision(18, 2);
        b.Property(v => v.RowVersion).IsRowVersion();
        b.Ignore(v => v.Available);
        b.Ignore(v => v.Description);
    }
}

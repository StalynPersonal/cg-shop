using CgShop.Domain.Catalog;
using CgShop.Domain.Orders;
using CgShop.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CgShop.Infrastructure.Persistence.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> b)
    {
        b.ToTable("Orders");
        b.HasKey(o => o.Id);
        b.HasOne<Tenant>().WithMany().HasForeignKey(o => o.TenantId).OnDelete(DeleteBehavior.Restrict);
        b.Property(o => o.Number).HasMaxLength(30).IsRequired();
        b.HasIndex(o => new { o.TenantId, o.Number }).IsUnique();
        b.HasIndex(o => new { o.TenantId, o.Status, o.CreatedAtUtc });
        b.HasIndex(o => new { o.Status, o.ReservationExpiresAtUtc });
        b.Property(o => o.Status).HasConversion<string>().HasMaxLength(30);
        b.Property(o => o.PaymentMethod).HasConversion<string>().HasMaxLength(20);
        b.Property(o => o.CustomerName).HasMaxLength(150).IsRequired();
        b.Property(o => o.CustomerEmail).HasMaxLength(200).IsRequired();
        b.Property(o => o.CustomerPhone).HasMaxLength(30);
        b.Property(o => o.ShippingAddress).HasMaxLength(500);
        b.Property(o => o.DeliveryMethod).HasConversion<string>().HasMaxLength(20);
        b.Property(o => o.CustomerUserId).HasMaxLength(450);
        b.Property(o => o.Subtotal).HasPrecision(18, 2);
        b.Property(o => o.Tax).HasPrecision(18, 2);
        b.Property(o => o.Total).HasPrecision(18, 2);
        b.Property(o => o.TaxRate).HasPrecision(5, 4);
        b.Property(o => o.Currency).HasMaxLength(3).IsFixedLength();
        b.Property(o => o.PaymentValidatedBy).HasMaxLength(150);
        b.Property(o => o.AccessToken).HasMaxLength(32).IsFixedLength().IsRequired();
        b.Property(o => o.RowVersion).IsRowVersion();
        b.Ignore(o => o.IsPaid);
        b.Ignore(o => o.HoldsReservation);

        b.HasMany(o => o.Items).WithOne().HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(o => o.History).WithOne().HasForeignKey(h => h.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(o => o.Receipts).WithOne().HasForeignKey(r => r.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(o => o.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Navigation(o => o.History).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Navigation(o => o.Receipts).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> b)
    {
        b.ToTable("OrderItems");
        b.HasKey(i => i.Id);
        b.Property(i => i.Sku).HasMaxLength(60).IsRequired();
        b.Property(i => i.ProductName).HasMaxLength(200).IsRequired();
        b.Property(i => i.VariantDescription).HasMaxLength(200);
        b.Property(i => i.UnitPrice).HasPrecision(18, 2);
        b.Ignore(i => i.LineTotal);
        b.HasOne<ProductVariant>().WithMany().HasForeignKey(i => i.VariantId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class OrderStatusHistoryConfiguration : IEntityTypeConfiguration<OrderStatusHistory>
{
    public void Configure(EntityTypeBuilder<OrderStatusHistory> b)
    {
        b.ToTable("OrderStatusHistory");
        b.HasKey(h => h.Id);
        b.Property(h => h.FromStatus).HasConversion<string>().HasMaxLength(30);
        b.Property(h => h.ToStatus).HasConversion<string>().HasMaxLength(30);
        b.Property(h => h.ChangedByUserId).HasMaxLength(450).IsRequired();
        b.Property(h => h.ChangedByName).HasMaxLength(150).IsRequired();
        b.Property(h => h.Note).HasMaxLength(1000);
    }
}

internal sealed class PaymentReceiptConfiguration : IEntityTypeConfiguration<PaymentReceipt>
{
    public void Configure(EntityTypeBuilder<PaymentReceipt> b)
    {
        b.ToTable("PaymentReceipts");
        b.HasKey(r => r.Id);
        b.Property(r => r.OriginalFileName).HasMaxLength(260).IsRequired();
        b.Property(r => r.StoragePath).HasMaxLength(500).IsRequired();
        b.Property(r => r.ContentType).HasMaxLength(100).IsRequired();
        b.Property(r => r.Reference).HasMaxLength(100);
    }
}

internal sealed class StockReservationConfiguration : IEntityTypeConfiguration<StockReservation>
{
    public void Configure(EntityTypeBuilder<StockReservation> b)
    {
        b.ToTable("StockReservations");
        b.HasKey(r => r.Id);
        b.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(r => new { r.Status, r.ExpiresAtUtc });
        b.HasIndex(r => r.OrderId);
        b.HasOne<Order>().WithMany().HasForeignKey(r => r.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ProductVariant>().WithMany().HasForeignKey(r => r.VariantId).OnDelete(DeleteBehavior.Restrict);
    }
}

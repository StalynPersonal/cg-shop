using CgShop.Application.Catalog;
using CgShop.Application.Common;
using CgShop.Application.Orders;
using CgShop.Application.Platform;
using CgShop.Domain.Common;
using CgShop.Domain.Orders;
using CgShop.Infrastructure.Persistence;
using CgShop.IntegrationTests.Infrastructure;
using CgShop.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CgShop.IntegrationTests;

/// <summary>La configuración del Super Admin se guarda en SQL Server y se aplica de inmediato.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class PlatformSettingsIntegrationTests(SqlServerFixture fx) : IAsyncLifetime
{
    private IPlatformSettingsProvider Provider => fx.Services.GetRequiredService<IPlatformSettingsProvider>();

    public Task InitializeAsync() => Provider.ResetAsync(TestData.SuperAdmin);

    // La base es compartida por la colección: siempre volver a los predeterminados.
    public Task DisposeAsync() => Provider.ResetAsync(TestData.SuperAdmin);

    [Fact]
    public async Task Reservation_hours_set_by_super_admin_apply_immediately_to_100_orders()
    {
        var defaults = await Provider.GetAsync();
        defaults.IsCustomized.Should().BeFalse();
        await Provider.UpdateAsync(defaults.Values with { ReservationHours = 12 }, TestData.SuperAdmin);

        var tenant = await fx.CreateTenantAsync();
        await using var scope = fx.TenantScope(tenant);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Products.AddRange(TestData.Products(10, tenant.Id, stockPerVariant: 1000));
        await db.SaveChangesAsync();
        var variants = await db.ProductVariants.Select(v => v.Id).ToListAsync();
        var checkout = scope.ServiceProvider.GetRequiredService<CheckoutService>();

        for (var i = 0; i < TestData.BatchSize; i++)
        {
            var before = DateTime.UtcNow;
            var result = await checkout.PlaceOrderAsync(new PlaceOrderRequest
            {
                CustomerUserId = $"c{i}", FullName = "Cliente", Email = $"c{i}@correo.com", Phone = "809",
                DeliveryMethod = DeliveryMethod.Pickup, Lines = [new CartLine(variants[i % variants.Count], 1)]
            });
            result.ReservationExpiresAtUtc.Should().BeCloseTo(before.AddHours(12), TimeSpan.FromMinutes(1));
        }
    }

    [Fact]
    public async Task Photo_limit_and_receipt_size_follow_saved_settings()
    {
        var current = (await Provider.GetAsync()).Values;
        await Provider.UpdateAsync(current with { MaxImagesPerProduct = 3, MaxReceiptMb = 2 }, TestData.SuperAdmin);

        fx.Services.GetRequiredService<IOptions<CatalogOptions>>().Value.MaxImagesPerProduct.Should().Be(3);
        fx.Services.GetRequiredService<IOptions<OrderOptions>>().Value.MaxReceiptBytes.Should().Be(2 * 1024 * 1024);

        var tenant = await fx.CreateTenantAsync();
        await using var scope = fx.TenantScope(tenant);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Products.AddRange(TestData.Products(1, tenant.Id));
        await db.SaveChangesAsync();
        var productId = await db.Products.Select(p => p.Id).FirstAsync();
        var images = scope.ServiceProvider.GetRequiredService<ProductImageService>();

        for (var i = 0; i < 3; i++)
        {
            var (name, content, _, _) = TestImages.Any(i);
            await images.UploadAsync(productId, name, new MemoryStream(content), content.Length, TestData.TenantStaff);
        }

        var (n, c, _, _) = TestImages.Any(3);
        await images.Invoking(s => s.UploadAsync(productId, n, new MemoryStream(c), c.Length, TestData.TenantStaff))
            .Should().ThrowAsync<DomainException>().WithMessage("*máximo de 3*");
    }

    [Fact]
    public async Task Only_super_admin_saves_and_reset_restores_defaults()
    {
        var view = await Provider.GetAsync();
        await Provider.Invoking(p => p.UpdateAsync(view.Values, TestData.TenantAdmin))
            .Should().ThrowAsync<ForbiddenException>();

        var saved = await Provider.UpdateAsync(view.Values with { IdleTimeoutMinutes = 30 }, TestData.SuperAdmin);
        saved.IsCustomized.Should().BeTrue();
        saved.UpdatedBy.Should().Be(TestData.SuperAdmin.DisplayName);
        Provider.Current.IdleTimeoutMinutes.Should().Be(30); // caché invalidada al guardar

        var reset = await Provider.ResetAsync(TestData.SuperAdmin);
        reset.IsCustomized.Should().BeFalse();
        reset.Values.Should().Be(reset.Defaults);
        Provider.Current.Should().Be(reset.Defaults);
    }

    [Fact]
    public async Task Invalid_values_are_rejected_and_nothing_is_saved()
    {
        var view = await Provider.GetAsync();
        await Provider.Invoking(p => p.UpdateAsync(view.Values with { ReservationHours = 0 }, TestData.SuperAdmin))
            .Should().ThrowAsync<DomainException>();
        (await Provider.GetAsync()).IsCustomized.Should().BeFalse();
    }
}

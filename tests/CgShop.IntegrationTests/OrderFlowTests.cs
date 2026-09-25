using CgShop.Application.Common;
using CgShop.Application.Orders;
using CgShop.Domain.Catalog;
using CgShop.Domain.Common;
using CgShop.Domain.Orders;
using CgShop.Domain.Tenants;
using CgShop.Infrastructure.Persistence;
using CgShop.IntegrationTests.Infrastructure;
using CgShop.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CgShop.IntegrationTests;

/// <summary>Flujo completo de órdenes con validación manual de pago contra SQL Server.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class OrderFlowTests(SqlServerFixture fx)
{
    private async Task<(Tenant Tenant, List<Guid> VariantIds)> TenantWithStockAsync(int stockPerVariant = 1000,
        int products = 10)
    {
        var tenant = await fx.CreateTenantAsync();
        await using var scope = fx.TenantScope(tenant);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var list = TestData.Products(products, tenant.Id, stockPerVariant).ToList();
        db.Products.AddRange(list);
        await db.SaveChangesAsync();
        return (tenant, list.SelectMany(p => p.Variants).Select(v => v.Id).ToList());
    }

    private static PlaceOrderRequest Request(int i, IReadOnlyList<Guid> variantIds, int qty = 1) => new()
    {
        FullName = $"Cliente {i}",
        Email = $"cliente{i}@correo.com",
        Phone = "809-555-0000",
        ShippingAddress = $"Calle {i}",
        PaymentMethod = i % 2 == 0 ? PaymentMethod.BankTransfer : PaymentMethod.PaymentLink,
        Lines = [new CartLine(variantIds[i % variantIds.Count], qty)]
    };

    [Fact]
    public async Task Placing_100_orders_reserves_stock_and_keeps_them_pending()
    {
        var (tenant, variantIds) = await TenantWithStockAsync();
        await using var scope = fx.TenantScope(tenant);
        var checkout = scope.ServiceProvider.GetRequiredService<CheckoutService>();

        var results = new List<PlaceOrderResult>();
        for (var i = 0; i < TestData.BatchSize; i++)
            results.Add(await checkout.PlaceOrderAsync(Request(i, variantIds, qty: 2)));

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var orders = await db.Orders.AsNoTracking().ToListAsync();
        orders.Should().HaveCount(TestData.BatchSize);
        orders.Should().OnlyContain(o => o.Status == OrderStatus.PendingPaymentValidation);
        results.Select(r => r.Number).Should().OnlyHaveUniqueItems();

        (await db.ProductVariants.SumAsync(v => v.StockReserved)).Should().Be(TestData.BatchSize * 2);
        (await db.StockReservations.CountAsync(r => r.Status == ReservationStatus.Active)).Should().Be(TestData.BatchSize);
    }

    [Fact]
    public async Task Owner_validates_50_and_rejects_50_stock_is_committed_or_released()
    {
        var (tenant, variantIds) = await TenantWithStockAsync(stockPerVariant: 1000);
        await using var scope = fx.TenantScope(tenant);
        var sp = scope.ServiceProvider;
        var checkout = sp.GetRequiredService<CheckoutService>();
        var admin = sp.GetRequiredService<OrderAdminService>();
        var db = sp.GetRequiredService<AppDbContext>();
        var initialOnHand = await db.ProductVariants.SumAsync(v => v.StockOnHand);

        var orders = new List<PlaceOrderResult>();
        for (var i = 0; i < TestData.BatchSize; i++)
            orders.Add(await checkout.PlaceOrderAsync(Request(i, variantIds)));

        for (var i = 0; i < orders.Count; i++)
        {
            if (i % 2 == 0)
                await admin.ValidatePaymentAsync(orders[i].OrderId, TestData.TenantAdmin, "Transferencia recibida");
            else
                await admin.RejectPaymentAsync(orders[i].OrderId, TestData.TenantAdmin, "Monto incorrecto");
        }

        db.ChangeTracker.Clear();
        (await db.Orders.CountAsync(o => o.Status == OrderStatus.PaymentValidated)).Should().Be(50);
        (await db.Orders.CountAsync(o => o.Status == OrderStatus.PaymentRejected)).Should().Be(50);
        (await db.ProductVariants.SumAsync(v => v.StockReserved)).Should().Be(0);
        (await db.ProductVariants.SumAsync(v => v.StockOnHand)).Should().Be(initialOnHand - 50);
        (await db.StockReservations.CountAsync(r => r.Status == ReservationStatus.Committed)).Should().Be(50);
        (await db.StockReservations.CountAsync(r => r.Status == ReservationStatus.Released)).Should().Be(50);

        var detail = await admin.GetAsync(orders[0].OrderId);
        detail.History.Should().Contain(h => h.To == OrderStatus.PaymentValidated && h.ChangedBy == TestData.TenantAdmin.DisplayName);
        detail.PaymentValidatedBy.Should().Be(TestData.TenantAdmin.DisplayName);
    }

    [Fact]
    public async Task Staff_cannot_validate_payment()
    {
        var (tenant, variantIds) = await TenantWithStockAsync();
        await using var scope = fx.TenantScope(tenant);
        var order = await scope.ServiceProvider.GetRequiredService<CheckoutService>().PlaceOrderAsync(Request(1, variantIds));
        var admin = scope.ServiceProvider.GetRequiredService<OrderAdminService>();

        await admin.Invoking(a => a.ValidatePaymentAsync(order.OrderId, TestData.TenantStaff, null))
            .Should().ThrowAsync<ForbiddenException>();
        (await admin.GetAsync(order.OrderId)).Status.Should().Be(OrderStatus.PendingPaymentValidation);
    }

    [Fact]
    public async Task Another_tenant_cannot_validate_or_see_the_order()
    {
        var (tenantA, variantIds) = await TenantWithStockAsync();
        var tenantB = await fx.CreateTenantAsync();

        PlaceOrderResult order;
        await using (var scopeA = fx.TenantScope(tenantA))
            order = await scopeA.ServiceProvider.GetRequiredService<CheckoutService>().PlaceOrderAsync(Request(1, variantIds));

        await using var scopeB = fx.TenantScope(tenantB);
        var adminB = scopeB.ServiceProvider.GetRequiredService<OrderAdminService>();
        await adminB.Invoking(a => a.ValidatePaymentAsync(order.OrderId, TestData.TenantAdmin, null))
            .Should().ThrowAsync<NotFoundException>();
        (await adminB.ListAsync(new OrderQuery(PageSize: 200))).TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_checkouts_never_oversell_stock()
    {
        var (tenant, variantIds) = await TenantWithStockAsync(stockPerVariant: 30, products: 1);
        var target = variantIds[0];

        // 100 compras concurrentes de 1 unidad sobre un SKU con 30 unidades.
        var tasks = Enumerable.Range(0, TestData.BatchSize).Select(async i =>
        {
            await using var scope = fx.TenantScope(tenant);
            try
            {
                await scope.ServiceProvider.GetRequiredService<CheckoutService>()
                    .PlaceOrderAsync(Request(i, [target]));
                return true;
            }
            catch (DomainException)
            {
                return false; // stock insuficiente
            }
            catch (DbUpdateConcurrencyException)
            {
                return false; // agotó reintentos por contención
            }
        });
        var outcomes = await Task.WhenAll(tasks);

        await using var check = fx.TenantScope(tenant);
        var variant = await check.ServiceProvider.GetRequiredService<AppDbContext>().ProductVariants.AsNoTracking()
            .FirstAsync(v => v.Id == target);
        var placed = outcomes.Count(o => o);

        placed.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(30);
        variant.StockReserved.Should().Be(placed);
        variant.StockReserved.Should().BeLessThanOrEqualTo(variant.StockOnHand);
    }

    [Fact]
    public async Task Expiry_job_releases_stock_of_100_overdue_orders_across_tenants()
    {
        var (tenant, variantIds) = await TenantWithStockAsync();
        await using (var scope = fx.TenantScope(tenant))
        {
            var checkout = scope.ServiceProvider.GetRequiredService<CheckoutService>();
            for (var i = 0; i < TestData.BatchSize; i++)
                await checkout.PlaceOrderAsync(Request(i, variantIds));

            // Simula que el plazo venció.
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Orders
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.ReservationExpiresAtUtc, DateTime.UtcNow.AddMinutes(-1)));
        }

        await using (var system = fx.SystemScope())
        {
            var processor = system.ServiceProvider.GetRequiredService<ReservationExpiryProcessor>();
            var total = 0;
            int batch;
            while ((batch = await processor.ExpireOverdueAsync(batchSize: 500)) > 0)
                total += batch;
            total.Should().BeGreaterThanOrEqualTo(TestData.BatchSize);
        }

        await using var verify = fx.TenantScope(tenant);
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Orders.CountAsync(o => o.Status == OrderStatus.Expired)).Should().Be(TestData.BatchSize);
        (await db.ProductVariants.SumAsync(v => v.StockReserved)).Should().Be(0);
    }

    [Fact]
    public async Task Expiry_processor_requires_system_scope()
    {
        var tenant = await fx.CreateTenantAsync();
        await using var scope = fx.TenantScope(tenant);
        await scope.ServiceProvider.GetRequiredService<ReservationExpiryProcessor>()
            .Invoking(p => p.ExpireOverdueAsync()).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Customer_uploads_receipts_for_100_orders_and_admin_downloads_them()
    {
        var (tenant, variantIds) = await TenantWithStockAsync();
        await using var scope = fx.TenantScope(tenant);
        var checkout = scope.ServiceProvider.GetRequiredService<CheckoutService>();
        var customer = scope.ServiceProvider.GetRequiredService<CustomerOrderService>();
        var admin = scope.ServiceProvider.GetRequiredService<OrderAdminService>();
        byte[] pdf = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34, 0x0A, 0x25];

        for (var i = 0; i < TestData.BatchSize; i++)
        {
            var order = await checkout.PlaceOrderAsync(Request(i, variantIds));
            var receipt = await customer.UploadReceiptAsync(order.Number, order.AccessToken, $"comprobante-{i}.pdf",
                new MemoryStream(pdf), pdf.Length, $"REF-{i}");

            receipt.ContentType.Should().Be("application/pdf");
            var file = await admin.OpenReceiptAsync(receipt.Id, TestData.TenantStaff);
            file.Should().NotBeNull();
            await using (file!.Value.Content)
            {
                using var ms = new MemoryStream();
                await file.Value.Content.CopyToAsync(ms);
                ms.ToArray().Should().Equal(pdf);
            }
        }

        Directory.GetFiles(Path.Combine(fx.UploadsPath, tenant.Id.ToString("N"), "receipts"))
            .Should().HaveCount(TestData.BatchSize);
    }

    [Fact]
    public async Task Receipt_with_wrong_token_or_fake_content_is_rejected()
    {
        var (tenant, variantIds) = await TenantWithStockAsync();
        await using var scope = fx.TenantScope(tenant);
        var order = await scope.ServiceProvider.GetRequiredService<CheckoutService>().PlaceOrderAsync(Request(1, variantIds));
        var customer = scope.ServiceProvider.GetRequiredService<CustomerOrderService>();
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00];

        await customer.Invoking(c => c.UploadReceiptAsync(order.Number, "TOKEN-INCORRECTO", "a.png",
            new MemoryStream(png), png.Length, null)).Should().ThrowAsync<NotFoundException>();

        byte[] exe = [0x4D, 0x5A, 0x90, 0x00];
        await customer.Invoking(c => c.UploadReceiptAsync(order.Number, order.AccessToken, "virus.png",
            new MemoryStream(exe), exe.Length, null)).Should().ThrowAsync<ValidationException>();

        (await customer.GetAsync(order.Number, "otro")).Should().BeNull();
        (await customer.GetAsync(order.Number, order.AccessToken))!.Receipts.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_after_validation_returns_stock_to_inventory()
    {
        var (tenant, variantIds) = await TenantWithStockAsync(stockPerVariant: 10, products: 1);
        await using var scope = fx.TenantScope(tenant);
        var sp = scope.ServiceProvider;
        var order = await sp.GetRequiredService<CheckoutService>().PlaceOrderAsync(Request(0, variantIds, qty: 4));
        var admin = sp.GetRequiredService<OrderAdminService>();

        await admin.ValidatePaymentAsync(order.OrderId, TestData.TenantAdmin, null);
        await admin.CancelAsync(order.OrderId, TestData.TenantAdmin, "Cliente desistió");

        var variant = await sp.GetRequiredService<AppDbContext>().ProductVariants.AsNoTracking()
            .FirstAsync(v => v.Id == variantIds[0]);
        variant.StockOnHand.Should().Be(10);
        variant.StockReserved.Should().Be(0);
    }

    [Fact]
    public async Task Insufficient_stock_rolls_back_the_whole_order()
    {
        var (tenant, variantIds) = await TenantWithStockAsync(stockPerVariant: 2, products: 3);
        await using var scope = fx.TenantScope(tenant);
        var checkout = scope.ServiceProvider.GetRequiredService<CheckoutService>();

        var request = Request(0, variantIds);
        request.Lines = [new CartLine(variantIds[0], 1), new CartLine(variantIds[1], 5)];

        await checkout.Invoking(c => c.PlaceOrderAsync(request)).Should().ThrowAsync<DomainException>();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.Orders.CountAsync()).Should().Be(0);
        (await db.ProductVariants.SumAsync(v => v.StockReserved)).Should().Be(0);
    }

    [Fact]
    public async Task Payment_method_must_be_enabled_by_tenant()
    {
        var tenant = Tenant.Create("Sin link", $"sl-{Guid.NewGuid():N}"[..15], "#000000");
        tenant.UpdatePaymentSettings(new PaymentSettings
        {
            BankAccounts = [new BankAccount { BankName = "B", AccountNumber = "1", AccountHolder = "H" }]
        });
        await using (var s = fx.NoTenantScope())
        {
            var db = s.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
        }

        await using var scope = fx.TenantScope(tenant);
        var product = Product.Create("Reloj", ProductCategory.Watches);
        product.AddVariant("R-1", 100, 5, color: "Negro");
        var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        ctx.Products.Add(product);
        await ctx.SaveChangesAsync();

        var request = Request(1, [product.Variants[0].Id]); // impar => PaymentLink
        await scope.ServiceProvider.GetRequiredService<CheckoutService>()
            .Invoking(c => c.PlaceOrderAsync(request)).Should().ThrowAsync<DomainException>().WithMessage("*método de pago*");
    }
}

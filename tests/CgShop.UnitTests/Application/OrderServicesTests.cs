using CgShop.Application.Common;
using CgShop.Application.Orders;
using CgShop.Domain.Catalog;
using CgShop.Domain.Common;
using CgShop.Domain.Orders;
using CgShop.Domain.Tenants;
using CgShop.Tests.Shared;
using CgShop.UnitTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CgShop.UnitTests.Application;

public class OrderServicesTests : IAsyncLifetime
{
    private readonly InMemoryDb _db = new();
    private readonly FakeTimeProvider _clock = new(TestData.Now);
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly IFileStorage _storage = Substitute.For<IFileStorage>();
    private readonly IOptions<OrderOptions> _options = Options.Create(new OrderOptions());
    private Tenant _tenant = null!;
    private InMemoryDb.Factory _factory = null!;
    private List<Product> _products = null!;

    public async Task InitializeAsync()
    {
        _tenant = await _db.AddTenantAsync(bankTransfer: true, paymentLink: false);
        _factory = _db.For(_tenant);
        _products = TestData.Products(TestData.BatchSize, _tenant.Id, stockPerVariant: 10).ToList();
        await using var ctx = _factory.CreateAppDbContext();
        ctx.Products.AddRange(_products);
        await ctx.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private CheckoutService Checkout() => new(_factory, _factory.Context, _clock, _options, _notifications,
        NullLogger<CheckoutService>.Instance);

    private OrderAdminService Admin() => new(_factory, _factory.Context, _storage, _clock, _notifications,
        NullLogger<OrderAdminService>.Instance);

    private PlaceOrderRequest Request(int i, int qty = 1) => new()
    {
        CustomerUserId = $"cliente-{i % 10}",
        FullName = $"Cliente {i}",
        Email = $"c{i}@correo.com",
        Phone = "809",
        ShippingAddress = "Calle 1",
        PaymentMethod = PaymentMethod.BankTransfer,
        Lines = [new CartLine(_products[i % _products.Count].Variants[0].Id, qty)]
    };

    [Fact]
    public async Task Checkout_of_100_orders_reserves_stock_and_notifies()
    {
        var checkout = Checkout();
        for (var i = 0; i < TestData.BatchSize; i++)
        {
            var result = await checkout.PlaceOrderAsync(Request(i, qty: 3));
            result.ReservationExpiresAtUtc.Should().Be(TestData.Now.AddHours(48));
        }

        await using var ctx = _factory.CreateAppDbContext();
        (await ctx.Orders.CountAsync()).Should().Be(TestData.BatchSize);
        (await ctx.Orders.AllAsync(o => o.Status == OrderStatus.PendingPaymentValidation)).Should().BeTrue();
        var firstVariants = _products.Select(p => p.Variants[0].Id).ToList();
        (await ctx.ProductVariants.Where(v => firstVariants.Contains(v.Id)).AllAsync(v => v.StockReserved == 3))
            .Should().BeTrue();
        await _notifications.Received(TestData.BatchSize).OrderStatusChangedAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Checkout_rejects_out_of_stock_disabled_method_and_invalid_input()
    {
        var checkout = Checkout();

        await checkout.Invoking(c => c.PlaceOrderAsync(Request(0, qty: 11)))
            .Should().ThrowAsync<DomainException>().WithMessage("*Stock insuficiente*");

        var link = Request(0);
        link.PaymentMethod = PaymentMethod.PaymentLink;
        await checkout.Invoking(c => c.PlaceOrderAsync(link))
            .Should().ThrowAsync<DomainException>().WithMessage("*método de pago*");

        var invalid = new PlaceOrderRequest { CustomerUserId = "cliente-1" };
        var ex = await checkout.Invoking(c => c.PlaceOrderAsync(invalid)).Should().ThrowAsync<ValidationException>();
        ex.Which.Errors.Should().HaveCountGreaterThanOrEqualTo(4);

        var tooMany = Request(0, qty: CheckoutService.MaxQuantityPerLine + 1);
        await checkout.Invoking(c => c.PlaceOrderAsync(tooMany)).Should().ThrowAsync<ValidationException>();

        var unknown = Request(0);
        unknown.Lines = [new CartLine(Guid.NewGuid(), 1)];
        await checkout.Invoking(c => c.PlaceOrderAsync(unknown))
            .Should().ThrowAsync<DomainException>().WithMessage("*ya no están disponibles*");
    }

    [Fact]
    public async Task Checkout_of_100_orders_alternating_shipping_and_pickup()
    {
        var checkout = Checkout();
        for (var i = 0; i < TestData.BatchSize; i++)
        {
            var request = Request(i);
            if (i % 2 == 1)
            {
                request.DeliveryMethod = DeliveryMethod.Pickup;
                request.ShippingAddress = null;
            }

            await checkout.PlaceOrderAsync(request);
        }

        await using var ctx = _factory.CreateAppDbContext();
        (await ctx.Orders.CountAsync(o => o.DeliveryMethod == DeliveryMethod.Pickup && o.ShippingAddress == null)).Should().Be(50);
        (await ctx.Orders.CountAsync(o => o.DeliveryMethod == DeliveryMethod.Shipping && o.ShippingAddress != null)).Should().Be(50);

        var detail = await Admin().ListAsync(new OrderQuery(PageSize: 200));
        detail.Items.Count(o => o.DeliveryMethod == DeliveryMethod.Pickup).Should().Be(50);
    }

    [Fact]
    public async Task Shipping_without_address_is_rejected_but_pickup_is_accepted()
    {
        var checkout = Checkout();
        var shipping = Request(0);
        shipping.ShippingAddress = "";
        var ex = await checkout.Invoking(c => c.PlaceOrderAsync(shipping)).Should().ThrowAsync<ValidationException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("dirección de envío"));

        var pickup = Request(0);
        pickup.ShippingAddress = null;
        pickup.DeliveryMethod = DeliveryMethod.Pickup;
        (await checkout.PlaceOrderAsync(pickup)).Number.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Checkout_requires_an_authenticated_customer()
    {
        var anonymous = Request(0);
        anonymous.CustomerUserId = null;

        await Checkout().Invoking(c => c.PlaceOrderAsync(anonymous))
            .Should().ThrowAsync<ForbiddenException>().WithMessage("*iniciar sesión*");
    }

    [Fact]
    public async Task My_orders_lists_only_the_customer_orders_out_of_100()
    {
        var checkout = Checkout();
        for (var i = 0; i < TestData.BatchSize; i++)
            await checkout.PlaceOrderAsync(Request(i)); // 10 clientes x 10 pedidos

        var customers = new CustomerOrderService(_factory, _factory.Context, _storage, _clock, _options,
            NullLogger<CustomerOrderService>.Instance);

        for (var c = 0; c < 10; c++)
        {
            var mine = await customers.ListMineAsync($"cliente-{c}", 1, 50);
            mine.TotalCount.Should().Be(10);
            mine.Items.Should().OnlyHaveUniqueItems(o => o.Number);
            (await customers.GetAsync(mine.Items[0].Number, mine.Items[0].AccessToken)).Should().NotBeNull();
        }

        (await customers.ListMineAsync("cliente-sin-pedidos")).TotalCount.Should().Be(0);
        var paged = await customers.ListMineAsync("cliente-0", 2, 4);
        paged.Items.Should().HaveCount(4);
        paged.TotalPages.Should().Be(3);
        await customers.Invoking(s => s.ListMineAsync(" ")).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Checkout_without_tenant_is_forbidden()
    {
        var noTenant = _db.For((CgShop.Application.Tenancy.TenantInfo?)null);
        var checkout = new CheckoutService(noTenant, noTenant.Context, _clock, _options, _notifications,
            NullLogger<CheckoutService>.Instance);
        await checkout.Invoking(c => c.PlaceOrderAsync(Request(0))).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Only_tenant_admin_validates_payment_for_100_orders()
    {
        var checkout = Checkout();
        var admin = Admin();
        var ids = new List<Guid>();
        for (var i = 0; i < TestData.BatchSize; i++)
            ids.Add((await checkout.PlaceOrderAsync(Request(i))).OrderId);

        foreach (var id in ids)
        {
            await admin.Invoking(a => a.ValidatePaymentAsync(id, TestData.TenantStaff, null)).Should().ThrowAsync<ForbiddenException>();
            await admin.Invoking(a => a.ValidatePaymentAsync(id, TestData.Customer, null)).Should().ThrowAsync<ForbiddenException>();
            await admin.Invoking(a => a.StartPreparingAsync(id, TestData.TenantStaff)).Should().ThrowAsync<DomainException>();

            await admin.ValidatePaymentAsync(id, TestData.TenantAdmin, "OK");
            await admin.StartPreparingAsync(id, TestData.TenantStaff);
        }

        var list = await admin.ListAsync(new OrderQuery(OrderStatus.Preparing, PageSize: 200));
        list.TotalCount.Should().Be(TestData.BatchSize);

        await using var ctx = _factory.CreateAppDbContext();
        (await ctx.ProductVariants.SumAsync(v => v.StockReserved)).Should().Be(0);
        (await ctx.StockReservations.AllAsync(r => r.Status == ReservationStatus.Committed)).Should().BeTrue();
    }

    [Fact]
    public async Task Validating_100_payments_records_sales_and_cancelling_records_returns()
    {
        var checkout = Checkout();
        var admin = Admin();
        var results = new List<PlaceOrderResult>();
        for (var i = 0; i < TestData.BatchSize; i++)
            results.Add(await checkout.PlaceOrderAsync(Request(i, qty: 2)));

        foreach (var r in results)
            await admin.ValidatePaymentAsync(r.OrderId, TestData.TenantAdmin, null);
        for (var i = 0; i < 10; i++)
            await admin.CancelAsync(results[i].OrderId, TestData.TenantAdmin, "Cliente desistió");

        await using var ctx = _factory.CreateAppDbContext();
        var sales = await ctx.StockMovements.Where(m => m.Type == StockMovementType.Sale).ToListAsync();
        sales.Should().HaveCount(TestData.BatchSize);
        sales.Should().AllSatisfy(m =>
        {
            m.Quantity.Should().Be(-2);
            m.OrderNumber.Should().NotBeNullOrEmpty();
            m.UserName.Should().Be(TestData.TenantAdmin.DisplayName);
        });
        sales.Select(m => m.OrderNumber).Should().BeEquivalentTo(results.Select(r => r.Number));

        var returns = await ctx.StockMovements.Where(m => m.Type == StockMovementType.CancellationReturn).ToListAsync();
        returns.Should().HaveCount(10).And.OnlyContain(m => m.Quantity == 2 && m.Reason == "Cliente desistió");

        var dashboard = await admin.GetDashboardAsync();
        dashboard.ManualAdjustmentsLast7Days.Should().Be(0);
    }

    [Fact]
    public async Task Trending_ranks_units_sold_in_the_last_30_days_and_ignores_unpaid_cancelled_and_old_orders()
    {
        var checkout = Checkout();
        var admin = Admin();
        async Task<PlaceOrderResult> Sell(int product, bool validate = true)
        {
            var request = Request(0);
            request.Lines = [new CartLine(_products[product].Variants[0].Id, 1)];
            var result = await checkout.PlaceOrderAsync(request);
            if (validate)
                await admin.ValidatePaymentAsync(result.OrderId, TestData.TenantAdmin, null);
            return result;
        }

        // Ventas viejas (hace 31 días) del producto 60: no cuentan.
        for (var i = 0; i < 10; i++)
            await Sell(60);
        _clock.Advance(TimeSpan.FromDays(31));

        // 100 pedidos recientes: el producto p (0..9) vende p+1 unidades; 45 productos más venden 1 cada uno.
        var orders = 0;
        for (var p = 0; p < 10; p++)
        for (var n = 0; n <= p; n++, orders++)
        {
            var result = await Sell(p, validate: p != 9);           // los del 9 quedan sin validar
            if (p == 8)
                await admin.CancelAsync(result.OrderId, TestData.TenantAdmin, "Cancelado");  // los del 8 se cancelan
        }
        for (var p = 10; orders < TestData.BatchSize; p++, orders++)
            await Sell(p);

        var catalog = new CgShop.Application.Catalog.CatalogService(_factory);
        var trending = await catalog.GetTrendingAsync(_clock.GetUtcNow().UtcDateTime, take: 5);
        trending.Select(c => c.Id).Should().Equal(
            _products[7].Id, _products[6].Id, _products[5].Id, _products[4].Id, _products[3].Id);

        var all = await catalog.GetTrendingAsync(_clock.GetUtcNow().UtcDateTime, take: 48);
        all.Should().OnlyHaveUniqueItems(c => c.Id);
        all.Select(c => c.Id).Should().NotContain(_products[60].Id, "sus ventas son de hace más de 30 días")
            .And.NotContain(_products[9].Id, "sus pedidos no tienen pago validado")
            .And.NotContain(_products[8].Id, "sus pedidos se cancelaron");
    }

    [Fact]
    public async Task Trending_without_sales_falls_back_to_newest_products()
    {
        var catalog = new CgShop.Application.Catalog.CatalogService(_factory);
        var trending = await catalog.GetTrendingAsync(_clock.GetUtcNow().UtcDateTime, take: 12);

        trending.Should().HaveCount(12);
        var newest = await catalog.SearchAsync(new CgShop.Application.Catalog.CatalogQuery(PageSize: 12));
        trending.Select(c => c.Id).Should().BeEquivalentTo(newest.Items.Select(c => c.Id));
    }

    [Fact]
    public async Task Dashboard_counts_statuses_and_revenue()
    {
        var checkout = Checkout();
        var admin = Admin();
        var results = new List<PlaceOrderResult>();
        for (var i = 0; i < TestData.BatchSize; i++)
            results.Add(await checkout.PlaceOrderAsync(Request(i)));

        for (var i = 0; i < 30; i++)
            await admin.ValidatePaymentAsync(results[i].OrderId, TestData.TenantAdmin, null);
        for (var i = 30; i < 40; i++)
            await admin.RejectPaymentAsync(results[i].OrderId, TestData.TenantAdmin, "No recibido");

        var dash = await admin.GetDashboardAsync();
        dash.PendingValidation.Should().Be(60);
        dash.ToPrepare.Should().Be(30);
        dash.ByStatus[OrderStatus.PaymentRejected].Should().Be(10);
        dash.ValidatedRevenue.Should().Be(results.Take(30).Sum(r => r.Total));
        dash.OrdersToday.Should().Be(TestData.BatchSize);
        dash.ActiveProducts.Should().Be(TestData.BatchSize);

        var search = await admin.ListAsync(new OrderQuery(Search: "c7@correo.com"));
        search.Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Expiry_processor_expires_overdue_orders_only()
    {
        var checkout = Checkout();
        for (var i = 0; i < TestData.BatchSize; i++)
            await checkout.PlaceOrderAsync(Request(i));

        var system = _db.For((CgShop.Application.Tenancy.TenantInfo?)null);
        system.Context.EnterSystemScope();
        var processor = new ReservationExpiryProcessor(system, system.Context, _clock,
            NullLogger<ReservationExpiryProcessor>.Instance);

        _clock.Advance(TimeSpan.FromHours(47));
        (await processor.ExpireOverdueAsync()).Should().Be(0);

        _clock.Advance(TimeSpan.FromHours(1));
        (await processor.ExpireOverdueAsync()).Should().Be(TestData.BatchSize);

        await using var ctx = _factory.CreateAppDbContext();
        (await ctx.Orders.AllAsync(o => o.Status == OrderStatus.Expired)).Should().BeTrue();
        (await ctx.ProductVariants.SumAsync(v => v.StockReserved)).Should().Be(0);
    }

    [Fact]
    public async Task Receipt_upload_validates_and_stores_in_tenant_folder()
    {
        _storage.SaveAsync(_tenant.Id, "receipts", ".png", Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns("receipts/x.png");
        var result = await Checkout().PlaceOrderAsync(Request(1));
        var customer = new CustomerOrderService(_factory, _factory.Context, _storage, _clock, _options,
            NullLogger<CustomerOrderService>.Instance);
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

        var receipt = await customer.UploadReceiptAsync(result.Number, result.AccessToken, "..\\..\\pago.png",
            new MemoryStream(png), png.Length, " REF-9 ");

        receipt.FileName.Should().Be("pago.png");
        receipt.Reference.Should().Be("REF-9");
        receipt.ContentType.Should().Be("image/png");
        var detail = await customer.GetAsync(result.Number, result.AccessToken);
        detail!.Receipts.Should().ContainSingle();
        detail.History.Should().HaveCount(2);

        var instructions = await customer.GetPaymentInstructionsAsync(PaymentMethod.BankTransfer);
        instructions.BankAccounts.Should().ContainSingle();
        (await customer.GetAvailablePaymentMethodsAsync()).Should().Equal(PaymentMethod.BankTransfer);
    }
}

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

        var invalid = new PlaceOrderRequest();
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

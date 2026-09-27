using System.Text;
using CgShop.Application.Catalog;
using CgShop.Application.Common;
using CgShop.Application.Orders;
using CgShop.Application.Reports;
using CgShop.Domain.Catalog;
using CgShop.Domain.Orders;
using CgShop.Tests.Shared;
using CgShop.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CgShop.UnitTests.Application;

/// <summary>Reportes por rango de fechas con 100 pedidos en 10 días y todos los estados.</summary>
public class ReportServiceTests : IAsyncLifetime
{
    private static readonly DateOnly Day1 = new(2026, 9, 1);
    private readonly InMemoryDb _db = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 1, 16, 0, 0, TimeSpan.Zero));
    private InMemoryDb.Factory _factory = null!;
    private List<Product> _products = null!;
    private ReportService _reports = null!;

    /// <summary>Lo que debería salir en el reporte, calculado a medida que se crean los pedidos.</summary>
    private readonly List<(DateOnly Day, OrderStatus Status, decimal Total, int Units, Guid ProductId, string Email)> _expected = [];

    public async Task InitializeAsync()
    {
        _factory = _db.For(await _db.AddTenantAsync());
        _products = TestData.Products(10, _factory.Context.Tenant!.Id, stockPerVariant: 1000).ToList();
        await using (var ctx = _factory.CreateAppDbContext())
        {
            ctx.Products.AddRange(_products);
            await ctx.SaveChangesAsync();
        }

        _reports = new ReportService(_factory);
        var notifications = Substitute.For<INotificationService>();
        var checkout = new CheckoutService(_factory, _factory.Context, _clock, Options.Create(new OrderOptions()),
            notifications, NullLogger<CheckoutService>.Instance);
        var admin = new OrderAdminService(_factory, _factory.Context, Substitute.For<IFileStorage>(), _clock,
            notifications, NullLogger<OrderAdminService>.Instance);

        // En orden cronológico (el reloj de prueba no retrocede): por día y, dentro del día, por hora.
        var sequence = Enumerable.Range(0, TestData.BatchSize).OrderBy(i => i % 10).ThenBy(i => i % 25 == 0);
        foreach (var i in sequence)
        {
            var day = Day1.AddDays(i % 10);
            // Hora local de RD (UTC-4): 12:00, o 23:30 (ya es el día siguiente en UTC).
            var localTime = i % 25 == 0 ? new TimeOnly(23, 30) : new TimeOnly(12, 0);
            _clock.SetUtcNow(new DateTimeOffset(day.ToDateTime(localTime), TimeSpan.FromHours(-4)).ToUniversalTime());

            var product = _products[i % 10];
            var units = 1 + i % 3;
            var result = await checkout.PlaceOrderAsync(new PlaceOrderRequest
            {
                CustomerUserId = $"c{i % 7}", FullName = $"Cliente {i % 7}", Email = $"cliente{i % 7}@correo.com",
                Phone = "809", ShippingAddress = "Calle 1",
                DeliveryMethod = i % 4 == 0 ? DeliveryMethod.Pickup : DeliveryMethod.Shipping,
                PaymentMethod = i % 2 == 0 ? PaymentMethod.BankTransfer : PaymentMethod.PaymentLink,
                Lines = [new CartLine(product.Variants[0].Id, units)]
            });

            var status = (i / 10) switch
            {
                7 => OrderStatus.PendingPaymentValidation,
                8 => OrderStatus.PaymentRejected,
                9 => OrderStatus.Cancelled,
                _ => OrderStatus.PaymentValidated
            };
            if (status == OrderStatus.PaymentValidated)
                await admin.ValidatePaymentAsync(result.OrderId, TestData.TenantAdmin, null);
            else if (status == OrderStatus.PaymentRejected)
                await admin.RejectPaymentAsync(result.OrderId, TestData.TenantAdmin, "No llegó");
            else if (status == OrderStatus.Cancelled)
                await admin.CancelAsync(result.OrderId, TestData.TenantAdmin, "Desistió");

            var total = (await admin.GetAsync(result.OrderId)).Total;
            _expected.Add((day, status, total, units, product.Id, $"cliente{i % 7}@correo.com"));
        }

        // Ajustes manuales en el rango: 5 del empleado (+2) y 3 del dueño (-1).
        var products = new ProductAdminService(_factory, _factory.Context, Substitute.For<IFileStorage>(), _clock,
            NullLogger<ProductAdminService>.Instance);
        for (var i = 0; i < 8; i++)
            await products.AdjustStockAsync(_products[i].Variants[0].Id, i < 5 ? 2 : -1, "Conteo",
                i < 5 ? TestData.TenantStaff : TestData.TenantAdmin);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private List<(DateOnly Day, OrderStatus Status, decimal Total, int Units, Guid ProductId, string Email)> Paid =>
        _expected.Where(e => e.Status == OrderStatus.PaymentValidated).ToList();

    [Fact]
    public async Task Summary_daily_and_breakdowns_match_the_100_orders()
    {
        var r = await _reports.GetSalesReportAsync(Day1, Day1.AddDays(9), TestData.TenantAdmin);

        r.Summary.PaidOrders.Should().Be(70);
        r.Summary.Revenue.Should().Be(Paid.Sum(e => e.Total));
        r.Summary.Subtotal.Should().Be(r.Summary.Revenue - r.Summary.Tax);
        r.Summary.UnitsSold.Should().Be(Paid.Sum(e => e.Units));
        r.Summary.AverageTicket.Should().Be(Math.Round(r.Summary.Revenue / 70, 2, MidpointRounding.AwayFromZero));
        r.Summary.PendingOrders.Should().Be(10);
        r.Summary.RejectedOrders.Should().Be(10);
        r.Summary.CancelledOrders.Should().Be(10);
        r.Summary.ExpiredOrders.Should().Be(0);

        // Un renglón por día (con los días en hora de RD, aunque en UTC el pedido de las 23:30 caiga al día siguiente).
        r.Daily.Select(d => d.Date).Should().Equal(Enumerable.Range(0, 10).Select(Day1.AddDays));
        foreach (var d in r.Daily)
        {
            d.Orders.Should().Be(Paid.Count(e => e.Day == d.Date));
            d.Revenue.Should().Be(Paid.Where(e => e.Day == d.Date).Sum(e => e.Total));
            d.Units.Should().Be(Paid.Where(e => e.Day == d.Date).Sum(e => e.Units));
        }

        var top = r.TopProducts[0];
        top.Units.Should().Be(Paid.GroupBy(e => e.ProductId).Max(g => g.Sum(e => e.Units)));
        r.TopProducts.Select(p => p.Units).Should().BeInDescendingOrder();
        r.ByCategory.Sum(c => c.Units).Should().Be(r.Summary.UnitsSold);
        r.ByCategory.Sum(c => c.Revenue).Should().Be(r.Summary.Subtotal, "por categoría se muestra sin ITBIS");
        r.ByCategory.Sum(c => c.Share).Should().BeApproximately(100, 0.5m);

        r.ByPaymentMethod.Sum(b => b.Orders).Should().Be(70);
        r.ByDelivery.Sum(b => b.Revenue).Should().Be(r.Summary.Revenue);
        r.TopCustomers.Should().HaveCount(7);
        r.TopCustomers.Sum(c => c.Revenue).Should().Be(r.Summary.Revenue);
        r.TopCustomers.Select(c => c.Revenue).Should().BeInDescendingOrder();

        r.AdjustmentsByUser.Should().BeEquivalentTo(new[]
        {
            new AdjustmentsByUserDto(TestData.TenantStaff.DisplayName, "TenantStaff", 5, 10, 0),
            new AdjustmentsByUserDto(TestData.TenantAdmin.DisplayName, "TenantAdmin", 3, 0, 3)
        });
    }

    [Fact]
    public async Task A_single_day_counts_orders_by_local_date_including_late_night_ones()
    {
        // i = 0, 50 (23:30 local) y 10, 20, 30, 40, 60 caen el 1/9; 70 está pendiente (i/10 = 7).
        var r = await _reports.GetSalesReportAsync(Day1, Day1, TestData.TenantAdmin);

        r.Daily.Should().ContainSingle();
        r.Summary.PaidOrders.Should().Be(Paid.Count(e => e.Day == Day1));
        r.Summary.PaidOrders.Should().Be(7);
        r.Summary.PendingOrders.Should().Be(1);
    }

    [Fact]
    public async Task Empty_period_has_zeros_and_invalid_ranges_or_staff_are_rejected()
    {
        var empty = await _reports.GetSalesReportAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), TestData.TenantAdmin);
        empty.Summary.PaidOrders.Should().Be(0);
        empty.Summary.AverageTicket.Should().Be(0);
        empty.Daily.Should().HaveCount(31).And.OnlyContain(d => d.Revenue == 0);
        empty.TopProducts.Should().BeEmpty();

        await _reports.Invoking(s => s.GetSalesReportAsync(Day1.AddDays(1), Day1, TestData.TenantAdmin))
            .Should().ThrowAsync<ValidationException>();
        await _reports.Invoking(s => s.GetSalesReportAsync(Day1, Day1.AddDays(400), TestData.TenantAdmin))
            .Should().ThrowAsync<ValidationException>();
        await _reports.Invoking(s => s.GetSalesReportAsync(Day1, Day1, TestData.TenantStaff))
            .Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Every_section_exports_to_csv_for_excel()
    {
        var r = await _reports.GetSalesReportAsync(Day1, Day1.AddDays(9), TestData.TenantAdmin);

        foreach (var section in ReportCsv.Sections.Keys)
        {
            var bytes = ReportCsv.Build(r, section);
            bytes.Take(3).Should().Equal(Encoding.UTF8.GetPreamble(), "BOM para que Excel lea tildes");
            var lines = Encoding.UTF8.GetString(bytes[3..]).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            lines[0].Should().Contain("01/09/2026").And.Contain("10/09/2026");
            lines.Length.Should().BeGreaterThan(2);
        }

        var daily = Encoding.UTF8.GetString(ReportCsv.Build(r, "diario"));
        daily.Should().Contain("Fecha,Pedidos,Unidades,Ventas").And.Contain("01/09/2026,7,");
        ReportCsv.FileName("productos", Day1, Day1.AddDays(9)).Should().Be("reporte-productos-20260901-20260910.csv");
        FluentActions.Invoking(() => ReportCsv.Build(r, "otra")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Csv_escapes_commas_quotes_and_formula_like_values()
    {
        var r = new SalesReportDto(Day1, Day1,
            new SalesSummaryDto(0, 0, 0, 0, 0, 0, 0, 0, 0, 0), [], [], [], [], [],
            [new CustomerSalesDto("Pérez, Juan \"JP\"", "=HYPERLINK(\"x\")", 1, 10, Day1)], []);

        var csv = Encoding.UTF8.GetString(ReportCsv.Build(r, "clientes"));

        csv.Should().Contain("\"Pérez, Juan \"\"JP\"\"\"").And.Contain("\"'=HYPERLINK(\"\"x\"\")\"");
    }
}

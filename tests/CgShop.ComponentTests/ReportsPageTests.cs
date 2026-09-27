using Bunit;
using CgShop.Application.Orders;
using CgShop.Application.Reports;
using CgShop.Tests.Shared;
using CgShop.Web.Components.Pages.Admin;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace CgShop.ComponentTests;

/// <summary>Página de reportes con 100 pedidos (60 pagados) del día.</summary>
public sealed class ReportsPageTests : StoreTestContext
{
    private async Task SeedAsync()
    {
        await SeedProductsAsync(20);
        var orders = await SeedOrdersAsync();
        var admin = new OrderAdminService(new DirectFactory(this), TenantContext,
            Substitute.For<CgShop.Application.Common.IFileStorage>(), Clock,
            Substitute.For<CgShop.Application.Common.INotificationService>(), NullLogger<OrderAdminService>.Instance);
        foreach (var o in orders.Take(60))
            await admin.ValidatePaymentAsync(o.OrderId, TestData.TenantAdmin, null);
    }

    [Fact]
    public async Task Shows_kpis_chart_and_tables_for_this_month_and_switches_period()
    {
        await SeedAsync();
        JSInterop.Mode = JSRuntimeMode.Loose;
        AuthorizeAs(TestData.TenantAdmin);

        var cut = Render<Reports>(p => p.AddCascadingValue(HostContext));

        cut.WaitForAssertion(() => cut.Find("[data-testid='kpi-orders-value']").TextContent.Should().Be("60"));
        cut.Find("[data-testid='kpi-pending-value']").TextContent.Should().Be("40");
        cut.Find("[data-testid='kpi-units-value']").TextContent.Should().Be("60");
        cut.FindAll("[data-testid='table-products'] tbody tr").Should().HaveCount(ReportService.TopCount);
        cut.Find("[data-testid='csv-productos']").GetAttribute("href").Should().StartWith("/admin/reportes/productos.csv?desde=");

        var today = StoreTime.Today(Clock).ToString("dd/MM/yyyy");
        cut.Find("[data-testid='preset-hoy']").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid='report-period']").TextContent
            .Should().Contain($"Del {today} al {today}"));
        cut.Find("[data-testid='kpi-orders-value']").TextContent.Should().Be("60");

        cut.Find("[data-testid='preset-mes-anterior']").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid='kpi-orders-value']").TextContent.Should().Be("0"));
        cut.Markup.Should().Contain("No hubo ventas en este período.");
    }
}

using Bunit;
using CgShop.Application.Catalog;
using CgShop.Tests.Shared;
using CgShop.Web.Components.Pages.Admin;
using Microsoft.Extensions.Logging.Abstractions;

namespace CgShop.ComponentTests;

/// <summary>El propietario ve cada ajuste hecho por un empleado (100 ajustes).</summary>
public sealed class StockMovementsPageTests : StoreTestContext
{
    private async Task Seed100StaffAdjustmentsAsync()
    {
        await SeedProductsAsync(20);
        var service = new ProductAdminService(new DirectFactory(this), TenantContext, Clock,
            NullLogger<ProductAdminService>.Instance);
        var variants = (await service.GetInventoryAsync()).ToList();
        for (var i = 0; i < TestData.BatchSize; i++)
            await service.AdjustStockAsync(variants[i % variants.Count].VariantId, i % 2 == 0 ? 3 : -1,
                $"Motivo {i}", TestData.TenantStaff);
    }

    [Fact]
    public async Task Owner_sees_staff_adjustments_with_user_and_reason()
    {
        await Seed100StaffAdjustmentsAsync();
        AuthorizeAs(TestData.TenantAdmin);

        var cut = Render<StockMovements>(p => p.AddCascadingValue(HostContext));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='movement-type']").Should().HaveCount(50)); // página 1
        cut.Markup.Should().Contain(TestData.TenantStaff.DisplayName).And.Contain("Empleado").And.Contain("Motivo 99");
        cut.FindAll("[data-testid='movement-qty']").Select(e => e.TextContent.Trim())
            .Should().Contain(["+3", "-1"]);
    }

    [Fact]
    public async Task Dashboard_warns_owner_about_recent_manual_adjustments()
    {
        await Seed100StaffAdjustmentsAsync();
        AuthorizeAs(TestData.TenantAdmin);

        var cut = Render<Dashboard>(p => p.AddCascadingValue(HostContext));

        cut.WaitForAssertion(() => cut.Find("[data-testid='manual-adjustments-alert']").TextContent
            .Should().Contain("100"));
    }

    [Fact]
    public async Task Staff_dashboard_does_not_show_the_audit_alert()
    {
        await Seed100StaffAdjustmentsAsync();
        AuthorizeAs(TestData.TenantStaff);

        var cut = Render<Dashboard>(p => p.AddCascadingValue(HostContext));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Pagos por validar"));
        cut.FindAll("[data-testid='manual-adjustments-alert']").Should().BeEmpty();
    }

    private sealed class DirectFactory(StoreTestContext owner) : CgShop.Application.Common.IAppDbContextFactory
    {
        public CgShop.Application.Common.IAppDbContext CreateDbContext() => owner.CreateDbPublic();
    }
}

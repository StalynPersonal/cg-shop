using Bunit;
using CgShop.Domain.Common;
using CgShop.Tests.Shared;
using CgShop.Web.Components.Pages.Store;
using CgShop.Web.Endpoints;
using MudBlazor;

namespace CgShop.ComponentTests;

public sealed class LoginRedirectTests
{
    [Theory]
    [InlineData(Roles.TenantAdmin, null, "/admin")]
    [InlineData(Roles.TenantAdmin, "/checkout", "/admin")]
    [InlineData(Roles.TenantAdmin, "/admin/pagos", "/admin/pagos")]
    [InlineData(Roles.TenantStaff, null, "/admin")]
    [InlineData(Roles.Customer, null, "/mi-cuenta")]
    [InlineData(Roles.Customer, "/", "/mi-cuenta")]
    [InlineData(Roles.Customer, "/checkout", "/checkout")]
    [InlineData(Roles.Customer, "/admin", "/mi-cuenta")]
    [InlineData(Roles.Customer, "//evil.com", "/mi-cuenta")]
    [InlineData(Roles.Customer, "https://evil.com", "/mi-cuenta")]
    public void Owner_goes_to_admin_panel_and_customer_to_user_panel(string role, string? returnUrl, string expected) =>
        LoginRedirect.Resolve([role], returnUrl, isAdminHost: false).Should().Be(expected);

    [Fact]
    public void Super_admin_goes_home_on_admin_host() =>
        LoginRedirect.Resolve([Roles.SuperAdmin], null, isAdminHost: true).Should().Be("/");
}

/// <summary>"Mi cuenta": el cliente ve solo sus pedidos (100 en total, 10 por página).</summary>
public sealed class CustomerAccountPagesTests : StoreTestContext
{
    [Fact]
    public async Task My_account_lists_only_the_logged_customer_orders()
    {
        await SeedProductsAsync(20);
        var mine = await SeedOrdersAsync(TestData.BatchSize, customerUserId: TestData.Customer.UserId);
        await SeedOrdersAsync(30, customerUserId: "otro-cliente");
        AuthorizeAs(TestData.Customer);

        var cut = Render<MyAccount>(p => p.AddCascadingValue(HostContext));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='my-order-number']").Should().HaveCount(10));
        var shown = cut.FindAll("[data-testid='my-order-number']").Select(e => e.TextContent.TrimStart('#')).ToList();
        shown.Should().BeSubsetOf(mine.Select(o => o.Number));
        cut.FindComponents<MudPagination>().Should().ContainSingle(); // 100 / 10 => 10 páginas
        cut.Markup.Should().Contain("Pagar / comprobante");
    }

    [Fact]
    public void My_account_without_orders_shows_empty_state()
    {
        AuthorizeAs(TestData.Customer);
        var cut = Render<MyAccount>(p => p.AddCascadingValue(HostContext));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Aún no tienes pedidos"));
    }
}

using Bunit;
using CgShop.Domain.Orders;
using CgShop.Tests.Shared;
using CgShop.Web.Components.Pages.Admin;
using Microsoft.EntityFrameworkCore;
using MudBlazor;

namespace CgShop.ComponentTests;

/// <summary>Panel del tenant renderizado con servicios reales y 100 pedidos/productos.</summary>
public sealed class AdminPagesTests : StoreTestContext
{
    [Fact]
    public async Task Dashboard_shows_100_orders_pending_validation()
    {
        await SeedProductsAsync();
        await SeedOrdersAsync();
        AuthorizeAs(TestData.TenantAdmin);

        var cut = Render<Dashboard>(p => p.AddCascadingValue(HostContext));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("<b>100</b> pedido(s) esperando validación de pago");
            cut.FindAll("[data-testid='stat-value']").First().TextContent.Should().Be("100");
        });
    }

    [Fact]
    public async Task Payment_queue_paginates_100_pending_orders_and_admin_sees_validate_buttons()
    {
        await SeedProductsAsync();
        await SeedOrdersAsync();
        AuthorizeAs(TestData.TenantAdmin);

        var cut = Render<PaymentValidation>(p => p.AddCascadingValue(HostContext));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='pending-order']").Should().HaveCount(12));
        cut.FindAll("[data-testid='validate-payment']").Should().HaveCount(12);
        cut.FindComponents<MudPagination>().Should().ContainSingle(); // 100 / 12 => 9 páginas
    }

    [Fact]
    public async Task Staff_sees_queue_but_cannot_validate_payments()
    {
        await SeedProductsAsync();
        await SeedOrdersAsync();
        AuthorizeAs(TestData.TenantStaff);

        var cut = Render<PaymentValidation>(p => p.AddCascadingValue(HostContext));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='pending-order']").Should().HaveCount(12));
        cut.FindAll("[data-testid='validate-payment']").Should().BeEmpty();
        cut.Markup.Should().Contain("Solo el propietario/administrador de la tienda puede validar pagos.");
    }

    [Fact]
    public async Task Validating_from_the_queue_uses_dialog_and_moves_order_to_validated()
    {
        await SeedProductsAsync(10);
        var orders = await SeedOrdersAsync(3);
        AuthorizeAs(TestData.TenantAdmin);

        var provider = Render<MudDialogProvider>();
        var cut = Render<PaymentValidation>(p => p.AddCascadingValue(HostContext));
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='validate-payment']").Should().HaveCount(3));

        await cut.InvokeAsync(() => cut.FindAll("[data-testid='validate-payment']")[0].Click());
        provider.WaitForAssertion(() => provider.Markup.Should().Contain("Validar pago de #"));
        var confirm = provider.FindAll("button").Last(b => b.TextContent.Contains("Validar pago"));
        await provider.InvokeAsync(() => confirm.Click());

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='pending-order']").Should().HaveCount(2));
        await using var db = CreateDb();
        (await db.Orders.CountAsync(o => o.Status == OrderStatus.PaymentValidated)).Should().Be(1);
        orders.Should().HaveCount(3);
    }

    [Fact]
    public async Task Order_detail_shows_audit_trail_and_owner_actions()
    {
        await SeedProductsAsync(5);
        var order = (await SeedOrdersAsync(1))[0];
        AuthorizeAs(TestData.TenantAdmin);

        var cut = Render<OrderDetail>(p => p.AddCascadingValue(HostContext).Add(x => x.Id, order.OrderId));

        cut.WaitForAssertion(() => cut.Find("[data-testid='order-title']").TextContent.Should().Be($"#{order.Number}"));
        cut.Markup.Should().Contain("Validar pago").And.Contain("Rechazar pago").And.Contain("Auditoría");
        cut.Markup.Should().Contain("Pendiente de validación de pago");
        cut.Markup.Should().NotContain("Marcar enviado"); // no se despacha sin pago validado
    }

    [Fact]
    public async Task Pickup_order_in_preparation_offers_ready_for_pickup_and_never_ship()
    {
        await SeedProductsAsync(5);
        var order = (await SeedOrdersAsync(1))[0];
        await using (var db = CreateDb())
        {
            var entity = await db.Orders.Include(o => o.History).FirstAsync(o => o.Id == order.OrderId);
            // Convierte el pedido semilla en retiro en tienda y lo lleva a "En preparación".
            db.Entry(entity).Property(nameof(CgShop.Domain.Orders.Order.DeliveryMethod)).CurrentValue = DeliveryMethod.Pickup;
            db.Entry(entity).Property(nameof(CgShop.Domain.Orders.Order.ShippingAddress)).CurrentValue = null;
            entity.ValidatePayment(TestData.TenantAdmin, null, DateTime.UtcNow);
            entity.StartPreparing(TestData.TenantStaff, DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        AuthorizeAs(TestData.TenantStaff);
        var cut = Render<OrderDetail>(p => p.AddCascadingValue(HostContext).Add(x => x.Id, order.OrderId));

        cut.WaitForAssertion(() => cut.Find("[data-testid='ready-for-pickup']"));
        cut.Markup.Should().NotContain("Marcar enviado");
    }

    [Fact]
    public async Task Staff_detail_hides_validation_actions()
    {
        await SeedProductsAsync(5);
        var order = (await SeedOrdersAsync(1))[0];
        AuthorizeAs(TestData.TenantStaff);

        var cut = Render<OrderDetail>(p => p.AddCascadingValue(HostContext).Add(x => x.Id, order.OrderId));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Solo el propietario puede validar el pago"));
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Validar pago");
    }

    [Fact]
    public async Task Inventory_lists_all_variants_of_100_products_with_reserved_stock()
    {
        await SeedProductsAsync();
        await SeedOrdersAsync();
        AuthorizeAs(TestData.TenantStaff);

        var cut = Render<Inventory>(p => p.AddCascadingValue(HostContext));

        cut.WaitForAssertion(() => cut.FindComponent<MudDataGrid<CgShop.Application.Catalog.InventoryRowDto>>()
            .Instance.Items!.Count().Should().Be(TestData.Products().Sum(p => p.Variants.Count)));
        var rows = cut.FindComponent<MudDataGrid<CgShop.Application.Catalog.InventoryRowDto>>().Instance.Items!;
        rows.Sum(r => r.StockReserved).Should().Be(TestData.BatchSize);
    }
}

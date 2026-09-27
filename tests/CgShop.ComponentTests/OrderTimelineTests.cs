using Bunit;
using CgShop.Application.Orders;
using CgShop.Domain.Orders;
using CgShop.Tests.Shared;
using CgShop.Web.Components.Shared;

namespace CgShop.ComponentTests;

public sealed class OrderTimelineTests : MudTestContext
{
    private static IReadOnlyList<OrderHistoryDto> HistoryOf(Order o) =>
        o.History.Select(h => new OrderHistoryDto(h.FromStatus, h.ToStatus, h.ChangedByName, h.Note, h.ReceiptId,
            h.ChangedAtUtc)).ToList();

    [Fact]
    public void Receipt_upload_is_shown_as_an_event_not_as_a_repeated_status_for_100_orders()
    {
        foreach (var order in TestData.Orders())
        {
            order.AttachReceipt("pago.pdf", "p", "application/pdf", 10, null, TestData.Now);
            order.ValidatePayment(TestData.TenantAdmin, "  ", TestData.Now);

            var cut = Render<OrderTimeline>(p => p.Add(x => x.History, HistoryOf(order)));

            var statusTitles = cut.FindAll("[data-testid='timeline-status'] .mud-typography-subtitle2")
                .Select(e => e.TextContent.Trim()).ToList();
            statusTitles.Should().OnlyHaveUniqueItems()
                .And.Equal("Pago validado", "Pendiente de validación de pago");
            cut.Find("[data-testid='timeline-event']").TextContent.Should().Contain("Comprobante de pago adjuntado");
            cut.Markup.Should().Contain("Pago validado manualmente.");
            cut.FindAll(".mud-typography-caption").Should().OnlyContain(c => !c.TextContent.TrimEnd().EndsWith("·"));
        }
    }

    [Fact]
    public void Admin_audit_shows_actor_and_transition()
    {
        var order = TestData.Order(1);
        order.ValidatePayment(TestData.TenantAdmin, "Transferencia verificada", TestData.Now);

        var cut = Render<OrderTimeline>(p => p.Add(x => x.History, HistoryOf(order))
            .Add(x => x.ShowActor, true).Add(x => x.ShowTransition, true));

        cut.Markup.Should().Contain("Pendiente de validación de pago → Pago validado")
            .And.Contain(TestData.TenantAdmin.DisplayName).And.Contain("Transferencia verificada");
    }
}

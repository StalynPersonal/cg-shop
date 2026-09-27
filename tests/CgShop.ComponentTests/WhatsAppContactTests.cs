using Bunit;
using CgShop.Tests.Shared;
using CgShop.Web.Components.Shared;

namespace CgShop.ComponentTests;

public sealed class WhatsAppContactTests : MudTestContext
{
    [Fact]
    public void Shows_number_and_prefilled_link_for_100_orders()
    {
        foreach (var order in TestData.Orders())
        {
            var cut = Render<WhatsAppContact>(p => p
                .Add(x => x.Number, "+1 809 555 1234")
                .Add(x => x.OrderNumber, order.Number)
                .Add(x => x.Total, order.Total));

            cut.Find("[data-testid='whatsapp-number']").TextContent.Should().Be("+1 809 555 1234");
            var href = cut.Find("[data-testid='whatsapp-button']").GetAttribute("href");
            href.Should().StartWith("https://wa.me/18095551234?text=");
            Uri.UnescapeDataString(href!).Should().Contain($"#{order.Number}").And.Contain("RD$");
            cut.Markup.Should().Contain("Para confirmar tu pago");
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("123")]
    public void Renders_nothing_without_a_valid_number(string? number)
    {
        var cut = Render<WhatsAppContact>(p => p.Add(x => x.Number, number));
        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Checkout_variant_hides_button()
    {
        var cut = Render<WhatsAppContact>(p => p.Add(x => x.Number, "8095551234").Add(x => x.ShowButton, false));
        cut.FindAll("[data-testid='whatsapp-button']").Should().BeEmpty();
        cut.Find("[data-testid='whatsapp-contact']");
    }
}

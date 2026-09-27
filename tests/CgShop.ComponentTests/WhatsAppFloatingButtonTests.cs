using Bunit;
using CgShop.Domain.Tenants;
using CgShop.Web.Components.Shared;
using Microsoft.EntityFrameworkCore;

namespace CgShop.ComponentTests;

/// <summary>Botón flotante de WhatsApp de la tienda.</summary>
public sealed class WhatsAppFloatingButtonTests : StoreTestContext
{
    private async Task ConfigureAsync(bool show)
    {
        await using var db = CreateDb();
        var tenant = await db.Tenants.FirstAsync(t => t.Id == Tenant.Id);
        var ps = tenant.PaymentSettings;
        tenant.UpdatePaymentSettings(new PaymentSettings
        {
            BankAccounts = ps.BankAccounts, PaymentLinkUrl = ps.PaymentLinkUrl, PickupAddress = ps.PickupAddress,
            WhatsAppNumber = ps.WhatsAppNumber, ShowWhatsAppButton = show
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Shows_a_chat_link_with_the_store_name_when_enabled()
    {
        await ConfigureAsync(show: true);

        var cut = Render<WhatsAppFloatingButton>(p => p.Add(x => x.StoreName, "Tienda Verde"));

        cut.WaitForAssertion(() => cut.Find("[data-testid='whatsapp-float']"));
        var href = cut.Find("[data-testid='whatsapp-float']").GetAttribute("href");
        href.Should().StartWith("https://wa.me/18095551234?text=").And.Contain(Uri.EscapeDataString("Hola Tienda Verde"));
        cut.Find("[data-testid='whatsapp-float']").GetAttribute("target").Should().Be("_blank");
    }

    [Fact]
    public async Task Is_hidden_when_the_owner_did_not_enable_it()
    {
        await ConfigureAsync(show: false);

        var cut = Render<WhatsAppFloatingButton>();

        cut.FindAll("[data-testid='whatsapp-float']").Should().BeEmpty();
    }
}

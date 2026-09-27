using Bunit;
using CgShop.Domain.Orders;
using CgShop.Tests.Shared;
using CgShop.Web.Components.Shared;

namespace CgShop.ComponentTests;

public sealed class DeliveryOptionsTests : MudTestContext
{
    [Fact]
    public void Shipping_shows_required_address_and_extra_cost_note()
    {
        var cut = Render<DeliveryOptions>(p => p.Add(x => x.Method, DeliveryMethod.Shipping));

        cut.FindAll("[data-testid='shipping-address']").Should().NotBeEmpty();
        cut.Find("[data-testid='shipping-cost-note']").TextContent.Should().Contain("costo adicional");
        cut.FindAll("[data-testid='pickup-note']").Should().BeEmpty();
    }

    [Fact]
    public void Pickup_hides_address_and_shows_store_pickup_address_for_100_stores()
    {
        foreach (var tenant in TestData.Tenants())
        {
            var pickupAddress = $"Local de {tenant.Name}, Santo Domingo";
            var cut = Render<DeliveryOptions>(p => p
                .Add(x => x.Method, DeliveryMethod.Pickup)
                .Add(x => x.PickupAddress, pickupAddress));

            cut.FindAll("[data-testid='shipping-address']").Should().BeEmpty();
            cut.FindAll("[data-testid='shipping-cost-note']").Should().BeEmpty();
            cut.Find("[data-testid='pickup-note']").TextContent.Should().Contain(pickupAddress);
        }
    }

    [Fact]
    public void Switching_to_pickup_clears_address_and_hides_field()
    {
        var method = DeliveryMethod.Shipping;
        string? address = "Calle 1";
        var cut = Render<DeliveryOptions>(p => p
            .Add(x => x.Method, method)
            .Add(x => x.MethodChanged, m => method = m)
            .Add(x => x.Address, address)
            .Add(x => x.AddressChanged, a => address = a));

        cut.Find("input[data-testid='delivery-pickup']").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='shipping-address']").Should().BeEmpty());

        method.Should().Be(DeliveryMethod.Pickup);
        address.Should().BeNull();
    }
}

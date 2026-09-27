using CgShop.Domain.Common;
using CgShop.Domain.Tenants;
using CgShop.Tests.Shared;

namespace CgShop.UnitTests.Domain;

public class WhatsAppTests
{
    [Theory]
    [InlineData("+1 809 555 1234", "18095551234")]
    [InlineData("809-555-1234", "18095551234")]
    [InlineData("(829) 555-9876", "18295559876")]
    [InlineData("849.555.0000", "18495550000")]
    [InlineData("+34 612 345 678", "34612345678")]
    public void Normalizes_to_international_digits(string input, string expected) =>
        WhatsApp.ToInternational(input).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("abc")]
    [InlineData("1234567890123456")]
    public void Rejects_invalid_numbers(string? input) => WhatsApp.IsValid(input).Should().BeFalse();

    [Fact]
    public void Builds_chat_links_with_order_message_for_100_numbers()
    {
        for (var i = 0; i < TestData.BatchSize; i++)
        {
            var link = WhatsApp.ChatLink($"809-555-{i:0000}", $"Hola, pedido #ORD-{i}");

            link.Should().StartWith($"https://wa.me/1809555{i:0000}?text=");
            link.Should().Contain(Uri.EscapeDataString($"#ORD-{i}"));
        }
    }

    [Fact]
    public void Tenant_accepts_valid_whatsapp_and_rejects_invalid()
    {
        foreach (var tenant in TestData.Tenants())
        {
            tenant.UpdatePaymentSettings(new PaymentSettings { WhatsAppNumber = "+1 809 555 1234" });
            tenant.PaymentSettings.WhatsAppNumber.Should().Be("+1 809 555 1234");

            tenant.Invoking(t => t.UpdatePaymentSettings(new PaymentSettings { WhatsAppNumber = "123" }))
                .Should().Throw<DomainException>().WithMessage("*WhatsApp*");
        }
    }
}

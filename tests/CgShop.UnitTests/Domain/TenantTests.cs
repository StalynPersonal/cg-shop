using CgShop.Domain.Common;
using CgShop.Domain.Tenants;
using CgShop.Tests.Shared;

namespace CgShop.UnitTests.Domain;

public class TenantTests
{
    [Fact]
    public void Creates_100_tenants_with_mandatory_primary_color()
    {
        var tenants = TestData.Tenants().ToList();

        tenants.Should().HaveCount(TestData.BatchSize);
        tenants.Should().AllSatisfy(t =>
        {
            HexColor.IsValid(t.PrimaryColor).Should().BeTrue();
            t.PrimaryColor.Should().Be(t.PrimaryColor.ToUpperInvariant());
            t.IsActive.Should().BeTrue();
            t.TaxRate.Should().Be(0.18m);
            t.Currency.Should().Be("DOP");
        });
        tenants.Select(t => t.Slug).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("")]
    [InlineData("verde")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("00FF00")]
    public void Rejects_missing_or_invalid_primary_color(string color)
    {
        var act = () => Tenant.Create("Empresa", "empresa", color);
        act.Should().Throw<DomainException>().WithMessage("*color primario*");
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("www")]
    [InlineData("a")]
    [InlineData("-malo")]
    [InlineData("con espacio")]
    [InlineData("tienda_1")]
    public void Rejects_invalid_or_reserved_slugs(string slug)
    {
        var act = () => Tenant.Create("Empresa", slug, "#00AA00");
        act.Should().Throw<DomainException>().WithMessage("*subdominio*");
    }

    [Fact]
    public void Slug_is_normalized_to_lowercase()
    {
        Tenant.Create("Empresa", "  Mi-Tienda ", "#00aa00").Slug.Should().Be("mi-tienda");
    }

    [Theory]
    [InlineData("#FFFFFF", "#000000")]
    [InlineData("#FFEB3B", "#000000")]
    [InlineData("#000000", "#FFFFFF")]
    [InlineData("#1B5E20", "#FFFFFF")]
    [InlineData("#C62828", "#FFFFFF")]
    public void Contrast_text_is_readable(string background, string expected) =>
        HexColor.ContrastText(background).Should().Be(expected);

    [Fact]
    public void Payment_link_must_be_https()
    {
        var tenant = Tenant.Create("Empresa", "empresa", "#00AA00");

        tenant.Invoking(t => t.UpdatePaymentSettings(new PaymentSettings { PaymentLinkUrl = "http://pago.com" }))
            .Should().Throw<DomainException>();

        tenant.UpdatePaymentSettings(new PaymentSettings
        {
            PaymentLinkUrl = "https://pago.com/x",
            BankAccounts = [new BankAccount { BankName = "Banco Popular", AccountNumber = "123", AccountHolder = "Empresa SRL" }]
        });
        tenant.PaymentSettings.AcceptsBankTransfer.Should().BeTrue();
        tenant.PaymentSettings.AcceptsPaymentLink.Should().BeTrue();
    }

    [Fact]
    public void Incomplete_bank_account_is_rejected()
    {
        var tenant = Tenant.Create("Empresa", "empresa", "#00AA00");
        tenant.Invoking(t => t.UpdatePaymentSettings(new PaymentSettings { BankAccounts = [new BankAccount { BankName = "BHD" }] }))
            .Should().Throw<DomainException>();
    }

    [Fact]
    public void Suspend_and_activate()
    {
        var tenant = Tenant.Create("Empresa", "empresa", "#00AA00");
        tenant.Suspend();
        tenant.IsActive.Should().BeFalse();
        tenant.Activate();
        tenant.IsActive.Should().BeTrue();
    }
}

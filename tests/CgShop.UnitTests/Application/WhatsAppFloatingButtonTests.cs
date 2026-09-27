using CgShop.Application.Common;
using CgShop.Application.Tenants;
using CgShop.Domain.Common;
using CgShop.Domain.Tenants;
using CgShop.Tests.Shared;
using CgShop.UnitTests.Support;

namespace CgShop.UnitTests.Application;

/// <summary>Botón flotante de WhatsApp: lo activa el propietario y solo con número configurado.</summary>
public class WhatsAppFloatingButtonTests
{
    private readonly InMemoryDb _db = new();

    [Fact]
    public async Task Each_of_100_stores_shows_the_button_only_when_the_owner_enabled_it()
    {
        var expected = new List<(InMemoryDb.Factory Factory, string? Number)>();
        for (var i = 0; i < TestData.BatchSize; i++)
        {
            var factory = _db.For(await _db.AddTenantAsync($"tienda-{i:000}"));
            var service = new TenantSettingsService(factory, factory.Context);
            var settings = await service.GetPaymentSettingsAsync();
            settings.ShowWhatsAppButton.Should().BeFalse("viene apagado por defecto");

            var number = i % 3 == 0 ? null : $"+1 809 555 {i:0000}";
            settings.WhatsAppNumber = number;
            settings.ShowWhatsAppButton = number is not null && i % 2 == 0;
            await service.UpdatePaymentSettingsAsync(settings, TestData.TenantAdmin);
            expected.Add((factory, settings.ShowWhatsAppButton ? number : null));
        }

        foreach (var (factory, number) in expected)
        {
            var service = new TenantSettingsService(factory, factory.Context);
            (await service.GetFloatingWhatsAppAsync()).Should().Be(number);
            (await service.GetPaymentSettingsAsync()).ShowWhatsAppButton.Should().Be(number is not null);
        }
    }

    [Fact]
    public async Task Cannot_enable_the_button_without_a_number_and_staff_cannot_change_it()
    {
        var factory = _db.For(await _db.AddTenantAsync());
        var service = new TenantSettingsService(factory, factory.Context);
        var settings = await service.GetPaymentSettingsAsync();
        settings.WhatsAppNumber = null;
        settings.ShowWhatsAppButton = true;

        await service.Invoking(s => s.UpdatePaymentSettingsAsync(settings, TestData.TenantAdmin))
            .Should().ThrowAsync<DomainException>().WithMessage("*botón flotante*número*");

        settings.WhatsAppNumber = "+1 809 555 1234";
        await service.Invoking(s => s.UpdatePaymentSettingsAsync(settings, TestData.TenantStaff))
            .Should().ThrowAsync<ForbiddenException>();
        (await service.GetFloatingWhatsAppAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Without_an_active_store_there_is_no_button()
    {
        var factory = _db.For((CgShop.Application.Tenancy.TenantInfo?)null);
        (await new TenantSettingsService(factory, factory.Context).GetFloatingWhatsAppAsync()).Should().BeNull();
    }
}

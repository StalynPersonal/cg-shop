using CgShop.Application.Common;
using CgShop.Application.Tenancy;
using CgShop.Domain.Common;
using CgShop.Domain.Tenants;
using Microsoft.EntityFrameworkCore;

namespace CgShop.Application.Tenants;

/// <summary>Configuración de pagos (cuentas bancarias / enlace de pago) del tenant activo.</summary>
public sealed class TenantSettingsService(IAppDbContextFactory dbFactory, ITenantContext tenantContext)
{
    public async Task<PaymentSettings> GetPaymentSettingsAsync(CancellationToken ct = default)
    {
        var info = Guard.RequireTenant(tenantContext);
        await using var db = dbFactory.CreateDbContext();
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == info.Id, ct);
        return new PaymentSettings
        {
            PaymentLinkUrl = tenant.PaymentSettings.PaymentLinkUrl,
            Instructions = tenant.PaymentSettings.Instructions,
            PickupAddress = tenant.PaymentSettings.PickupAddress,
            WhatsAppNumber = tenant.PaymentSettings.WhatsAppNumber,
            ShowWhatsAppButton = tenant.PaymentSettings.ShowWhatsAppButton,
            BankAccounts = tenant.PaymentSettings.BankAccounts.Select(b => new BankAccount
            {
                BankName = b.BankName, AccountNumber = b.AccountNumber, AccountHolder = b.AccountHolder,
                AccountType = b.AccountType, HolderDocument = b.HolderDocument
            }).ToList()
        };
    }

    public async Task UpdatePaymentSettingsAsync(PaymentSettings settings, ActorInfo actor,
        CancellationToken ct = default)
    {
        Guard.RequireTenantAdmin(actor);
        var info = Guard.RequireTenant(tenantContext);
        await using var db = dbFactory.CreateDbContext();
        var tenant = await db.Tenants.FirstAsync(t => t.Id == info.Id, ct);
        tenant.UpdatePaymentSettings(settings);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Número para el botón flotante de WhatsApp de la tienda, o null si el propietario no lo activó.</summary>
    public async Task<string?> GetFloatingWhatsAppAsync(CancellationToken ct = default)
    {
        if (tenantContext.Tenant is not { } info)
            return null;
        await using var db = dbFactory.CreateDbContext();
        var settings = await db.Tenants.AsNoTracking().Where(t => t.Id == info.Id)
            .Select(t => t.PaymentSettings).FirstOrDefaultAsync(ct);
        return settings is { ShowWhatsAppButton: true, WhatsAppNumber: { Length: > 0 } number } ? number : null;
    }
}

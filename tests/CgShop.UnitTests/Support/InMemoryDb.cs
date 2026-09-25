using CgShop.Application.Common;
using CgShop.Application.Tenancy;
using CgShop.Domain.Tenants;
using CgShop.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CgShop.UnitTests.Support;

/// <summary>Base InMemory compartida por nombre; cada fábrica usa su propio TenantContext.</summary>
public sealed class InMemoryDb
{
    private readonly DbContextOptions<AppDbContext> _options;

    public InMemoryDb()
    {
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"cgshop-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
    }

    public AppDbContext Create(ITenantContext tenant) => new(_options, tenant);

    public Factory For(TenantInfo? tenant)
    {
        var context = new TenantContext();
        if (tenant is not null)
            context.SetTenant(tenant);
        return new Factory(this, context);
    }

    public Factory For(Tenant tenant) => For(TenantInfo.From(tenant));

    public async Task<Tenant> AddTenantAsync(string slug = "tienda", string color = "#2E7D32",
        bool bankTransfer = true, bool paymentLink = true)
    {
        var tenant = Tenant.Create($"Empresa {slug}", slug, color);
        tenant.UpdatePaymentSettings(new PaymentSettings
        {
            BankAccounts = bankTransfer
                ? [new BankAccount { BankName = "Banco", AccountNumber = "1", AccountHolder = "Titular" }]
                : [],
            PaymentLinkUrl = paymentLink ? "https://pagos.test/x" : null
        });
        await using var db = Create(new TenantContext());
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    public sealed class Factory(InMemoryDb owner, TenantContext context) : IAppDbContextFactory
    {
        public TenantContext Context { get; } = context;
        public IAppDbContext CreateDbContext() => owner.Create(Context);
        public AppDbContext CreateAppDbContext() => owner.Create(Context);
    }
}

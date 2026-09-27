using Bunit;
using CgShop.Application;
using CgShop.Application.Common;
using CgShop.Application.Orders;
using CgShop.Application.Tenancy;
using CgShop.Domain.Common;
using CgShop.Domain.Tenants;
using CgShop.Infrastructure.Persistence;
using CgShop.Tests.Shared;
using CgShop.Web.Services;
using CgShop.Web.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CgShop.ComponentTests;

/// <summary>
/// Contexto para renderizar páginas reales (tienda/panel) con los servicios de aplicación reales
/// sobre EF InMemory, un tenant activo y 100 registros de prueba.
/// </summary>
public abstract class StoreTestContext : MudTestContext
{
    private readonly DbContextOptions<AppDbContext> _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"cgshop-ui-{Guid.NewGuid():N}")
        .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
        .Options;

    protected Tenant Tenant { get; }
    protected TenantInfo TenantInfo { get; }
    protected TenantContext TenantContext { get; } = new();
    protected FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
    protected HostContext HostContext { get; }

    protected StoreTestContext()
    {
        Tenant = Tenant.Create("Tienda Verde", "verde", "#2E7D32");
        Tenant.UpdatePaymentSettings(new PaymentSettings
        {
            BankAccounts = [new BankAccount { BankName = "Banco Popular", AccountNumber = "800-1", AccountHolder = "Verde SRL" }],
            PaymentLinkUrl = "https://pagos.test/verde",
            WhatsAppNumber = "809-555-1234",
            PickupAddress = "Av. Lincoln 100"
        });
        TenantInfo = TenantInfo.From(Tenant);
        TenantContext.SetTenant(TenantInfo);
        HostContext = new HostContext(HostKind.Tenant, TenantInfo);

        using (var db = CreateDb())
        {
            db.Tenants.Add(Tenant);
            db.SaveChanges();
        }

        Services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        Services.AddSingleton<ITenantContext>(TenantContext);
        Services.AddSingleton(TenantContext);
        Services.AddSingleton<IAppDbContextFactory>(new Factory(this));
        Services.AddSingleton<TimeProvider>(Clock);
        Services.AddSingleton(Substitute.For<IFileStorage>());
        Services.AddSingleton(Substitute.For<INotificationService>());
        Services.AddSingleton(Substitute.For<ITenantStore>());
        Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new OrderOptions()));
        Services.AddDataProtection();
        Services.AddScoped<Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage.ProtectedLocalStorage>();
        Services.AddScoped<CartState>();
        Services.AddApplication();
    }

    protected AppDbContext CreateDb() => new(_dbOptions, TenantContext);

    protected ActorInfo Admin => TestData.TenantAdmin;

    protected async Task SeedProductsAsync(int count = TestData.BatchSize, int stock = 50)
    {
        await using var db = CreateDb();
        db.Products.AddRange(TestData.Products(count, Tenant.Id, stock));
        await db.SaveChangesAsync();
    }

    /// <summary>Crea pedidos reales vía CheckoutService (reservan stock).</summary>
    protected async Task<List<PlaceOrderResult>> SeedOrdersAsync(int count = TestData.BatchSize)
    {
        await using var db = CreateDb();
        var variants = await db.ProductVariants.AsNoTracking().Select(v => v.Id).ToListAsync();
        // Instancia directa: no resolver del contenedor bUnit antes de registrar la autorización.
        var checkout = new CheckoutService(new Factory(this), TenantContext, Clock,
            Microsoft.Extensions.Options.Options.Create(new OrderOptions()), Substitute.For<INotificationService>(),
            NullLogger<CheckoutService>.Instance);
        var results = new List<PlaceOrderResult>();
        for (var i = 0; i < count; i++)
        {
            results.Add(await checkout.PlaceOrderAsync(new PlaceOrderRequest
            {
                FullName = $"Cliente {i:000}", Email = $"cliente{i}@correo.com", Phone = "809", ShippingAddress = "Calle 1",
                Lines = [new CartLine(variants[i % variants.Count], 1)]
            }));
        }

        return results;
    }

    protected void AuthorizeAs(ActorInfo actor)
    {
        var auth = AddAuthorization();
        auth.SetAuthorized(actor.DisplayName);
        auth.SetRoles([.. actor.Roles]);
        auth.SetClaims(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, actor.UserId),
            new System.Security.Claims.Claim(CgShop.Infrastructure.Identity.CgClaimTypes.FullName, actor.DisplayName));
        auth.SetPolicies(PoliciesFor(actor));
    }

    private static string[] PoliciesFor(ActorInfo actor)
    {
        var policies = new List<string>();
        if (actor.IsTenantStaff) policies.Add(CgShop.Web.Policies.TenantStaff);
        if (actor.IsTenantAdmin) policies.Add(CgShop.Web.Policies.TenantAdmin);
        if (actor.IsInRole(Roles.SuperAdmin)) policies.Add(CgShop.Web.Policies.SuperAdmin);
        return [.. policies];
    }

    private sealed class Factory(StoreTestContext owner) : IAppDbContextFactory
    {
        public IAppDbContext CreateDbContext() => owner.CreateDb();
    }
}

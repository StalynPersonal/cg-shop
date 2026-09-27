using CgShop.Application.Orders;
using CgShop.Application.Tenancy;
using CgShop.Domain.Catalog;
using CgShop.Domain.Common;
using CgShop.Domain.Orders;
using CgShop.Domain.Tenants;
using CgShop.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CgShop.Infrastructure.Persistence;

/// <summary>
/// Datos iniciales de desarrollo: roles, Super Admin y dos tiendas demo (verde y roja)
/// con catálogo de las 5 categorías y algunas órdenes pendientes de validación.
/// Idempotente: si ya existen tenants no vuelve a sembrar.
/// </summary>
public sealed class DataSeeder(
    AppDbContext db,
    UserManager<ApplicationUser> users,
    RoleManager<IdentityRole> roles,
    IServiceScopeFactory scopeFactory,
    ILogger<DataSeeder> logger)
{
    public const string SuperAdminEmail = "superadmin@cgshop.local";
    public const string DemoPassword = "Admin123!";

    public async Task SeedAsync(bool migrate = true, CancellationToken ct = default)
    {
        if (migrate)
            await db.Database.MigrateAsync(ct);

        foreach (var role in Roles.All)
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole(role));

        // Por nombre de usuario (único global); el correo puede repetirse entre tiendas.
        if (await users.FindByNameAsync(SuperAdminEmail) is null)
            await CreateUserAsync(SuperAdminEmail, "Super Administrador", null, Roles.SuperAdmin);

        if (await db.Tenants.AnyAsync(ct))
        {
            await BackfillDemoContactAsync(ct);
            await BackfillDemoCustomersAsync(ct);
            await BackfillDemoAudiencesAsync(ct);
            return;
        }

        await SeedTenantAsync("Tienda Verde", "verde", "#2E7D32", "Banco Popular", ct);
        await SeedTenantAsync("Tienda Roja", "rojo", "#C62828", "Banco BHD", ct);
        logger.LogInformation("Datos demo creados: verde.localhost y rojo.localhost (clave {Password})", DemoPassword);
    }

    private async Task SeedTenantAsync(string name, string slug, string color, string bank, CancellationToken ct)
    {
        var tenant = Tenant.Create(name, slug, color, $"ventas@{slug}.local");
        tenant.UpdatePaymentSettings(new PaymentSettings
        {
            BankAccounts =
            [
                new BankAccount
                {
                    BankName = bank, AccountNumber = slug == "verde" ? "800-123456-7" : "900-765432-1",
                    AccountHolder = $"{name} SRL", AccountType = "Corriente", HolderDocument = "RNC 1-01-00000-1"
                }
            ],
            PaymentLinkUrl = $"https://pagos.ejemplo.com/{slug}",
            Instructions = "Envíe el comprobante indicando su número de orden. Validamos pagos en horario laborable.",
            WhatsAppNumber = slug == "verde" ? "+1 809 555 1234" : "+1 829 555 9876",
            PickupAddress = slug == "verde"
                ? "Av. Abraham Lincoln #100, Piantini, Santo Domingo (L-S 9:00-18:00)"
                : "C/ El Conde #50, Zona Colonial, Santo Domingo (L-S 10:00-19:00)"
        });
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(ct);

        await CreateUserAsync($"admin@{slug}.local", $"Dueño {name}", tenant.Id, Roles.TenantAdmin);
        await CreateUserAsync($"staff@{slug}.local", $"Empleado {name}", tenant.Id, Roles.TenantStaff);
        var customer = await CreateUserAsync(CustomerEmail(slug), $"Cliente {name}", tenant.Id, Roles.Customer,
            "809-555-0101");

        // Catálogo y órdenes dentro de un scope con el tenant activo (mismo camino que producción).
        await using var scope = scopeFactory.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(TenantInfo.From(tenant));
        var tenantDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var products = DemoCatalog.Build(slug).ToList();
        tenantDb.Products.AddRange(products);
        await tenantDb.SaveChangesAsync(ct);

        var checkout = scope.ServiceProvider.GetRequiredService<CheckoutService>();
        var variants = products.SelectMany(p => p.Variants).ToList();
        for (var i = 0; i < 3; i++)
        {
            await checkout.PlaceOrderAsync(new PlaceOrderRequest
            {
                CustomerUserId = customer.Id,
                FullName = customer.FullName,
                Email = customer.Email!,
                Phone = customer.PhoneNumber!,
                DeliveryMethod = i == 1 ? DeliveryMethod.Pickup : DeliveryMethod.Shipping,
                ShippingAddress = i == 1 ? null : $"Av. Winston Churchill #{10 + i}, Santo Domingo",
                PaymentMethod = i % 2 == 0 ? PaymentMethod.BankTransfer : PaymentMethod.PaymentLink,
                Lines = [new CartLine(variants[i * 3].Id, 1), new CartLine(variants[i * 3 + 1].Id, 2)]
            }, ct);
        }
    }

    /// <summary>Completa WhatsApp y dirección de retiro en las tiendas demo creadas antes de existir esos campos.</summary>
    private async Task BackfillDemoContactAsync(CancellationToken ct)
    {
        foreach (var tenant in await db.Tenants.Where(t => t.Slug == "verde" || t.Slug == "rojo").ToListAsync(ct))
        {
            var ps = tenant.PaymentSettings;
            if (ps.WhatsAppNumber is not null && ps.PickupAddress is not null)
                continue;
            tenant.UpdatePaymentSettings(new PaymentSettings
            {
                BankAccounts = ps.BankAccounts,
                PaymentLinkUrl = ps.PaymentLinkUrl,
                Instructions = ps.Instructions,
                WhatsAppNumber = ps.WhatsAppNumber ?? (tenant.Slug == "verde" ? "+1 809 555 1234" : "+1 829 555 9876"),
                PickupAddress = ps.PickupAddress ?? (tenant.Slug == "verde"
                    ? "Av. Abraham Lincoln #100, Piantini, Santo Domingo (L-S 9:00-18:00)"
                    : "C/ El Conde #50, Zona Colonial, Santo Domingo (L-S 10:00-19:00)")
            });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Asigna Hombre/Mujer/Niños a los productos demo creados antes de existir las secciones
    /// y agrega los productos infantiles que falten. Solo toca productos que siguen como Unisex.
    /// </summary>
    private async Task BackfillDemoAudiencesAsync(CancellationToken ct)
    {
        foreach (var tenant in await db.Tenants.Where(t => t.Slug == "verde" || t.Slug == "rojo").ToListAsync(ct))
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(TenantInfo.From(tenant));
            var tenantDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var existing = await tenantDb.Products.ToListAsync(ct);
            foreach (var product in existing.Where(p => p.Audience == ProductAudience.Unisex))
                product.SetAudience(DemoCatalog.AudienceFor(product.Name));

            var slugs = existing.Select(p => p.Slug).ToHashSet();
            tenantDb.Products.AddRange(DemoCatalog.Build(tenant.Slug)
                .Where(p => p.Audience == ProductAudience.Kids && !slugs.Contains(p.Slug)));
            await tenantDb.SaveChangesAsync(ct);
        }
    }

    public static string CustomerEmail(string slug) => $"cliente@{slug}.local";

    /// <summary>Crea el cliente demo en tiendas sembradas antes de existir las cuentas de cliente.</summary>
    private async Task BackfillDemoCustomersAsync(CancellationToken ct)
    {
        foreach (var tenant in await db.Tenants.Where(t => t.Slug == "verde" || t.Slug == "rojo").ToListAsync(ct))
        {
            var email = CustomerEmail(tenant.Slug);
            var normalized = users.NormalizeEmail(email);
            if (!await users.Users.AnyAsync(u => u.NormalizedEmail == normalized && u.TenantId == tenant.Id, ct))
                await CreateUserAsync(email, $"Cliente {tenant.Name}", tenant.Id, Roles.Customer, "809-555-0101");
        }
    }

    private async Task<ApplicationUser> CreateUserAsync(string email, string fullName, Guid? tenantId, string role,
        string? phone = null)
    {
        var user = new ApplicationUser
        {
            // Clientes: nombre de usuario único por tienda (mismo criterio que TenantUserService).
            UserName = role == Roles.Customer ? $"c.{tenantId:N}.{email}" : email,
            Email = email, EmailConfirmed = true, FullName = fullName, TenantId = tenantId, PhoneNumber = phone
        };
        var result = await users.CreateAsync(user, DemoPassword);
        if (!result.Succeeded)
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
        await users.AddToRoleAsync(user, role);
        return user;
    }
}

internal static class DemoCatalog
{
    public static IEnumerable<Product> Build(string prefix)
    {
        var p = prefix.ToUpperInvariant()[..2];

        var polo = Product.Create("Polo Clásico Algodón", ProductCategory.Clothing, "Lacoste",
            "Polo de algodón piqué, corte regular.", null, new Dictionary<string, string> { ["Material"] = "Algodón piqué" });
        foreach (var (size, i) in new[] { "S", "M", "L", "XL" }.Select((s, i) => (s, i)))
        {
            polo.AddVariant($"{p}-POLO-BL-{size}", 2450, 10 + i, size, "Blanco");
            polo.AddVariant($"{p}-POLO-NG-{size}", 2450, 8 + i, size, "Negro");
        }

        var jeans = Product.Create("Jeans Slim Fit", ProductCategory.Clothing, "Levi's",
            "Denim elástico de tiro medio.", null, new Dictionary<string, string> { ["Material"] = "Denim 98% algodón" });
        foreach (var size in new[] { "28", "30", "32", "34", "36" })
            jeans.AddVariant($"{p}-JEAN-AZ-{size}", 3200, 6, size, "Azul");

        var gorra = Product.Create("Gorra 9FORTY Yankees", ProductCategory.Caps, "New Era",
            "Gorra ajustable bordada.", null);
        gorra.AddVariant($"{p}-CAP-NG", 1850, 15, "Ajustable", "Negro");
        gorra.AddVariant($"{p}-CAP-AZ", 1850, 12, "Ajustable", "Azul marino");

        var gorraTrucker = Product.Create("Gorra Trucker Malla", ProductCategory.Caps, "Adidas", "Transpirable.", null);
        gorraTrucker.AddVariant($"{p}-TRK-SM", 1450, 9, "S/M", "Gris");
        gorraTrucker.AddVariant($"{p}-TRK-LX", 1450, 7, "L/XL", "Gris");

        var reloj = Product.Create("Reloj G-Shock GA-2100", ProductCategory.Watches, "Casio",
            "Resistente a golpes, 200 m.", null,
            new Dictionary<string, string> { ["Movimiento"] = "Cuarzo", ["Resistencia"] = "200 m", ["Caja"] = "45 mm" });
        reloj.AddVariant($"{p}-GSH-NG", 8900, 5, color: "Negro");
        reloj.AddVariant($"{p}-GSH-VE", 8900, 3, color: "Verde oliva");

        var relojAuto = Product.Create("Reloj Presage Automático", ProductCategory.Watches, "Seiko",
            "Esfera coctel, cristal zafiro.", null,
            new Dictionary<string, string> { ["Movimiento"] = "Automático 4R35", ["Resistencia"] = "50 m" });
        relojAuto.AddVariant($"{p}-PRS-AZ", 24500, 2, color: "Azul");

        var perfume = Product.Create("Sauvage Eau de Parfum", ProductCategory.Perfumes, "Dior",
            "Fragancia amaderada aromática.", null, new Dictionary<string, string>
            {
                ["NotasSalida"] = "Bergamota de Calabria", ["NotasCorazon"] = "Lavanda, Anís estrellado",
                ["NotasFondo"] = "Ambroxan, Vainilla", ["Concentracion"] = "EDP"
            });
        perfume.AddVariant($"{p}-SAU-60", 6900, 6, volumeMl: 60);
        perfume.AddVariant($"{p}-SAU-100", 9400, 4, volumeMl: 100);

        var perfume2 = Product.Create("Coco Mademoiselle", ProductCategory.Perfumes, "Chanel",
            "Oriental fresco femenino.", null, new Dictionary<string, string>
            {
                ["NotasSalida"] = "Naranja, Bergamota", ["NotasCorazon"] = "Rosa, Jazmín", ["NotasFondo"] = "Pachulí, Vetiver"
            });
        perfume2.AddVariant($"{p}-COC-50", 8200, 5, volumeMl: 50);
        perfume2.AddVariant($"{p}-COC-100", 11900, 3, volumeMl: 100);

        var tenis = Product.Create("Tenis Air Max 90", ProductCategory.Footwear, "Nike",
            "Amortiguación Air visible.", null, new Dictionary<string, string> { ["Material"] = "Cuero y malla" });
        foreach (var size in new[] { "38", "39", "40", "41", "42", "43", "44" })
        {
            tenis.AddVariant($"{p}-AM90-BL-{size}", 7800, 4, size, "Blanco");
            tenis.AddVariant($"{p}-AM90-NG-{size}", 7800, 3, size, "Negro");
        }

        var botas = Product.Create("Botas Chelsea Cuero", ProductCategory.Footwear, "Timberland", "Cuero genuino.", null);
        foreach (var size in new[] { "40", "41", "42", "43" })
            botas.AddVariant($"{p}-CHL-MR-{size}", 9800, 2, size, "Marrón");

        var camisetaNinos = Product.Create("Camiseta Infantil Dinosaurio", ProductCategory.Clothing, "Carter's",
            "Algodón suave para el día a día.", null, new Dictionary<string, string> { ["Material"] = "100% algodón" });
        foreach (var size in new[] { "4", "6", "8", "10" })
        {
            camisetaNinos.AddVariant($"{p}-DINO-AZ-{size}", 950, 6, size, "Azul");
            camisetaNinos.AddVariant($"{p}-DINO-VD-{size}", 950, 6, size, "Verde");
        }

        var tenisNinos = Product.Create("Tenis Infantiles Revolution", ProductCategory.Footwear, "Nike",
            "Ligeros y con cierre de velcro.", null);
        foreach (var size in new[] { "28", "29", "30", "31", "32", "33" })
            tenisNinos.AddVariant($"{p}-REVK-RS-{size}", 3900, 3, size, "Rosado");

        Product[] products = [polo, jeans, gorra, gorraTrucker, reloj, relojAuto, perfume, perfume2, tenis, botas,
            camisetaNinos, tenisNinos];
        foreach (var product in products)
            product.SetAudience(AudienceFor(product.Name));
        return products;
    }

    /// <summary>Público de cada producto demo; lo no listado es Unisex.</summary>
    public static ProductAudience AudienceFor(string name) => name switch
    {
        "Polo Clásico Algodón" or "Reloj Presage Automático" or "Sauvage Eau de Parfum" => ProductAudience.Men,
        "Coco Mademoiselle" or "Botas Chelsea Cuero" => ProductAudience.Women,
        "Camiseta Infantil Dinosaurio" or "Tenis Infantiles Revolution" => ProductAudience.Kids,
        _ => ProductAudience.Unisex
    };
}

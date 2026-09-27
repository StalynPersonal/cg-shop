using System.Net.Http.Headers;
using System.Net.Http.Json;
using CgShop.Api.Endpoints;
using CgShop.Domain.Common;
using CgShop.Domain.Tenants;
using CgShop.Infrastructure.Identity;
using CgShop.Infrastructure.Persistence;
using CgShop.IntegrationTests.Infrastructure;
using CgShop.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CgShop.IntegrationTests.Api;

/// <summary>Levanta CgShop.Api en memoria contra la base de pruebas del fixture.</summary>
public sealed class ApiFactory(SqlServerFixture fx) : WebApplicationFactory<Program>
{
    public const string RootDomain = "cgshop.test";
    public const string Password = "ClaveSegura1";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", fx.ConnectionString);
        builder.UseSetting("Jwt:Key", "clave-de-pruebas-de-integracion-cgshop-2026!");
        builder.UseSetting("Jobs:Enabled", "false");
        builder.UseSetting("Tenancy:RootDomains:0", RootDomain);
        builder.UseSetting("Tenancy:AllowHeaderFallback", "true");
        builder.UseSetting("FileStorage:RootPath", fx.UploadsPath);
    }

    /// <summary>Cliente de la tienda por subdominio (Host header) o por X-Tenant.</summary>
    public HttpClient ClientFor(string slug, bool viaHeader = false)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri(viaHeader ? $"http://api.other.io/" : $"http://{slug}.{RootDomain}/"),
            AllowAutoRedirect = false
        });
        if (viaHeader)
            client.DefaultRequestHeaders.Add("X-Tenant", slug);
        return client;
    }

    public HttpClient AdminHostClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://admin.{RootDomain}/") });

    public async Task<HttpClient> AuthenticatedClientAsync(string slug, string email)
    {
        var client = ClientFor(slug);
        var response = await client.PostAsJsonAsync("/api/auth/token", new LoginRequest(email, Password));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    /// <summary>Registra un cliente nuevo en la tienda y devuelve un cliente HTTP autenticado como él.</summary>
    public async Task<HttpClient> CustomerClientAsync(string slug, string? email = null)
    {
        var client = ClientFor(slug);
        var response = await client.PostAsJsonAsync("/api/auth/register", new CgShop.Infrastructure.Identity.RegisterCustomerRequest(
            "Cliente API", email ?? $"cliente-{Guid.NewGuid():N}@correo.com", "809-555-0000", Password));
        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    /// <summary>Crea un tenant con 'stockProducts' productos y usuarios admin@/staff@.</summary>
    public async Task<Tenant> CreateTenantAsync(int products = TestData.BatchSize, int stock = 100)
    {
        var tenant = await fx.CreateTenantAsync();
        await using var scope = fx.TenantScope(tenant);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Products.AddRange(TestData.Products(products, tenant.Id, stock));
        await db.SaveChangesAsync();

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        foreach (var (prefix, role) in new[] { ("admin", Roles.TenantAdmin), ("staff", Roles.TenantStaff) })
        {
            var user = new ApplicationUser
            {
                UserName = $"{prefix}@{tenant.Slug}.test", Email = $"{prefix}@{tenant.Slug}.test", EmailConfirmed = true,
                FullName = $"{prefix} {tenant.Slug}", TenantId = tenant.Id
            };
            (await users.CreateAsync(user, Password)).Succeeded.Should().BeTrue();
            await users.AddToRoleAsync(user, role);
        }

        return tenant;
    }
}

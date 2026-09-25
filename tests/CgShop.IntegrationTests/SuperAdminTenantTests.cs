using CgShop.Application.Common;
using CgShop.Application.Tenancy;
using CgShop.Application.Tenants;
using CgShop.Domain.Common;
using CgShop.Domain.Tenants;
using CgShop.Infrastructure.Identity;
using CgShop.IntegrationTests.Infrastructure;
using CgShop.Tests.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace CgShop.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class SuperAdminTenantTests(SqlServerFixture fx)
{
    private static CreateTenantRequest Request(string slug, string color) => new()
    {
        Name = $"Empresa {slug}",
        Slug = slug,
        PrimaryColor = color,
        ContactEmail = $"info@{slug}.com",
        AdminFullName = $"Dueño {slug}",
        AdminEmail = $"admin@{slug}.test",
        AdminPassword = "ClaveSegura1"
    };

    [Fact]
    public async Task Super_admin_registers_100_tenants_each_with_its_own_primary_color_and_admin()
    {
        var run = Guid.NewGuid().ToString("N")[..6];
        await using var scope = fx.NoTenantScope();
        var service = scope.ServiceProvider.GetRequiredService<SuperAdminTenantService>();
        var store = scope.ServiceProvider.GetRequiredService<ITenantStore>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var created = new List<(Guid Id, string Slug, string Color)>();
        for (var i = 1; i <= TestData.BatchSize; i++)
        {
            var slug = $"sa{run}-{i:000}";
            var color = TestData.HexColorFor(i);
            created.Add((await service.CreateAsync(Request(slug, color), TestData.SuperAdmin), slug, color));
        }

        foreach (var (id, slug, color) in created)
        {
            var info = await store.FindBySlugAsync(slug);
            info.Should().NotBeNull();
            info!.Id.Should().Be(id);
            info.PrimaryColor.Should().Be(color.ToUpperInvariant());

            var admin = await users.FindByEmailAsync($"admin@{slug}.test");
            admin!.TenantId.Should().Be(id);
            (await users.IsInRoleAsync(admin, Roles.TenantAdmin)).Should().BeTrue();
        }

        created.Select(c => c.Color).Distinct().Should().HaveCount(TestData.BatchSize);
    }

    [Theory]
    [InlineData("")]
    [InlineData("rojo")]
    [InlineData("#12")]
    public async Task Creating_a_tenant_without_valid_primary_color_fails(string color)
    {
        await using var scope = fx.NoTenantScope();
        var service = scope.ServiceProvider.GetRequiredService<SuperAdminTenantService>();

        await service.Invoking(s => s.CreateAsync(Request($"nc-{Guid.NewGuid():N}"[..12], color), TestData.SuperAdmin))
            .Should().ThrowAsync<ValidationException>().WithMessage("*color primario*");
    }

    [Fact]
    public async Task Only_super_admin_can_create_tenants()
    {
        await using var scope = fx.NoTenantScope();
        var service = scope.ServiceProvider.GetRequiredService<SuperAdminTenantService>();

        await service.Invoking(s => s.CreateAsync(Request("forbidden-x", "#00FF00"), TestData.TenantAdmin))
            .Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Duplicate_slug_and_failed_admin_provisioning_leave_no_tenant()
    {
        var slug = $"dup-{Guid.NewGuid():N}"[..14];
        await using var scope = fx.NoTenantScope();
        var service = scope.ServiceProvider.GetRequiredService<SuperAdminTenantService>();
        await service.CreateAsync(Request(slug, "#123456"), TestData.SuperAdmin);

        await service.Invoking(s => s.CreateAsync(Request(slug, "#654321"), TestData.SuperAdmin))
            .Should().ThrowAsync<ValidationException>().WithMessage("*ya está en uso*");

        // Mismo email de admin => Identity falla => el tenant recién creado se compensa (se elimina).
        var other = $"cmp-{Guid.NewGuid():N}"[..14];
        var req = Request(other, "#111111");
        req.AdminEmail = $"admin@{slug}.test";
        await service.Invoking(s => s.CreateAsync(req, TestData.SuperAdmin)).Should().ThrowAsync<ValidationException>();
        (await scope.ServiceProvider.GetRequiredService<ITenantStore>().FindBySlugAsync(other)).Should().BeNull();
    }

    [Fact]
    public async Task Branding_update_and_suspension_invalidate_the_cache()
    {
        var slug = $"br-{Guid.NewGuid():N}"[..14];
        await using var scope = fx.NoTenantScope();
        var service = scope.ServiceProvider.GetRequiredService<SuperAdminTenantService>();
        var store = scope.ServiceProvider.GetRequiredService<ITenantStore>();
        var id = await service.CreateAsync(Request(slug, "#00AA00"), TestData.SuperAdmin);
        (await store.FindBySlugAsync(slug))!.PrimaryColor.Should().Be("#00AA00");

        await service.UpdateBrandingAsync(id, new UpdateBrandingRequest
        {
            Name = "Nuevo nombre", PrimaryColor = "#0000FF", TaxRate = 0.16m
        }, TestData.SuperAdmin);
        (await store.FindBySlugAsync(slug))!.PrimaryColor.Should().Be("#0000FF");

        await service.SetStatusAsync(id, TenantStatus.Suspended, TestData.SuperAdmin);
        (await store.FindBySlugAsync(slug))!.IsActive.Should().BeFalse();

        var summary = await service.GetAsync(id);
        summary!.Name.Should().Be("Nuevo nombre");
        summary.TaxRate.Should().Be(0.16m);
    }

    [Fact]
    public async Task Demo_seed_created_green_and_red_stores()
    {
        await using var scope = fx.NoTenantScope();
        var store = scope.ServiceProvider.GetRequiredService<ITenantStore>();
        (await store.FindBySlugAsync("verde"))!.PrimaryColor.Should().Be("#2E7D32");
        (await store.FindBySlugAsync("rojo"))!.PrimaryColor.Should().Be("#C62828");
    }
}

using CgShop.Application.Common;
using CgShop.Application.Orders;
using CgShop.Application.Tenancy;
using CgShop.Domain.Common;
using CgShop.Domain.Orders;
using CgShop.Infrastructure.Identity;
using CgShop.Infrastructure.Persistence;
using CgShop.IntegrationTests.Infrastructure;
using CgShop.Tests.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace CgShop.IntegrationTests;

/// <summary>Cuentas de cliente por tienda: registro, login aislado y "Mis pedidos".</summary>
[Collection(SqlServerCollection.Name)]
public sealed class CustomerAccountTests(SqlServerFixture fx)
{
    private const string Password = "ClaveSegura1";

    private static RegisterCustomerRequest Customer(int i, string run) =>
        new($"Cliente {i}", $"cliente{i}.{run}@correo.com", $"809-555-{i:0000}", Password);

    [Fact]
    public async Task Registers_100_customers_and_same_emails_are_allowed_in_another_store()
    {
        var run = Guid.NewGuid().ToString("N")[..6];
        var storeA = await fx.CreateTenantAsync();
        var storeB = await fx.CreateTenantAsync();

        foreach (var store in new[] { storeA, storeB })
        {
            await using var scope = fx.TenantScope(store);
            var service = scope.ServiceProvider.GetRequiredService<TenantUserService>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            for (var i = 0; i < TestData.BatchSize; i++)
            {
                var user = await service.RegisterCustomerAsync(Customer(i, run), scope.ServiceProvider.GetRequiredService<ITenantContext>());
                user.TenantId.Should().Be(store.Id);
                (await users.IsInRoleAsync(user, Roles.Customer)).Should().BeTrue();
                (await users.CheckPasswordAsync(user, Password)).Should().BeTrue();
            }
        }
    }

    [Fact]
    public async Task Duplicate_email_in_same_store_and_weak_password_are_rejected_in_spanish()
    {
        var run = Guid.NewGuid().ToString("N")[..6];
        var store = await fx.CreateTenantAsync();
        await using var scope = fx.TenantScope(store);
        var service = scope.ServiceProvider.GetRequiredService<TenantUserService>();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();

        await service.RegisterCustomerAsync(Customer(1, run), tenant);
        await service.Invoking(s => s.RegisterCustomerAsync(Customer(1, run), tenant))
            .Should().ThrowAsync<ValidationException>().WithMessage("*Ya existe una cuenta*");

        var weak = Customer(2, run) with { Password = "solominusculas" };
        var ex = await service.Invoking(s => s.RegisterCustomerAsync(weak, tenant)).Should().ThrowAsync<ValidationException>();
        ex.Which.Errors.Should().Contain(e => e.Contains("mayúscula")).And.Contain(e => e.Contains("número"));
    }

    [Fact]
    public async Task Login_lookup_only_finds_users_of_the_current_store()
    {
        var run = Guid.NewGuid().ToString("N")[..6];
        var storeA = await fx.CreateTenantAsync();
        var storeB = await fx.CreateTenantAsync();

        await using (var scopeA = fx.TenantScope(storeA))
        {
            var tenant = scopeA.ServiceProvider.GetRequiredService<ITenantContext>();
            var service = scopeA.ServiceProvider.GetRequiredService<TenantUserService>();
            for (var i = 0; i < TestData.BatchSize; i++)
                await service.RegisterCustomerAsync(Customer(i, run), tenant);

            for (var i = 0; i < TestData.BatchSize; i++)
                (await service.FindForLoginAsync(Customer(i, run).Email.ToUpperInvariant(), tenant))!.TenantId.Should().Be(storeA.Id);
        }

        await using (var scopeB = fx.TenantScope(storeB))
        {
            var service = scopeB.ServiceProvider.GetRequiredService<TenantUserService>();
            var tenant = scopeB.ServiceProvider.GetRequiredService<ITenantContext>();
            for (var i = 0; i < TestData.BatchSize; i++)
                (await service.FindForLoginAsync(Customer(i, run).Email, tenant)).Should().BeNull();
        }

        await using var admin = fx.NoTenantScope();
        admin.ServiceProvider.GetRequiredService<TenantContext>().SetAdminHost();
        var adminService = admin.ServiceProvider.GetRequiredService<TenantUserService>();
        var adminTenant = admin.ServiceProvider.GetRequiredService<ITenantContext>();
        (await adminService.FindForLoginAsync(DataSeeder.SuperAdminEmail, adminTenant)).Should().NotBeNull();
        (await adminService.FindForLoginAsync(Customer(0, run).Email, adminTenant)).Should().BeNull();
    }

    [Fact]
    public async Task My_orders_returns_only_orders_of_each_registered_customer()
    {
        var run = Guid.NewGuid().ToString("N")[..6];
        var store = await fx.CreateTenantAsync();
        await using var scope = fx.TenantScope(store);
        var sp = scope.ServiceProvider;
        var tenant = sp.GetRequiredService<ITenantContext>();
        var db = sp.GetRequiredService<AppDbContext>();
        db.Products.AddRange(TestData.Products(10, store.Id, stockPerVariant: 1000));
        await db.SaveChangesAsync();
        var variant = db.ProductVariants.First().Id;

        var customers = new List<ApplicationUser>();
        for (var c = 0; c < 5; c++)
            customers.Add(await sp.GetRequiredService<TenantUserService>().RegisterCustomerAsync(Customer(c, run), tenant));

        var checkout = sp.GetRequiredService<CheckoutService>();
        for (var i = 0; i < TestData.BatchSize; i++)
        {
            var customer = customers[i % customers.Count];
            await checkout.PlaceOrderAsync(new PlaceOrderRequest
            {
                CustomerUserId = customer.Id, FullName = customer.FullName, Email = customer.Email!,
                Phone = customer.PhoneNumber!, DeliveryMethod = DeliveryMethod.Pickup,
                Lines = [new CartLine(variant, 1)]
            });
        }

        var orders = sp.GetRequiredService<CustomerOrderService>();
        foreach (var customer in customers)
        {
            var mine = await orders.ListMineAsync(customer.Id, 1, 100);
            mine.TotalCount.Should().Be(20);
            mine.Items.Should().BeInDescendingOrder(o => o.CreatedAtUtc);
        }
    }
}

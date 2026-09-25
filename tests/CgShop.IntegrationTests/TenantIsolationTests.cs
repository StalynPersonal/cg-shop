using CgShop.Application.Catalog;
using CgShop.Domain.Catalog;
using CgShop.Domain.Tenants;
using CgShop.Infrastructure.Persistence;
using CgShop.IntegrationTests.Infrastructure;
using CgShop.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CgShop.IntegrationTests;

/// <summary>Aislamiento estricto por TenantId a nivel de base de datos (SQL Server real).</summary>
[Collection(SqlServerCollection.Name)]
public sealed class TenantIsolationTests(SqlServerFixture fx) : IAsyncLifetime
{
    private Tenant _a = null!;
    private Tenant _b = null!;

    public async Task InitializeAsync()
    {
        _a = await fx.CreateTenantAsync(color: "#2E7D32");
        _b = await fx.CreateTenantAsync(color: "#C62828");
        await SeedProductsAsync(_a, "A");
        await SeedProductsAsync(_b, "B");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task SeedProductsAsync(Tenant tenant, string prefix)
    {
        await using var scope = fx.TenantScope(tenant);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var product in TestData.Products())
        {
            // TenantId vacío: lo asigna el DbContext a partir del tenant activo.
            var copy = Product.Create($"{prefix} {product.Name}", product.Category, product.Brand);
            foreach (var v in product.Variants)
                copy.AddVariant($"{prefix}-{v.Sku}", v.Price, v.StockOnHand, v.Size, v.Color, v.VolumeMl);
            db.Products.Add(copy);
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Each_tenant_sees_only_its_own_100_products()
    {
        foreach (var (tenant, prefix) in new[] { (_a, "A "), (_b, "B ") })
        {
            await using var scope = fx.TenantScope(tenant);
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var products = await db.Products.Include(p => p.Variants).ToListAsync();

            products.Should().HaveCount(TestData.BatchSize);
            products.Should().AllSatisfy(p =>
            {
                p.TenantId.Should().Be(tenant.Id);
                p.Name.Should().StartWith(prefix);
                p.Variants.Should().AllSatisfy(v => v.TenantId.Should().Be(tenant.Id));
            });
        }
    }

    [Fact]
    public async Task Catalog_service_returns_only_active_tenant_data()
    {
        await using var scope = fx.TenantScope(_a);
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogService>();

        var page = await catalog.SearchAsync(new CatalogQuery(PageSize: 200));

        page.TotalCount.Should().Be(TestData.BatchSize);
        page.Items.Should().OnlyContain(p => p.Name.StartsWith("A "));
    }

    [Fact]
    public async Task Without_tenant_no_rows_are_returned()
    {
        await using var scope = fx.NoTenantScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        (await db.Products.CountAsync()).Should().Be(0);
        (await db.ProductVariants.CountAsync()).Should().Be(0);
        (await db.Orders.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Entity_of_another_tenant_is_invisible_even_by_id()
    {
        Guid productOfB;
        await using (var scopeB = fx.TenantScope(_b))
            productOfB = await scopeB.ServiceProvider.GetRequiredService<AppDbContext>().Products.Select(p => p.Id).FirstAsync();

        await using var scopeA = fx.TenantScope(_a);
        var dbA = scopeA.ServiceProvider.GetRequiredService<AppDbContext>();
        (await dbA.Products.FirstOrDefaultAsync(p => p.Id == productOfB)).Should().BeNull();
        (await dbA.Products.FindAsync(productOfB)).Should().BeNull();
        (await scopeA.ServiceProvider.GetRequiredService<CatalogService>().GetByIdAsync(productOfB)).Should().BeNull();
    }

    [Fact]
    public async Task IgnoreQueryFilters_sees_all_tenants()
    {
        await using var scope = fx.NoTenantScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var counts = await db.Products.IgnoreQueryFilters()
            .Where(p => p.TenantId == _a.Id || p.TenantId == _b.Id)
            .GroupBy(p => p.TenantId).Select(g => g.Count()).ToListAsync();

        counts.Should().BeEquivalentTo([TestData.BatchSize, TestData.BatchSize]);
    }

    [Fact]
    public async Task Updating_rows_of_another_tenant_is_blocked()
    {
        await using var scope = fx.TenantScope(_a);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var foreign = await db.Products.IgnoreQueryFilters().Include(p => p.Variants)
            .Where(p => p.TenantId == _b.Id).Take(TestData.BatchSize).ToListAsync();
        foreign.Should().HaveCount(TestData.BatchSize);

        foreach (var product in foreign)
        {
            product.Deactivate();
            var act = () => db.SaveChangesAsync();
            await act.Should().ThrowAsync<CrossTenantWriteException>();
            db.Entry(product).State = EntityState.Unchanged;
        }
    }

    [Fact]
    public async Task Inserting_with_foreign_tenant_id_is_blocked()
    {
        await using var scope = fx.TenantScope(_a);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var product = Product.Create("Intruso", ProductCategory.Watches);
        product.TenantId = _b.Id;
        db.Products.Add(product);

        await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowAsync<CrossTenantWriteException>();
    }

    [Fact]
    public async Task Inserting_without_tenant_is_blocked()
    {
        await using var scope = fx.NoTenantScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Products.Add(Product.Create("Huérfano", ProductCategory.Caps));

        await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowAsync<CrossTenantWriteException>();
    }

    [Fact]
    public async Task Same_sku_can_exist_in_different_tenants_but_not_twice_in_one()
    {
        await using (var scope = fx.TenantScope(_a))
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var p = Product.Create("Compartido", ProductCategory.Watches);
            p.AddVariant("SKU-COMPARTIDO", 100, 1, color: "Negro");
            db.Products.Add(p);
            await db.SaveChangesAsync();
        }

        await using (var scope = fx.TenantScope(_b))
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var p = Product.Create("Compartido", ProductCategory.Watches);
            p.AddVariant("SKU-COMPARTIDO", 100, 1, color: "Negro");
            db.Products.Add(p);
            await db.SaveChangesAsync(); // índice único (TenantId, Sku) lo permite
        }

        await using (var scope = fx.TenantScope(_a))
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var dup = Product.Create("Duplicado", ProductCategory.Watches);
            dup.AddVariant("SKU-COMPARTIDO", 100, 1, color: "Negro");
            db.Products.Add(dup);
            await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
        }
    }
}

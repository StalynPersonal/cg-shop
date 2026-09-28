using CgShop.Application.Catalog;
using CgShop.Application.Common;
using CgShop.Application.Orders;
using CgShop.Domain.Catalog;
using CgShop.Domain.Common;
using CgShop.Domain.Orders;
using CgShop.Tests.Shared;
using CgShop.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CgShop.UnitTests.Application;

/// <summary>Productos destacados a mano en "Productos en tendencia" y "Novedades" (100 productos).</summary>
public class FeaturedTests : IAsyncLifetime
{
    private readonly InMemoryDb _db = new();
    private readonly FakeTimeProvider _clock = new(TestData.Now);
    private InMemoryDb.Factory _factory = null!;
    private FeaturedService _featured = null!;
    private CatalogService _catalog = null!;
    private ProductAdminService _products = null!;
    private List<Product> _seed = null!;

    public async Task InitializeAsync()
    {
        _factory = _db.For(await _db.AddTenantAsync());
        _seed = TestData.Products(TestData.BatchSize, _factory.Context.Tenant!.Id, stockPerVariant: 50).ToList();
        await using (var ctx = _factory.CreateAppDbContext())
        {
            ctx.Products.AddRange(_seed);
            await ctx.SaveChangesAsync();
        }

        _featured = new FeaturedService(_factory, _clock);
        _catalog = new CatalogService(_factory);
        _products = new ProductAdminService(_factory, _factory.Context, Substitute.For<IFileStorage>(), _clock,
            NullLogger<ProductAdminService>.Instance);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task FeatureAsync(FeaturedSection section, params int[] indexes)
    {
        foreach (var i in indexes)
            await _featured.SetAsync(_seed[i].Id, section, true, TestData.TenantAdmin);
    }

    [Fact]
    public async Task Up_to_12_per_section_in_the_chosen_order_with_move_and_remove()
    {
        int[] chosen = [90, 5, 42, 17, 63, 8, 71, 30, 55, 12, 99, 1];
        await FeatureAsync(FeaturedSection.Trending, chosen);

        var list = await _featured.ListAsync(FeaturedSection.Trending, TestData.TenantAdmin);
        list.Select(f => f.ProductId).Should().Equal(chosen.Select(i => _seed[i].Id));
        list.Select(f => f.Rank).Should().Equal(Enumerable.Range(0, 12));

        await _featured.Invoking(s => s.SetAsync(_seed[2].Id, FeaturedSection.Trending, true, TestData.TenantAdmin))
            .Should().ThrowAsync<DomainException>().WithMessage("*12 productos destacados*");
        // La otra sección es independiente.
        await _featured.SetAsync(_seed[2].Id, FeaturedSection.NewArrivals, true, TestData.TenantAdmin);

        await _featured.MoveAsync(_seed[42].Id, FeaturedSection.Trending, -1, TestData.TenantAdmin);
        await _featured.SetAsync(_seed[90].Id, FeaturedSection.Trending, false, TestData.TenantAdmin);
        await _featured.SetAsync(_seed[5].Id, FeaturedSection.Trending, true, TestData.TenantAdmin); // ya estaba: no cambia

        list = await _featured.ListAsync(FeaturedSection.Trending, TestData.TenantAdmin);
        list.Select(f => f.ProductId).Take(3).Should().Equal(_seed[42].Id, _seed[5].Id, _seed[17].Id);
        list.Select(f => f.Rank).Should().Equal(Enumerable.Range(0, 11), "el orden queda continuo al quitar");

        (await _featured.GetStatusAsync(_seed[2].Id)).Should().Be((false, true));
        var history = await _products.GetHistoryAsync(_seed[90].Id, TestData.TenantAdmin);
        history.Select(h => h.Type).Should().Equal(ProductChangeType.Unfeatured, ProductChangeType.Featured);
        history[0].Details.Should().Be("Productos en tendencia");
    }

    [Fact]
    public async Task Trending_shows_featured_first_then_best_sellers_then_newest_without_repeats()
    {
        // Ventas: el producto 3 vende más que el 4.
        var notifications = Substitute.For<INotificationService>();
        var checkout = new CheckoutService(_factory, _factory.Context, _clock, Options.Create(new OrderOptions()),
            notifications, NullLogger<CheckoutService>.Instance);
        var orders = new OrderAdminService(_factory, _factory.Context, Substitute.For<IFileStorage>(), _clock,
            notifications, NullLogger<OrderAdminService>.Instance);
        foreach (var (index, qty) in new[] { (3, 5), (4, 2), (10, 1) })
        {
            var r = await checkout.PlaceOrderAsync(new PlaceOrderRequest
            {
                CustomerUserId = "c", FullName = "C", Email = "c@c.com", Phone = "809", ShippingAddress = "Calle",
                PaymentMethod = PaymentMethod.BankTransfer, Lines = [new CartLine(_seed[index].Variants[0].Id, qty)]
            });
            await orders.ValidatePaymentAsync(r.OrderId, TestData.TenantAdmin, null);
        }

        await FeatureAsync(FeaturedSection.Trending, 77, 10, 20); // el 10 también se vendió: no debe repetirse
        await _products.SetActiveAsync(_seed[20].Id, false, TestData.TenantAdmin); // destacado pero inactivo: no aparece

        var trending = await _catalog.GetTrendingAsync(_clock.GetUtcNow().UtcDateTime, take: 12);

        trending.Select(c => c.Id).Take(4).Should().Equal(_seed[77].Id, _seed[10].Id, _seed[3].Id, _seed[4].Id);
        trending.Should().HaveCount(12).And.OnlyHaveUniqueItems(c => c.Id);
        trending.Select(c => c.Id).Should().NotContain(_seed[20].Id);
    }

    [Fact]
    public async Task New_arrivals_show_featured_first_then_the_newest()
    {
        await FeatureAsync(FeaturedSection.NewArrivals, 0, 1); // los más viejos
        var newest = (await _catalog.SearchAsync(new CatalogQuery(PageSize: 6))).Items.Select(c => c.Id).ToList();

        var arrivals = await _catalog.GetNewArrivalsAsync(8);

        arrivals.Select(c => c.Id).Should().Equal([_seed[0].Id, _seed[1].Id, .. newest]);
    }

    [Fact]
    public async Task Only_the_owner_manages_featured_products()
    {
        await _featured.Invoking(s => s.SetAsync(_seed[0].Id, FeaturedSection.Trending, true, TestData.TenantStaff))
            .Should().ThrowAsync<ForbiddenException>();
        await _featured.Invoking(s => s.ListAsync(FeaturedSection.Trending, TestData.TenantStaff))
            .Should().ThrowAsync<ForbiddenException>();
        await _featured.Invoking(s => s.MoveAsync(_seed[0].Id, FeaturedSection.Trending, 1, TestData.TenantAdmin))
            .Should().ThrowAsync<NotFoundException>();
    }
}

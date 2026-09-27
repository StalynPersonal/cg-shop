using CgShop.Application.Catalog;
using CgShop.Application.Common;
using CgShop.Application.Orders;
using CgShop.Domain.Catalog;
using CgShop.Domain.Common;
using CgShop.Domain.Orders;
using CgShop.Tests.Shared;
using CgShop.UnitTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace CgShop.UnitTests.Application;

/// <summary>
/// Cambio de categoría: entre categorías compatibles se conservan las variantes; si no, se eliminan (con aviso),
/// el stock eliminado queda en Movimientos, los pedidos conservan su copia y todo queda en el historial.
/// </summary>
public class CategoryChangeTests : IAsyncLifetime
{
    private readonly InMemoryDb _db = new();
    private readonly FakeTimeProvider _clock = new(TestData.Now);
    private InMemoryDb.Factory _factory = null!;
    private ProductAdminService _products = null!;
    private List<Product> _seed = null!;

    public async Task InitializeAsync()
    {
        _factory = _db.For(await _db.AddTenantAsync());
        _products = new ProductAdminService(_factory, _factory.Context, Substitute.For<IFileStorage>(), _clock,
            NullLogger<ProductAdminService>.Instance);
        _seed = TestData.Products(TestData.BatchSize, _factory.Context.Tenant!.Id, stockPerVariant: 20).ToList();
        await using var ctx = _factory.CreateAppDbContext();
        ctx.Products.AddRange(_seed);
        await ctx.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Destino de prueba: compatible si existe otra categoría con las mismas variantes; si no, Ropa.</summary>
    private static ProductCategory TargetFor(ProductCategory from) => from switch
    {
        ProductCategory.Clothing => ProductCategory.Footwear,     // talla + color → compatible
        ProductCategory.Caps => ProductCategory.Clothing,         // compatible
        ProductCategory.Footwear => ProductCategory.Caps,         // compatible
        ProductCategory.Watches => ProductCategory.Electronics,   // solo color → compatible
        _ => ProductCategory.Clothing                             // Perfumes (ml) → incompatible
    };

    private async Task<(CheckoutService Checkout, OrderAdminService Orders)> OrderServicesAsync()
    {
        await Task.CompletedTask;
        var notifications = Substitute.For<INotificationService>();
        return (new CheckoutService(_factory, _factory.Context, _clock, Options.Create(new OrderOptions()), notifications,
                NullLogger<CheckoutService>.Instance),
            new OrderAdminService(_factory, _factory.Context, Substitute.For<IFileStorage>(), _clock, notifications,
                NullLogger<OrderAdminService>.Instance));
    }

    private static PlaceOrderRequest Order(Guid variantId) => new()
    {
        CustomerUserId = "cliente", FullName = "Cliente", Email = "c@correo.com", Phone = "809",
        ShippingAddress = "Calle 1", PaymentMethod = PaymentMethod.BankTransfer, Lines = [new CartLine(variantId, 1)]
    };

    [Fact]
    public async Task Changes_category_of_100_products_keeping_or_removing_variants_as_appropriate()
    {
        // Pedidos pagados de todos los productos: deben conservarse tal cual.
        var (checkout, orders) = await OrderServicesAsync();
        var paid = new List<(Guid OrderId, OrderItemDto Item)>();
        foreach (var p in _seed)
        {
            var r = await checkout.PlaceOrderAsync(Order(p.Variants[0].Id));
            await orders.ValidatePaymentAsync(r.OrderId, TestData.TenantAdmin, null);
            paid.Add((r.OrderId, (await orders.GetAsync(r.OrderId)).Items.Single()));
        }

        foreach (var p in _seed)
        {
            var to = TargetFor(p.Category);
            var compatible = p.Category.HasSameVariantsAs(to);
            var (dto, variantsBefore) = await _products.GetForEditAsync(p.Id);

            var preview = await _products.PreviewCategoryChangeAsync(p.Id, to, TestData.TenantAdmin);
            preview.KeepsVariants.Should().Be(compatible);
            preview.VariantCount.Should().Be(variantsBefore.Count);
            preview.UnitsInStock.Should().Be(variantsBefore.Sum(v => v.Available));
            preview.Blocked.Should().BeFalse();

            await _products.ChangeCategoryAsync(p.Id, to, TestData.TenantAdmin);

            var (after, variantsAfter) = await _products.GetForEditAsync(p.Id);
            after.Category.Should().Be(to);
            if (compatible)
                variantsAfter.Select(v => (v.Sku, v.Price, v.Available))
                    .Should().Equal(variantsBefore.Select(v => (v.Sku, v.Price, v.Available)));
            else
                variantsAfter.Should().BeEmpty();

            var history = await _products.GetHistoryAsync(p.Id, TestData.TenantAdmin);
            history[0].Type.Should().Be(ProductChangeType.CategoryChanged);
            history[0].UserName.Should().Be(TestData.TenantAdmin.DisplayName);
            history[0].Details.Should().StartWith($"{p.Category.DisplayName()} → {to.DisplayName()}")
                .And.Contain(compatible ? "Se conservaron" : "Se eliminaron");
        }

        await using var ctx = _factory.CreateAppDbContext();
        var removals = await ctx.StockMovements.Where(m => m.Reason!.StartsWith("Cambio de categoría")).ToListAsync();
        var perfumes = _seed.Where(p => p.Category == ProductCategory.Perfumes).ToList();
        removals.Select(m => m.ProductId).Distinct().Should().BeEquivalentTo(perfumes.Select(p => p.Id),
            "solo los incompatibles (Perfumes) eliminan variantes y dejan registro de su stock");
        removals.Should().OnlyContain(m => m.Quantity < 0 && m.StockAfter == 0 && m.Reason == "Cambio de categoría: Perfumes → Ropa");

        // Todos los pedidos pagados siguen iguales.
        foreach (var (orderId, item) in paid)
            (await orders.GetAsync(orderId)).Items.Single().Should().BeEquivalentTo(item);
    }

    [Fact]
    public async Task Pending_orders_block_only_changes_that_remove_variants()
    {
        var (checkout, _) = await OrderServicesAsync();
        var perfume = _seed.First(p => p.Category == ProductCategory.Perfumes);
        var shirt = _seed.First(p => p.Category == ProductCategory.Clothing);
        await checkout.PlaceOrderAsync(Order(perfume.Variants[0].Id));
        await checkout.PlaceOrderAsync(Order(shirt.Variants[0].Id));

        var preview = await _products.PreviewCategoryChangeAsync(perfume.Id, ProductCategory.Watches, TestData.TenantAdmin);
        preview.Blocked.Should().BeTrue();
        preview.PendingOrders.Should().Be(1);
        await _products.Invoking(s => s.ChangeCategoryAsync(perfume.Id, ProductCategory.Watches, TestData.TenantAdmin))
            .Should().ThrowAsync<DomainException>().WithMessage("*1 pedido(s) pendiente(s) de pago*");

        // Compatible: las variantes (y su reserva) se conservan, así que no hay nada que bloquear.
        await _products.ChangeCategoryAsync(shirt.Id, ProductCategory.Footwear, TestData.TenantAdmin);
        (await _products.GetForEditAsync(shirt.Id)).Product.Category.Should().Be(ProductCategory.Footwear);
    }

    [Fact]
    public async Task Removing_variants_turns_color_photos_into_general_ones()
    {
        var watch = _seed.First(p => p.Category == ProductCategory.Watches);
        await using (var ctx = _factory.CreateAppDbContext())
        {
            var product = await ctx.Products.Include(p => p.Variants).Include(p => p.Images).FirstAsync(p => p.Id == watch.Id);
            var image = product.AddImage("products/x.jpg", "image/jpeg", 100, 800, 600, "x.jpg", 10, TestData.Now);
            ctx.ProductImages.Add(image);
            product.SetImageColor(image.Id, product.Variants[0].Color);
            await ctx.SaveChangesAsync();
        }

        await _products.ChangeCategoryAsync(watch.Id, ProductCategory.Perfumes, TestData.TenantAdmin);

        await using var check = _factory.CreateAppDbContext();
        (await check.ProductImages.Where(i => i.ProductId == watch.Id).Select(i => i.Color).SingleAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Only_the_owner_can_change_and_the_category_must_differ()
    {
        var p = _seed[0];
        await _products.Invoking(s => s.ChangeCategoryAsync(p.Id, ProductCategory.Caps, TestData.TenantStaff))
            .Should().ThrowAsync<ForbiddenException>();
        await _products.Invoking(s => s.PreviewCategoryChangeAsync(p.Id, ProductCategory.Caps, TestData.TenantStaff))
            .Should().ThrowAsync<ForbiddenException>();
        await _products.Invoking(s => s.ChangeCategoryAsync(p.Id, p.Category, TestData.TenantAdmin))
            .Should().ThrowAsync<DomainException>().WithMessage("*ya está en esa categoría*");

        ProductCategory.Clothing.HasSameVariantsAs(ProductCategory.Phones).Should().BeTrue("talla/capacidad + color");
        ProductCategory.Perfumes.HasSameVariantsAs(ProductCategory.Clothing).Should().BeFalse();
    }
}

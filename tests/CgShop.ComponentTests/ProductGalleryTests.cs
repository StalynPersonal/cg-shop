using Bunit;
using CgShop.Application.Catalog;
using CgShop.Domain.Catalog;
using CgShop.Tests.Shared;
using CgShop.Web.Components.Pages.Admin;
using CgShop.Web.Components.Shared;
using Microsoft.EntityFrameworkCore;

namespace CgShop.ComponentTests;

public sealed class ProductGalleryTests : MudTestContext
{
    private static List<ProductImageDto> Images(int count) =>
        Enumerable.Range(0, count).Select(i =>
            new ProductImageDto(Guid.NewGuid(), $"/imagenes/{i}", i, 800, 600, 1000, $"f{i}.jpg")).ToList();

    [Fact]
    public void Shows_main_photo_and_thumbnails_and_switches_on_click()
    {
        var images = Images(10);
        var cut = Render<ProductGallery>(p => p.Add(x => x.Images, images).Add(x => x.AltText, "Tenis"));

        cut.Find("[data-testid='gallery-main']").GetAttribute("src").Should().Be("/imagenes/0");
        cut.FindAll("[data-testid='gallery-thumb']").Should().HaveCount(10);

        cut.FindAll("[data-testid='gallery-thumb']")[7].Click();
        cut.Find("[data-testid='gallery-main']").GetAttribute("src").Should().Be("/imagenes/7");
    }

    [Fact]
    public void Single_photo_has_no_thumbnails_and_no_photos_shows_fallback()
    {
        Render<ProductGallery>(p => p.Add(x => x.Images, Images(1)))
            .FindAll("[data-testid='gallery-thumb']").Should().BeEmpty();

        var external = Render<ProductGallery>(p => p.Add(x => x.Images, []).Add(x => x.FallbackUrl, "https://cdn/x.jpg"));
        external.Markup.Should().Contain("https://cdn/x.jpg");

        var icon = Render<ProductGallery>(p => p.Add(x => x.Images, []).Add(x => x.Category, ProductCategory.Watches));
        icon.Markup.Should().Contain("cg-product-image");
    }
}

/// <summary>Editor de fotos del panel con fotos reales guardadas (100 fotos en 10 productos).</summary>
public sealed class ProductGalleryEditorTests : StoreTestContext
{
    [Fact]
    public async Task Editor_lists_photos_marks_main_and_shows_limits()
    {
        await SeedProductsAsync(10);
        await using (var db = CreateDb())
        {
            var products = await db.Products.Include(p => p.Images).ToListAsync();
            for (var i = 0; i < TestData.BatchSize; i++)
            {
                var product = products[i % products.Count];
                db.ProductImages.Add(product.AddImage($"products/{i}.jpg", "image/jpeg", 2048, 800, 600, $"f{i}.jpg", 10,
                    DateTime.UtcNow));
            }

            await db.SaveChangesAsync();
        }

        Guid productId;
        await using (var db = CreateDb())
            productId = await db.Products.Select(p => p.Id).FirstAsync();

        AuthorizeAs(TestData.TenantStaff);
        var cut = Render<ProductGalleryEditor>(p => p.AddCascadingValue(HostContext).Add(x => x.ProductId, productId));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='gallery-item']").Should().HaveCount(10));
        cut.FindAll("[data-testid='main-photo']").Should().ContainSingle();
        cut.Markup.Should().Contain("Fotos (10/10)").And.Contain("JPG, PNG o WebP").And.Contain("5 MB");
        cut.Find("[data-testid='upload-photos']").HasAttribute("disabled").Should().BeTrue(); // límite alcanzado
    }
}

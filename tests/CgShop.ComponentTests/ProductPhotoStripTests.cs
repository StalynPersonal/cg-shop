using Bunit;
using CgShop.Tests.Shared;
using CgShop.Web.Components.Pages.Admin;
using Microsoft.EntityFrameworkCore;

namespace CgShop.ComponentTests;

/// <summary>Vista previa compacta de fotos encima de las variantes en el editor de producto.</summary>
public sealed class ProductPhotoStripTests : StoreTestContext
{
    [Fact]
    public async Task Shows_a_small_thumbnail_per_uploaded_photo_with_the_main_one_marked()
    {
        await SeedProductsAsync(10);
        List<Guid> ids;
        await using (var db = CreateDb())
        {
            var products = await db.Products.Include(p => p.Images).OrderBy(p => p.Name).ToListAsync();
            ids = products.Select(p => p.Id).ToList();
            // 100 fotos: 10 por producto.
            for (var i = 0; i < TestData.BatchSize; i++)
            {
                var product = products[i % 10];
                db.ProductImages.Add(product.AddImage($"products/{i}.jpg", "image/jpeg", 2048, 800, 600, $"f{i}.jpg", 10,
                    DateTime.UtcNow));
            }
            await db.SaveChangesAsync();
        }

        AuthorizeAs(TestData.TenantStaff);
        var cut = Render<ProductEditor>(p => p.AddCascadingValue(HostContext).Add(x => x.Id, ids[0]));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='photo-thumb']").Should().HaveCount(10));
        cut.Find("[data-testid='photo-strip']").TextContent.Should().Contain("Fotos (10)").And.Contain("Administrar");
        cut.FindAll("[data-testid='photo-thumb'].main").Should().ContainSingle();
        cut.FindAll("[data-testid='photo-thumb']")[0].GetAttribute("src").Should().StartWith("/imagenes/");
    }

    [Fact]
    public async Task Without_photos_says_so_and_links_to_add_them()
    {
        await SeedProductsAsync(1);
        Guid id;
        await using (var db = CreateDb())
            id = await db.Products.Select(p => p.Id).FirstAsync();

        AuthorizeAs(TestData.TenantStaff);
        var cut = Render<ProductEditor>(p => p.AddCascadingValue(HostContext).Add(x => x.Id, id));

        cut.WaitForAssertion(() => cut.Find("[data-testid='no-photos']"));
        cut.Find("[data-testid='photo-strip']").TextContent.Should().Contain("Fotos (0)").And.Contain("Agregar fotos");
        cut.FindAll("[data-testid='photo-thumb']").Should().BeEmpty();
    }

    [Fact]
    public async Task With_only_an_external_image_shows_it_as_the_preview()
    {
        var product = CgShop.Domain.Catalog.Product.Create("Tenis Externos", CgShop.Domain.Catalog.ProductCategory.Footwear,
            "Nike", null, "https://cdn.ejemplo.com/tenis.jpg");
        product.AddVariant("EXT-40", 5000, 3, "40", "Negro");
        await using (var db = CreateDb())
        {
            db.Products.Add(product);
            await db.SaveChangesAsync();
        }

        AuthorizeAs(TestData.TenantStaff);
        var cut = Render<ProductEditor>(p => p.AddCascadingValue(HostContext).Add(x => x.Id, product.Id));

        cut.WaitForAssertion(() => cut.Find("[data-testid='photo-strip']").TextContent.Should().Contain("se usa la imagen externa"));
        cut.Find("[data-testid='photo-strip'] img").GetAttribute("src").Should().Be("https://cdn.ejemplo.com/tenis.jpg");
    }
}

using CgShop.Application.Catalog;
using CgShop.Application.Common;
using CgShop.Domain.Catalog;
using CgShop.Domain.Common;
using CgShop.Infrastructure.Files;
using CgShop.Tests.Shared;
using CgShop.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CgShop.UnitTests.Application;

public class ProductImageValidatorTests
{
    private static readonly CatalogOptions Options = new();

    [Fact]
    public void Accepts_100_valid_images_and_reads_real_dimensions()
    {
        for (var i = 0; i < TestData.BatchSize; i++)
        {
            var (name, content, width, height) = TestImages.Any(i);

            var info = ProductImageValidator.Validate(name, content, Options);

            info.Width.Should().Be(width);
            info.Height.Should().Be(height);
            info.ContentType.Should().StartWith("image/");
        }
    }

    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("webp")]
    public void Detects_each_format(string format)
    {
        var (content, type) = format switch
        {
            "jpeg" => (TestImages.Jpeg(800, 600), "image/jpeg"),
            "png" => (TestImages.Png(800, 600), "image/png"),
            _ => (TestImages.WebpVp8X(800, 600), "image/webp")
        };
        ProductImageValidator.TryRead(content)!.ContentType.Should().Be(type);
    }

    [Fact]
    public void Rejects_disguised_or_invalid_files()
    {
        Reject("virus.jpg", [0x4D, 0x5A, 0x90, 0x00], "*no es una imagen*");
        Reject("animacion.gif", "GIF89a0000000000000000000000000"u8.ToArray(), "*no es una imagen*");
        Reject("foto.jpg", TestImages.Png(800, 600), "*extensión no corresponde*");
        Reject("foto.png", TestImages.Png(800, 600)[..20], "*no es una imagen*");      // truncada
        Reject("vacia.png", [], "*vacío*");
    }

    [Theory]
    [InlineData(299, 800)]
    [InlineData(800, 100)]
    [InlineData(6001, 800)]
    public void Rejects_images_with_wrong_dimensions(int width, int height) =>
        Reject("foto.png", TestImages.Png(width, height), "*px*");

    [Fact]
    public void Rejects_files_over_the_size_limit()
    {
        var small = new CatalogOptions { MaxImageBytes = 1024 };
        FluentActions.Invoking(() => ProductImageValidator.Validate("foto.png", TestImages.Png(800, 600, padding: 2000), small))
            .Should().Throw<ValidationException>().WithMessage("*máximo*MB*");
    }

    private static void Reject(string name, byte[] content, string message) =>
        FluentActions.Invoking(() => ProductImageValidator.Validate(name, content, Options))
            .Should().Throw<ValidationException>().WithMessage(message);
}

public class ProductImageServiceTests : IAsyncLifetime, IDisposable
{
    private readonly InMemoryDb _db = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cgshop-img-tests", Guid.NewGuid().ToString("N"));
    private InMemoryDb.Factory _factory = null!;
    private ProductImageService _service = null!;
    private LocalFileStorage _storage = null!;
    private List<Product> _products = null!;

    public async Task InitializeAsync()
    {
        var tenant = await _db.AddTenantAsync();
        _factory = _db.For(tenant);
        _products = TestData.Products(10, tenant.Id).ToList();
        await using (var ctx = _factory.CreateAppDbContext())
        {
            ctx.Products.AddRange(_products);
            await ctx.SaveChangesAsync();
        }

        _storage = new LocalFileStorage(Microsoft.Extensions.Options.Options.Create(new FileStorageOptions { RootPath = _root }));
        _service = new ProductImageService(_factory, _factory.Context, _storage, TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(new CatalogOptions()), NullLogger<ProductImageService>.Instance);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private Task<ProductImageDto> Upload(Guid productId, int i, ActorInfo? actor = null)
    {
        var (name, content, _, _) = TestImages.Any(i);
        return _service.UploadAsync(productId, name, new MemoryStream(content), content.Length, actor ?? TestData.TenantStaff);
    }

    [Fact]
    public async Task Uploads_100_photos_across_10_products_and_catalog_shows_them()
    {
        for (var i = 0; i < TestData.BatchSize; i++)
            await Upload(_products[i % 10].Id, i);

        var catalog = new CatalogService(_factory);
        foreach (var product in _products)
        {
            var images = await _service.ListAsync(product.Id);
            images.Should().HaveCount(10);
            images.Select(im => im.SortOrder).Should().Equal(Enumerable.Range(0, 10));

            var detail = await catalog.GetBySlugAsync(product.Slug);
            detail!.Images.Should().HaveCount(10);
            detail.ImageUrl.Should().Be(images[0].Url);
        }

        var cards = await catalog.SearchAsync(new CatalogQuery(PageSize: 50));
        cards.Items.Should().OnlyContain(c => c.ImageUrl != null && c.ImageUrl.StartsWith("/imagenes/"));
        Directory.GetFiles(Path.Combine(_root, _factory.Context.Tenant!.Id.ToString("N"), "products"))
            .Should().HaveCount(TestData.BatchSize);
    }

    [Fact]
    public async Task Enforces_max_photos_per_product_and_rejects_invalid_uploads()
    {
        var productId = _products[0].Id;
        for (var i = 0; i < 10; i++)
            await Upload(productId, i);

        await FluentActions.Invoking(() => Upload(productId, 99)).Should().ThrowAsync<DomainException>().WithMessage("*máximo de 10*");

        await _service.Invoking(s => s.UploadAsync(_products[1].Id, "falsa.png", new MemoryStream([1, 2, 3]), 3, TestData.TenantStaff))
            .Should().ThrowAsync<ValidationException>();
        await FluentActions.Invoking(() => Upload(_products[1].Id, 1, TestData.Customer)).Should().ThrowAsync<ForbiddenException>();
        (await _service.ListAsync(_products[1].Id)).Should().BeEmpty(); // nada se guardó
    }

    [Fact]
    public async Task Set_main_move_and_delete_keep_order_and_remove_files()
    {
        var productId = _products[0].Id;
        var uploaded = new List<ProductImageDto>();
        for (var i = 0; i < 5; i++)
            uploaded.Add(await Upload(productId, i));

        await _service.SetMainAsync(productId, uploaded[3].Id, TestData.TenantStaff);
        (await _service.ListAsync(productId))[0].Id.Should().Be(uploaded[3].Id);

        await _service.MoveAsync(productId, uploaded[3].Id, 1, TestData.TenantStaff);
        (await _service.ListAsync(productId))[1].Id.Should().Be(uploaded[3].Id);

        await _service.DeleteAsync(productId, uploaded[0].Id, TestData.TenantStaff);
        var remaining = await _service.ListAsync(productId);
        remaining.Should().HaveCount(4).And.NotContain(i => i.Id == uploaded[0].Id);
        remaining.Select(i => i.SortOrder).Should().Equal(0, 1, 2, 3);
        (await _service.OpenAsync(uploaded[0].Id)).Should().BeNull();

        var open = await _service.OpenAsync(remaining[0].Id);
        open.Should().NotBeNull();
        await open!.Value.Content.DisposeAsync();
    }
}

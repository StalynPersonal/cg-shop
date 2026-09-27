using CgShop.Application.Catalog;
using CgShop.Application.Common;
using CgShop.Application.Marketing;
using CgShop.Domain.Common;
using CgShop.Domain.Marketing;
using CgShop.Infrastructure.Files;
using CgShop.Tests.Shared;
using CgShop.UnitTests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace CgShop.UnitTests.Application;

public class BannerServiceTests : IDisposable
{
    private readonly InMemoryDb _db = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cgshop-banner-tests", Guid.NewGuid().ToString("N"));
    private readonly LocalFileStorage _storage;

    public BannerServiceTests() =>
        _storage = new LocalFileStorage(Microsoft.Extensions.Options.Options.Create(new FileStorageOptions { RootPath = _root }));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private BannerService ServiceFor(InMemoryDb.Factory factory) =>
        new(factory, factory.Context, _storage, TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(new CatalogOptions()), NullLogger<BannerService>.Instance);

    private static Task<BannerDto> Upload(BannerService service, int width = 1920, int height = 640,
        ActorInfo? actor = null, string name = "banner.png")
    {
        var content = TestImages.Png(width, height);
        return service.UploadAsync(name, new MemoryStream(content), content.Length, actor ?? TestData.TenantAdmin);
    }

    [Fact]
    public async Task Uploads_100_banners_across_10_stores_each_sees_only_its_own()
    {
        var stores = new List<(InMemoryDb.Factory Factory, BannerService Service, List<Guid> Ids)>();
        for (var t = 0; t < 10; t++)
        {
            var factory = _db.For(await _db.AddTenantAsync($"tienda-{t}"));
            stores.Add((factory, ServiceFor(factory), []));
        }

        for (var i = 0; i < TestData.BatchSize; i++)
        {
            var store = stores[i % 10];
            // Formatos y proporciones variados, todos horizontales.
            var banner = await Upload(store.Service, 1200 + i * 10, 400 + i % 5 * 50);
            store.Ids.Add(banner.Id);
        }

        foreach (var (_, service, ids) in stores)
        {
            var list = await service.ListActiveAsync();
            list.Select(b => b.Id).Should().Equal(ids, "cada tienda ve solo los suyos y en el orden de subida");
            list.Select(b => b.SortOrder).Should().Equal(Enumerable.Range(0, 10));
            list.Should().OnlyContain(b => b.Url.StartsWith("/banners/"));

            var open = await service.OpenAsync(ids[0]);
            open.Should().NotBeNull();
            await open!.Value.Content.DisposeAsync();
        }

        // Una tienda no puede abrir el banner de otra.
        (await stores[0].Service.OpenAsync(stores[1].Ids[0])).Should().BeNull();

        // Límite por tienda.
        await FluentActions.Invoking(() => Upload(stores[0].Service))
            .Should().ThrowAsync<DomainException>().WithMessage($"*hasta {StoreBanner.MaxPerStore} banners*");
    }

    [Theory]
    [InlineData(800, 300, "*al menos 1000 px*")]    // angosto
    [InlineData(1200, 1200, "*horizontal*")]        // cuadrado
    [InlineData(1000, 1500, "*horizontal*")]        // vertical
    public async Task Rejects_banners_that_are_not_wide_enough(int width, int height, string message)
    {
        var service = ServiceFor(_db.For(await _db.AddTenantAsync()));
        await FluentActions.Invoking(() => Upload(service, width, height))
            .Should().ThrowAsync<ValidationException>().WithMessage(message);
        (await service.ListActiveAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Only_the_owner_manages_banners()
    {
        var service = ServiceFor(_db.For(await _db.AddTenantAsync()));
        await FluentActions.Invoking(() => Upload(service, actor: TestData.TenantStaff))
            .Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Invoking(() => Upload(service, actor: TestData.Customer))
            .Should().ThrowAsync<ForbiddenException>();
        await service.Invoking(s => s.ListAsync(TestData.TenantStaff)).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Edits_content_validates_links_and_hides_inactive_banners()
    {
        var service = ServiceFor(_db.For(await _db.AddTenantAsync()));
        var banner = await Upload(service);

        var updated = await service.UpdateAsync(banner.Id, new BannerContentDto
        {
            Title = "  Rebajas de verano ", Subtitle = "Hasta 40% en calzados", ButtonText = "Comprar",
            LinkUrl = "/catalogo/calzados", IsActive = true
        }, TestData.TenantAdmin);
        updated.Title.Should().Be("Rebajas de verano");
        updated.LinkUrl.Should().Be("/catalogo/calzados");

        (await service.UpdateAsync(banner.Id, new BannerContentDto { LinkUrl = "https://nike.com/ofertas" }, TestData.TenantAdmin))
            .LinkUrl.Should().Be("https://nike.com/ofertas");

        foreach (var bad in new[] { "javascript:alert(1)", "http://sin-cifrar.com", "//otro-sitio.com", "ftp://x" })
            await service.Invoking(s => s.UpdateAsync(banner.Id, new BannerContentDto { LinkUrl = bad }, TestData.TenantAdmin))
                .Should().ThrowAsync<DomainException>().WithMessage("*enlace*");

        await service.Invoking(s => s.UpdateAsync(banner.Id, new BannerContentDto { ButtonText = "Ver" }, TestData.TenantAdmin))
            .Should().ThrowAsync<DomainException>().WithMessage("*botón*");
        await service.Invoking(s => s.UpdateAsync(banner.Id, new BannerContentDto { Title = new string('x', 81) }, TestData.TenantAdmin))
            .Should().ThrowAsync<DomainException>().WithMessage("*80 caracteres*");

        await service.UpdateAsync(banner.Id, new BannerContentDto { IsActive = false }, TestData.TenantAdmin);
        (await service.ListActiveAsync()).Should().BeEmpty();
        (await service.ListAsync(TestData.TenantAdmin)).Should().ContainSingle(b => !b.IsActive);
    }

    [Fact]
    public async Task Move_and_delete_keep_a_continuous_order_and_remove_the_file()
    {
        var factory = _db.For(await _db.AddTenantAsync());
        var service = ServiceFor(factory);
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
            ids.Add((await Upload(service)).Id);

        await service.MoveAsync(ids[3], -1, TestData.TenantAdmin);
        (await service.ListActiveAsync()).Select(b => b.Id).Should().Equal(ids[0], ids[1], ids[3], ids[2], ids[4]);

        await service.MoveAsync(ids[0], -1, TestData.TenantAdmin); // ya es el primero: no cambia
        (await service.ListActiveAsync())[0].Id.Should().Be(ids[0]);

        await service.DeleteAsync(ids[1], TestData.TenantAdmin);
        var rest = await service.ListActiveAsync();
        rest.Select(b => b.Id).Should().Equal(ids[0], ids[3], ids[2], ids[4]);
        rest.Select(b => b.SortOrder).Should().Equal(0, 1, 2, 3);
        (await service.OpenAsync(ids[1])).Should().BeNull();
        Directory.GetFiles(Path.Combine(_root, factory.Context.Tenant!.Id.ToString("N"), "banners")).Should().HaveCount(4);
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using CgShop.Application.Catalog;
using CgShop.Infrastructure.Persistence;
using CgShop.IntegrationTests.Api;
using CgShop.IntegrationTests.Infrastructure;
using CgShop.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CgShop.IntegrationTests;

[Collection(SqlServerCollection.Name)]
public sealed class ProductImageIntegrationTests(SqlServerFixture fx) : IAsyncLifetime
{
    private ApiFactory _api = null!;

    public Task InitializeAsync()
    {
        _api = new ApiFactory(fx);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    [Fact]
    public async Task Photos_are_stored_per_store_and_invisible_to_other_stores()
    {
        var storeA = await _api.CreateTenantAsync(products: 10);
        var storeB = await _api.CreateTenantAsync(products: 1);
        List<Guid> imageIds = [];

        await using (var scope = fx.TenantScope(storeA))
        {
            var images = scope.ServiceProvider.GetRequiredService<ProductImageService>();
            var productIds = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Products.Select(p => p.Id).ToListAsync();
            for (var i = 0; i < TestData.BatchSize; i++)
            {
                var (name, content, _, _) = TestImages.Any(i);
                imageIds.Add((await images.UploadAsync(productIds[i % productIds.Count], name, new MemoryStream(content),
                    content.Length, TestData.TenantStaff)).Id);
            }
        }

        var clientA = _api.ClientFor(storeA.Slug);
        var clientB = _api.ClientFor(storeB.Slug);
        foreach (var id in imageIds.Take(20))
        {
            var ok = await clientA.GetAsync($"/imagenes/{id:N}");
            ok.StatusCode.Should().Be(HttpStatusCode.OK);
            ok.Headers.CacheControl!.Public.Should().BeTrue();
            ok.Content.Headers.ContentType!.MediaType.Should().StartWith("image/");

            (await clientB.GetAsync($"/imagenes/{id:N}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Staff_uploads_photos_via_api_invalid_ones_are_rejected_and_delete_cleans_files()
    {
        var store = await _api.CreateTenantAsync(products: 1);
        var staff = await _api.AuthenticatedClientAsync(store.Slug, $"staff@{store.Slug}.test");
        var products = await staff.GetFromJsonAsync<JsonElementPage>("/api/admin/products");
        var productId = products!.Items[0].Id;

        using (var form = new MultipartFormDataContent())
        {
            for (var i = 0; i < 3; i++)
            {
                var (name, content, _, _) = TestImages.Any(i);
                var file = new ByteArrayContent(content);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(file, "files", name);
            }

            (await staff.PostAsync($"/api/admin/products/{productId}/images", form)).StatusCode.Should().Be(HttpStatusCode.Created);
        }

        using (var bad = new MultipartFormDataContent())
        {
            bad.Add(new ByteArrayContent(TestImages.Png(100, 100)), "files", "pequena.png");
            (await staff.PostAsync($"/api/admin/products/{productId}/images", bad)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        var images = await staff.GetFromJsonAsync<List<ProductImageDto>>($"/api/admin/products/{productId}/images");
        images.Should().HaveCount(3);
        var folder = Path.Combine(fx.UploadsPath, store.Id.ToString("N"), "products");
        Directory.GetFiles(folder).Should().HaveCount(3);

        // El propietario elimina el producto (sin ventas): se borran fotos y archivos.
        await using (var scope = fx.TenantScope(store))
        {
            await scope.ServiceProvider.GetRequiredService<ProductAdminService>().DeleteAsync(productId, TestData.TenantAdmin);
        }

        Directory.GetFiles(folder).Should().BeEmpty();
    }

    private sealed record JsonElementPage(List<Row> Items);
    private sealed record Row(Guid Id);
}

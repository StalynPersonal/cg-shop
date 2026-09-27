using Bunit;
using CgShop.Application.Marketing;
using CgShop.Domain.Marketing;
using CgShop.Tests.Shared;
using CgShop.Web.Components.Pages.Admin;
using CgShop.Web.Components.Pages.Store;
using CgShop.Web.Components.Shared;

namespace CgShop.ComponentTests;

/// <summary>Carrusel de portada en la tienda y su administración en el panel.</summary>
public sealed class BannerComponentsTests : StoreTestContext
{
    private async Task<List<StoreBanner>> SeedBannersAsync(int count, Func<int, bool>? active = null)
    {
        var banners = new List<StoreBanner>();
        await using var db = CreateDb();
        for (var i = 0; i < count; i++)
        {
            var banner = StoreBanner.Create($"banners/{i}.png", "image/png", 1000, 1920, 640, i, DateTime.UtcNow);
            banner.UpdateContent($"Promo {i}", i % 2 == 0 ? $"Texto {i}" : null, i % 3 == 0 ? "Comprar" : null,
                i % 3 == 0 || i % 3 == 1 ? $"/catalogo?q=promo{i}" : null, active?.Invoke(i) ?? true);
            banners.Add(banner);
        }

        db.StoreBanners.AddRange(banners);
        await db.SaveChangesAsync();
        return banners;
    }

    [Fact]
    public void Carousel_holds_100_slides_and_shows_the_first_with_its_button()
    {
        var banners = Enumerable.Range(0, TestData.BatchSize).Select(i => new BannerDto(Guid.NewGuid(),
            $"/banners/{i}", i, 1920, 640, 1000, $"Promo {i}", null, i % 2 == 0 ? "Ver" : null,
            $"/catalogo?q=p{i}", true)).ToList();

        var cut = Render<HeroCarousel>(p => p.Add(x => x.Banners, banners));

        // El carrusel tiene las 100 diapositivas (MudBlazor solo pinta la visible: la primera).
        cut.FindComponents<MudBlazor.MudCarouselItem>().Should().HaveCount(TestData.BatchSize);
        cut.Find(".cg-hero-title").TextContent.Should().Be("Promo 0");
        cut.Find("[data-testid='hero-button']").GetAttribute("href").Should().Be("/catalogo?q=p0");
    }

    [Fact]
    public void Banner_without_button_is_a_full_link()
    {
        var banner = new BannerDto(Guid.NewGuid(), "/banners/1", 0, 1920, 640, 1, "Nueva colección", "Otoño", null,
            "/seccion/mujer", true);
        var cut = Render<HeroCarousel>(p => p.Add(x => x.Banners, [banner]));

        cut.Find("a.cg-hero-slide").GetAttribute("href").Should().Be("/seccion/mujer");
        cut.FindAll("[data-testid='hero-button']").Should().BeEmpty();
        cut.Find(".cg-hero-subtitle").TextContent.Should().Be("Otoño");
    }

    [Fact]
    public void Single_banner_does_not_rotate_or_show_arrows()
    {
        var one = new BannerDto(Guid.NewGuid(), "/banners/1", 0, 1920, 640, 1, null, null, null, null, true);
        var cut = Render<HeroCarousel>(p => p.Add(x => x.Banners, [one]));

        cut.FindAll("[data-testid='hero-slide']").Should().ContainSingle();
        cut.FindAll(".mud-carousel-elements-rtl button, .mud-carousel-elements-ltr button, .mud-carousel .mud-icon-button")
            .Should().BeEmpty();
        cut.FindAll(".cg-hero-caption").Should().BeEmpty("sin título ni texto no se oscurece la imagen");
    }

    [Fact]
    public async Task Home_shows_default_hero_without_banners_and_carousel_with_active_ones()
    {
        await SeedProductsAsync(10);
        var empty = Render<StoreHome>(p => p.AddCascadingValue(HostContext));
        empty.WaitForAssertion(() => empty.Find("[data-testid='default-hero']"));
        empty.FindAll("[data-testid='hero-carousel']").Should().BeEmpty();

        await SeedBannersAsync(10, active: i => i < 7);
        var cut = Render<StoreHome>(p => p.AddCascadingValue(HostContext));
        cut.WaitForAssertion(() => cut.FindComponents<MudBlazor.MudCarouselItem>().Should().HaveCount(7));
        cut.FindAll("[data-testid='default-hero']").Should().BeEmpty();
    }

    [Fact]
    public async Task Admin_page_lists_banners_with_their_content()
    {
        await SeedBannersAsync(10, active: i => i != 4);
        AuthorizeAs(TestData.TenantAdmin);

        var cut = Render<BannersPage>(p => p.AddCascadingValue(HostContext));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='banner-item']").Should().HaveCount(10));
        cut.Markup.Should().Contain("(10/10)");
        cut.Find("[data-testid='upload-banner']").HasAttribute("disabled").Should().BeTrue("se alcanzó el máximo");
        cut.FindAll(".cg-banner-preview.inactive").Should().ContainSingle();
    }
}

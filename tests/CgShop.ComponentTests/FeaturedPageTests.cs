using Bunit;
using CgShop.Application.Catalog;
using CgShop.Domain.Catalog;
using CgShop.Tests.Shared;
using CgShop.Web.Components.Pages.Admin;
using Microsoft.EntityFrameworkCore;

namespace CgShop.ComponentTests;

public sealed class FeaturedPageTests : StoreTestContext
{
    private async Task<List<Guid>> SeedFeaturedAsync()
    {
        await SeedProductsAsync();
        List<Guid> ids;
        await using (var db = CreateDb())
            ids = await db.Products.OrderBy(p => p.Name).Select(p => p.Id).Take(12).ToListAsync();
        var service = new FeaturedService(new DirectFactory(this), Clock);
        foreach (var id in ids.Take(12))
            await service.SetAsync(id, FeaturedSection.Trending, true, TestData.TenantAdmin);
        foreach (var id in ids.Take(3))
            await service.SetAsync(id, FeaturedSection.NewArrivals, true, TestData.TenantAdmin);
        return ids;
    }

    [Fact]
    public async Task Lists_both_sections_in_order_and_removes_items()
    {
        await SeedFeaturedAsync();
        AuthorizeAs(TestData.TenantAdmin);

        var cut = Render<FeaturedPage>(p => p.AddCascadingValue(HostContext));

        cut.WaitForAssertion(() => cut.FindAll("[data-testid='featured-Trending'] [data-testid='featured-item']").Should().HaveCount(12));
        cut.FindAll("[data-testid='featured-NewArrivals'] [data-testid='featured-item']").Should().HaveCount(3);
        cut.Find("[data-testid='featured-Trending']").TextContent.Should().Contain("12/12");

        cut.FindAll("[data-testid='featured-NewArrivals'] [data-testid='featured-remove']")[0].Click();
        cut.WaitForAssertion(() =>
            cut.FindAll("[data-testid='featured-NewArrivals'] [data-testid='featured-item']").Should().HaveCount(2));
    }

    [Fact]
    public async Task Editor_shows_featured_switches_with_the_current_state()
    {
        var ids = await SeedFeaturedAsync();

        AuthorizeAs(TestData.TenantAdmin);
        var cut = Render<ProductEditor>(p => p.AddCascadingValue(HostContext).Add(x => x.Id, ids[0]));
        cut.WaitForAssertion(() => cut.Find("[data-testid='featured-panel']"));
        ((AngleSharp.Html.Dom.IHtmlInputElement)cut.Find("input[data-testid='switch-trending']")).IsChecked.Should().BeTrue();
        ((AngleSharp.Html.Dom.IHtmlInputElement)cut.Find("input[data-testid='switch-new']")).IsChecked.Should().BeTrue();
    }
}

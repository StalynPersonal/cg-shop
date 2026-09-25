using System.Security.Claims;
using CgShop.Application.Orders;
using CgShop.Application.Tenancy;
using CgShop.Domain.Common;
using CgShop.Domain.Tenants;
using CgShop.Infrastructure.Identity;
using CgShop.Infrastructure.Tenancy;
using CgShop.Tests.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using AppValidationException = CgShop.Application.Common.ValidationException;

namespace CgShop.UnitTests.Infrastructure;

public class TenantResolutionTests
{
    private static readonly TenantResolutionOptions Options = new()
    {
        RootDomains = ["cgshop.com", "localhost"],
        AllowHeaderFallback = true
    };

    [Theory]
    [InlineData("verde.cgshop.com", "verde")]
    [InlineData("VERDE.CGSHOP.COM", "verde")]
    [InlineData("rojo.localhost", "rojo")]
    [InlineData("admin.localhost", "admin")]
    [InlineData("cgshop.com", null)]
    [InlineData("localhost", null)]
    [InlineData("a.b.cgshop.com", null)]
    [InlineData("verde.otrodominio.com", null)]
    [InlineData("evilcgshop.com", null)]
    public void Extracts_subdomain(string host, string? expected) =>
        Options.ExtractSubdomain(host).Should().Be(expected);

    [Fact]
    public void Extracts_subdomain_for_100_tenants()
    {
        foreach (var t in TestData.Tenants())
            Options.ExtractSubdomain($"{t.Slug}.cgshop.com").Should().Be(t.Slug);
    }

    private static ClaimsPrincipal User(Guid? tenantId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "u1") };
        if (tenantId is not null) claims.Add(new Claim(CgClaimTypes.TenantId, tenantId.ToString()!));
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public void Claim_must_match_resolved_tenant()
    {
        var tenant = Guid.NewGuid();
        TenantResolutionMiddleware.UserBelongsToTenant(new ClaimsPrincipal(), tenant).Should().BeTrue();
        TenantResolutionMiddleware.UserBelongsToTenant(User(tenant, Roles.TenantAdmin), tenant).Should().BeTrue();
        TenantResolutionMiddleware.UserBelongsToTenant(User(Guid.NewGuid(), Roles.TenantAdmin), tenant).Should().BeFalse();
        TenantResolutionMiddleware.UserBelongsToTenant(User(null, Roles.SuperAdmin), tenant).Should().BeTrue();
    }

    private static (TenantResolutionMiddleware Middleware, Func<bool> NextCalled) Build()
    {
        var called = false;
        var middleware = new TenantResolutionMiddleware(_ => { called = true; return Task.CompletedTask; },
            Microsoft.Extensions.Options.Options.Create(Options), NullLogger<TenantResolutionMiddleware>.Instance);
        return (middleware, () => called);
    }

    private static DefaultHttpContext Http(string host, ClaimsPrincipal? user = null)
    {
        var http = new DefaultHttpContext();
        http.Request.Host = new HostString(host);
        http.Response.Body = new MemoryStream();
        if (user is not null) http.User = user;
        return http;
    }

    [Fact]
    public async Task Middleware_resolves_100_tenants_by_subdomain()
    {
        foreach (var tenant in TestData.Tenants())
        {
            var info = TenantInfo.From(tenant);
            var store = Substitute.For<ITenantStore>();
            store.FindBySlugAsync(tenant.Slug, Arg.Any<CancellationToken>()).Returns(info);
            var (middleware, nextCalled) = Build();
            var context = new TenantContext();

            await middleware.InvokeAsync(Http($"{tenant.Slug}.cgshop.com"), context, store);

            nextCalled().Should().BeTrue();
            context.Tenant.Should().Be(info);
        }
    }

    [Fact]
    public async Task Middleware_returns_404_for_unknown_or_suspended_tenant()
    {
        var suspended = Tenant.Create("Susp", "suspendida", "#000000");
        suspended.Suspend();
        var store = Substitute.For<ITenantStore>();
        store.FindBySlugAsync("suspendida", Arg.Any<CancellationToken>()).Returns(TenantInfo.From(suspended));

        foreach (var host in new[] { "noexiste.cgshop.com", "suspendida.cgshop.com" })
        {
            var (middleware, nextCalled) = Build();
            var http = Http(host);
            await middleware.InvokeAsync(http, new TenantContext(), store);
            http.Response.StatusCode.Should().Be(404);
            nextCalled().Should().BeFalse();
        }
    }

    [Fact]
    public async Task Middleware_returns_403_when_claim_belongs_to_other_tenant()
    {
        var tenant = Tenant.Create("A", "tienda-a", "#000000");
        var store = Substitute.For<ITenantStore>();
        store.FindBySlugAsync("tienda-a", Arg.Any<CancellationToken>()).Returns(TenantInfo.From(tenant));
        var (middleware, nextCalled) = Build();
        var http = Http("tienda-a.cgshop.com", User(Guid.NewGuid(), Roles.TenantAdmin));

        await middleware.InvokeAsync(http, new TenantContext(), store);

        http.Response.StatusCode.Should().Be(403);
        nextCalled().Should().BeFalse();
    }

    [Fact]
    public async Task Middleware_marks_admin_host_and_supports_header_fallback()
    {
        var store = Substitute.For<ITenantStore>();
        var (middleware, _) = Build();
        var adminContext = new TenantContext();
        await middleware.InvokeAsync(Http("admin.cgshop.com"), adminContext, store);
        adminContext.IsAdminHost.Should().BeTrue();
        adminContext.Tenant.Should().BeNull();

        var tenant = Tenant.Create("H", "por-header", "#000000");
        store.FindBySlugAsync("por-header", Arg.Any<CancellationToken>()).Returns(TenantInfo.From(tenant));
        var http = Http("api.cgshop.io");
        http.Request.Headers[TenantResolutionOptions.HeaderName] = "Por-Header";
        var context = new TenantContext();
        await middleware.InvokeAsync(http, context, store);
        context.Tenant!.Slug.Should().Be("por-header");
    }

    [Fact]
    public void Tenant_context_cannot_switch_tenant_once_set()
    {
        var context = new TenantContext();
        context.SetTenant(TenantInfo.From(Tenant.Create("A", "aaa", "#000000")));
        context.Invoking(c => c.SetTenant(TenantInfo.From(Tenant.Create("B", "bbb", "#000000"))))
            .Should().Throw<InvalidOperationException>();
    }
}

public class ReceiptFileValidatorTests
{
    private const long Max = 5 * 1024 * 1024;

    [Theory]
    [InlineData("pago.pdf", new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, ".pdf", "application/pdf")]
    [InlineData("PAGO.PNG", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, ".png", "image/png")]
    [InlineData("foto.jpeg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, ".jpg", "image/jpeg")]
    public void Accepts_valid_files(string name, byte[] header, string ext, string type) =>
        ReceiptFileValidator.Validate(name, header, header.Length, Max).Should().Be((ext, type));

    [Theory]
    [InlineData("script.exe", new byte[] { 0x4D, 0x5A })]
    [InlineData("falso.pdf", new byte[] { 0x4D, 0x5A, 0x00, 0x00 })]
    [InlineData("falso.png", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })]
    [InlineData("vacio.pdf", new byte[0])]
    [InlineData("doc.docx", new byte[] { 0x50, 0x4B })]
    public void Rejects_invalid_files(string name, byte[] header) =>
        FluentActions.Invoking(() => ReceiptFileValidator.Validate(name, header, header.Length, Max))
            .Should().Throw<AppValidationException>();

    [Fact]
    public void Rejects_files_over_limit() =>
        FluentActions.Invoking(() => ReceiptFileValidator.Validate("a.pdf", [0x25, 0x50, 0x44, 0x46], Max + 1, Max))
            .Should().Throw<AppValidationException>().WithMessage("*5 MB*");
}

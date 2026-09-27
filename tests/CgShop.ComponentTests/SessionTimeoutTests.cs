using System.Net;
using Bunit;
using CgShop.Tests.Shared;
using CgShop.Web.Components.Shared;
using CgShop.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CgShop.ComponentTests;

/// <summary>Proveedor de configuración en memoria para pruebas del host.</summary>
public sealed class FakePlatformSettings : CgShop.Application.Platform.IPlatformSettingsProvider
{
    public CgShop.Domain.Platform.PlatformSettingsValues Values { get; set; } = new(48, 10, 5, 5, 10, 300, 6000, 15, 60);

    public CgShop.Domain.Platform.PlatformSettingsValues Current => Values;

    public Task<CgShop.Application.Platform.PlatformSettingsView> GetAsync(CancellationToken ct = default) =>
        Task.FromResult(new CgShop.Application.Platform.PlatformSettingsView(Values, Values, false, null, null));

    public Task<CgShop.Application.Platform.PlatformSettingsView> UpdateAsync(
        CgShop.Domain.Platform.PlatformSettingsValues values, CgShop.Domain.Common.ActorInfo actor,
        CancellationToken ct = default)
    {
        Values = values;
        return GetAsync(ct);
    }

    public Task<CgShop.Application.Platform.PlatformSettingsView> ResetAsync(CgShop.Domain.Common.ActorInfo actor,
        CancellationToken ct = default) => GetAsync(ct);
}

public sealed class SessionIdlePolicyTests
{
    [Fact]
    public void Session_expires_only_after_idle_minutes_for_100_configurations()
    {
        var issued = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        for (var minutes = 1; minutes <= TestData.BatchSize; minutes++)
        {
            var idle = TimeSpan.FromMinutes(minutes);
            SessionIdlePolicy.IsExpired(issued, issued + idle - TimeSpan.FromSeconds(1), idle).Should().BeFalse();
            SessionIdlePolicy.IsExpired(issued, issued + idle + TimeSpan.FromSeconds(1), idle).Should().BeTrue();
        }

        SessionIdlePolicy.IsExpired(null, issued, TimeSpan.FromMinutes(1)).Should().BeFalse();
    }
}

public sealed class SessionTimeoutOptionsTests
{
    [Theory]
    [InlineData(15, 900)]
    [InlineData(0, 60)]        // mínimo 1 minuto
    [InlineData(-5, 60)]
    [InlineData(5000, 86400)]  // máximo 24 horas
    public void Idle_timeout_is_clamped(int minutes, int expectedSeconds) =>
        new SessionTimeoutOptions { IdleTimeoutMinutes = minutes }.IdleTimeout.TotalSeconds.Should().Be(expectedSeconds);

    [Fact]
    public void Warning_never_exceeds_half_of_the_timeout_for_100_configurations()
    {
        for (var minutes = 1; minutes <= TestData.BatchSize; minutes++)
        {
            var o = new SessionTimeoutOptions { IdleTimeoutMinutes = minutes, WarningSeconds = 600, KeepAliveSeconds = 600 };
            o.EffectiveWarningSeconds.Should().BeLessThanOrEqualTo(Math.Max(10, minutes * 60 / 2));
            o.EffectiveKeepAliveSeconds.Should().BeLessThanOrEqualTo(Math.Max(15, minutes * 60 / 3));
        }
    }
}

public sealed class SessionTimeoutGuardTests : MudTestContext
{
    [Fact]
    public void Starts_idle_tracking_with_configured_minutes_for_100_configurations()
    {
        for (var minutes = 1; minutes <= TestData.BatchSize; minutes++)
        {
            using var ctx = new MudTestContextImpl();
            ctx.Services.AddSingleton(Options.Create(new SessionTimeoutOptions { IdleTimeoutMinutes = minutes }));

            ctx.Render<SessionTimeoutGuard>();

            var call = ctx.JSInterop.VerifyInvoke("cgSession.start");
            call.Arguments[1].Should().Be(minutes * 60);
            call.Arguments[4].Should().Be(SessionTimeoutGuard.PingUrl);
            call.Arguments[5].Should().Be(SessionTimeoutGuard.ExpireUrl);
        }
    }

    [Fact]
    public async Task Shows_countdown_and_stay_connected_resets_it()
    {
        Services.AddSingleton(Options.Create(new SessionTimeoutOptions()));
        var cut = Render<SessionTimeoutGuard>();
        cut.FindAll("[data-testid='session-warning']").Should().BeEmpty();

        await cut.InvokeAsync(() => cut.Instance.ShowWarning(45));
        cut.Find("[data-testid='session-seconds']").TextContent.Should().Be("45");

        cut.Find("[data-testid='session-stay']").Click();
        cut.FindAll("[data-testid='session-warning']").Should().BeEmpty();
        JSInterop.VerifyInvoke("cgSession.stayConnected");
    }

    [Fact]
    public async Task Activity_in_another_tab_hides_the_warning()
    {
        Services.AddSingleton(Options.Create(new SessionTimeoutOptions()));
        var cut = Render<SessionTimeoutGuard>();

        await cut.InvokeAsync(() => cut.Instance.ShowWarning(30));
        await cut.InvokeAsync(() => cut.Instance.HideWarning());

        cut.FindAll("[data-testid='session-warning']").Should().BeEmpty();
    }

    [Fact]
    public async Task Disposing_after_prerender_does_not_call_javascript()
    {
        // En el prerender (render estático) nunca corre OnAfterRender: desechar no debe invocar JS.
        var guard = new SessionTimeoutGuard();
        await guard.Invoking(g => g.DisposeAsync().AsTask()).Should().NotThrowAsync();
    }

    private sealed class MudTestContextImpl : MudTestContext;
}

/// <summary>Configuración real del host web: la cookie vence tras los minutos configurados sin actividad.</summary>
public sealed class SessionTimeoutHostTests : IClassFixture<SessionTimeoutHostTests.WebFactory>
{
    private readonly WebFactory _factory;

    public SessionTimeoutHostTests(WebFactory factory) => _factory = factory;

    public sealed class WebFactory : WebApplicationFactory<Program>
    {
        public FakePlatformSettings Settings { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Jobs:Enabled", "false");
            builder.ConfigureTestServices(services =>
                services.AddSingleton<CgShop.Application.Platform.IPlatformSettingsProvider>(Settings));
        }
    }

    [Fact]
    public void Idle_timeout_comes_from_platform_settings_and_changes_without_restart()
    {
        var cookie = _factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        cookie.ExpireTimeSpan.Should().Be(SessionIdlePolicy.MaxCookieLifetime);
        cookie.SlidingExpiration.Should().BeTrue();
        cookie.Events.OnValidatePrincipal.Should().NotBeNull();

        var options = _factory.Services.GetRequiredService<IOptions<SessionTimeoutOptions>>();
        _factory.Settings.Values = _factory.Settings.Values with { IdleTimeoutMinutes = 7 };
        options.Value.IdleTimeout.Should().Be(TimeSpan.FromMinutes(7));

        _factory.Settings.Values = _factory.Settings.Values with { IdleTimeoutMinutes = 30 };
        options.Value.IdleTimeout.Should().Be(TimeSpan.FromMinutes(30)); // sin reiniciar
    }

    [Fact]
    public async Task Expired_session_endpoint_signs_out_and_redirects_to_login_with_notice()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/cuenta/sesion-expirada");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("/cuenta/login?expirada=1");
    }

    [Fact]
    public async Task Keep_alive_requires_an_authenticated_session()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/cuenta/mantener-sesion");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Contain("/cuenta/login");
    }
}

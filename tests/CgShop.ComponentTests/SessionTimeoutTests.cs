using System.Net;
using Bunit;
using CgShop.Tests.Shared;
using CgShop.Web.Components.Shared;
using CgShop.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CgShop.ComponentTests;

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

    private sealed class MudTestContextImpl : MudTestContext;
}

/// <summary>Configuración real del host web: la cookie vence tras los minutos configurados sin actividad.</summary>
public sealed class SessionTimeoutHostTests : IClassFixture<SessionTimeoutHostTests.WebFactory>
{
    private readonly WebFactory _factory;

    public SessionTimeoutHostTests(WebFactory factory) => _factory = factory;

    public sealed class WebFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Session:IdleTimeoutMinutes", "7");
            builder.UseSetting("Jobs:Enabled", "false");
        }
    }

    [Fact]
    public void Auth_cookie_expires_after_configured_idle_minutes_with_sliding_renewal()
    {
        var cookie = _factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);

        cookie.ExpireTimeSpan.Should().Be(TimeSpan.FromMinutes(7));
        cookie.SlidingExpiration.Should().BeTrue();
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

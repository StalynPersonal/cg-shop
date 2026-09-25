using Bunit;
using CgShop.Web.Theming;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace CgShop.ComponentTests;

/// <summary>
/// Contexto bUnit con MudBlazor. Implementa IAsyncLifetime porque varios servicios de MudBlazor
/// solo son IAsyncDisposable y xUnit v2 dispone los tests de forma síncrona.
/// </summary>
public abstract class MudTestContext : BunitContext, IAsyncLifetime
{
    protected MudTestContext()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton<ITenantThemeService, TenantThemeService>();
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();
}

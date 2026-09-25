using CgShop.Domain.Common;
using CgShop.Infrastructure;
using CgShop.Infrastructure.Persistence;
using CgShop.Infrastructure.Tenancy;
using CgShop.Web;
using CgShop.Web.Components;
using CgShop.Web.Endpoints;
using CgShop.Web.Services;
using CgShop.Web.Theming;
using Microsoft.AspNetCore.Identity;
using MudBlazor;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices(o =>
{
    o.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
    o.SnackbarConfiguration.VisibleStateDuration = 4000;
});

builder.Services.AddInfrastructure(builder.Configuration);

// Autenticación por cookie de Identity. La cookie es "host-only": cada subdominio (tienda) tiene su propia sesión.
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthentication(o =>
    {
        o.DefaultScheme = IdentityConstants.ApplicationScheme;
        o.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();
builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "cgshop.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.LoginPath = "/cuenta/login";
    o.AccessDeniedPath = "/cuenta/acceso-denegado";
    o.SlidingExpiration = true;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
});
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.TenantStaff, p => p.RequireRole(Roles.TenantAdmin, Roles.TenantStaff))
    .AddPolicy(Policies.TenantAdmin, p => p.RequireRole(Roles.TenantAdmin))
    .AddPolicy(Policies.SuperAdmin, p => p.RequireRole(Roles.SuperAdmin));

builder.Services.AddSingleton<ITenantThemeService, TenantThemeService>();
builder.Services.AddScoped<CartState>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    scope.ServiceProvider.GetRequiredService<CgShop.Application.Tenancy.TenantContext>().EnterSystemScope();
    await scope.ServiceProvider.GetRequiredService<DataSeeder>().SeedAsync();
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.MapStaticAssets();

app.UseAuthentication();
app.UseTenantResolution(); // después de autenticar (valida claim tenant_id) y antes de autorizar
app.UseAuthorization();
app.UseAntiforgery();

app.MapAccountEndpoints();
app.MapReceiptEndpoints();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;

using System.Net;
using System.Security.Claims;
using CgShop.Application.Tenancy;
using CgShop.Domain.Common;
using CgShop.Infrastructure.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CgShop.Infrastructure.Tenancy;

/// <summary>
/// Resuelve el tenant de cada petición:
/// 1) subdominio (<c>verde.cgshop.com</c>, <c>verde.localhost</c>),
/// 2) header <c>X-Tenant</c> si está habilitado.
/// <c>admin.*</c> marca la zona de Super Admin. Un tenant inexistente o suspendido responde 404.
/// Si el usuario autenticado tiene un claim <c>tenant_id</c> distinto al tenant resuelto, responde 403.
/// Debe registrarse después de <c>UseAuthentication()</c> y antes de <c>UseAuthorization()</c>.
/// </summary>
public sealed class TenantResolutionMiddleware(
    RequestDelegate next,
    IOptions<TenantResolutionOptions> options,
    ILogger<TenantResolutionMiddleware> logger)
{
    public const string TenantItemKey = "cg:tenant";

    public async Task InvokeAsync(HttpContext http, TenantContext tenantContext, ITenantStore store)
    {
        var opts = options.Value;
        var slug = opts.ExtractSubdomain(http.Request.Host.Host);

        if (slug == opts.AdminSubdomain)
        {
            tenantContext.SetAdminHost();
            await next(http);
            return;
        }

        if (slug is null && opts.AllowHeaderFallback &&
            http.Request.Headers.TryGetValue(TenantResolutionOptions.HeaderName, out var header) &&
            !string.IsNullOrWhiteSpace(header))
        {
            slug = header.ToString().Trim().ToLowerInvariant();
        }

        if (slug is null)
        {
            // Dominio raíz: landing/directorio sin tenant. Los filtros globales no devuelven datos.
            await next(http);
            return;
        }

        var tenant = await store.FindBySlugAsync(slug, http.RequestAborted);
        if (tenant is null || !tenant.IsActive)
        {
            logger.LogInformation("Tenant '{Slug}' no encontrado o suspendido", slug);
            await WriteErrorAsync(http, HttpStatusCode.NotFound,
                "Tienda no encontrada", "La tienda que buscas no existe o no está disponible.");
            return;
        }

        if (!UserBelongsToTenant(http.User, tenant.Id))
        {
            logger.LogWarning("Usuario {User} con claim de otro tenant intentó acceder a {Slug}",
                http.User.Identity?.Name, slug);
            await WriteErrorAsync(http, HttpStatusCode.Forbidden,
                "Acceso denegado", "Tu cuenta no pertenece a esta tienda.");
            return;
        }

        tenantContext.SetTenant(tenant);
        http.Items[TenantItemKey] = tenant;
        await next(http);
    }

    public static bool UserBelongsToTenant(ClaimsPrincipal user, Guid tenantId)
    {
        if (user.Identity?.IsAuthenticated != true || user.IsInRole(Roles.SuperAdmin))
            return true;
        var claim = user.FindFirstValue(CgClaimTypes.TenantId);
        // Usuarios sin claim de tenant (p.ej. clientes invitados) no están atados a una tienda.
        return claim is null || (Guid.TryParse(claim, out var id) && id == tenantId);
    }

    private static async Task WriteErrorAsync(HttpContext http, HttpStatusCode status, string title, string message)
    {
        http.Response.StatusCode = (int)status;
        var wantsJson = http.Request.Path.StartsWithSegments("/api") ||
                        http.Request.Headers.Accept.Any(a => a?.Contains("application/json") == true);
        if (wantsJson)
        {
            await http.Response.WriteAsJsonAsync(new { title, status = (int)status, detail = message });
            return;
        }

        http.Response.ContentType = "text/html; charset=utf-8";
        await http.Response.WriteAsync($$"""
            <!doctype html><html lang="es"><head><meta charset="utf-8"><title>{{title}}</title>
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <style>body{font-family:Roboto,system-ui,sans-serif;display:grid;place-items:center;min-height:100vh;margin:0;background:#f5f5f5;color:#333}
            main{text-align:center;padding:2rem}h1{font-size:4rem;margin:0;color:#9e9e9e}</style></head>
            <body><main><h1>{{(int)status}}</h1><h2>{{title}}</h2><p>{{message}}</p></main></body></html>
            """);
    }
}

public static class TenantResolutionMiddlewareExtensions
{
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app) =>
        app.UseMiddleware<TenantResolutionMiddleware>();
}

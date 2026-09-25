using CgShop.Application.Orders;
using CgShop.Application.Tenancy;
using CgShop.Domain.Common;
using CgShop.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace CgShop.Web.Endpoints;

public static class AccountEndpoints
{
    /// <summary>
    /// Login/logout por formulario (el SignIn de cookies requiere una petición HTTP, no el circuito).
    /// En un subdominio de tienda solo pueden entrar usuarios de ESA tienda; en admin.* solo Super Admin.
    /// </summary>
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/cuenta/login", async ([FromForm] string email, [FromForm] string password,
            [FromForm] string? returnUrl, SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users,
            ITenantContext tenant) =>
        {
            var failure = $"/cuenta/login?error=1&returnUrl={Uri.EscapeDataString(returnUrl ?? "")}";
            var user = await users.FindByEmailAsync(email ?? "");
            if (user is null)
                return Results.LocalRedirect(failure);

            var allowed = tenant.IsAdminHost
                ? await users.IsInRoleAsync(user, Roles.SuperAdmin)
                : tenant.TenantId is { } tenantId && user.TenantId == tenantId;
            if (!allowed)
                return Results.LocalRedirect(failure);

            var result = await signIn.PasswordSignInAsync(user, password ?? "", isPersistent: true, lockoutOnFailure: true);
            if (result.IsLockedOut)
                return Results.LocalRedirect("/cuenta/login?error=lockout");
            if (!result.Succeeded)
                return Results.LocalRedirect(failure);

            var target = IsLocal(returnUrl) ? returnUrl! : tenant.IsAdminHost ? "/" : "/admin";
            return Results.LocalRedirect(target);
        });

        app.MapPost("/cuenta/logout", async (SignInManager<ApplicationUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.LocalRedirect("/");
        });

        // Diagnóstico (solo desarrollo): claims del usuario actual.
        if (app is WebApplication { Environment: var env } && env.IsDevelopment())
        {
            app.MapGet("/cuenta/claims", (HttpContext http) =>
                http.User.Claims.Select(c => new { c.Type, c.Value })).RequireAuthorization();
        }

        return app;
    }

    private static bool IsLocal(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));
}

public static class ReceiptEndpoints
{
    /// <summary>Descarga/visualización de comprobantes: solo personal de la tienda dueña de la orden.</summary>
    public static IEndpointRouteBuilder MapReceiptEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/comprobantes/{id:guid}", async (Guid id, bool? download, OrderAdminService orders,
            HttpContext http) =>
        {
            var file = await orders.OpenReceiptAsync(id, http.User.ToActor(), http.RequestAborted);
            if (file is null)
                return Results.NotFound();
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers.CacheControl = "private, no-store";
            // Sin nombre de descarga => inline (vista previa de imagen/PDF en el panel).
            return Results.File(file.Value.Content, file.Value.ContentType,
                download == true ? file.Value.FileName : null);
        }).RequireAuthorization(Policies.TenantStaff);

        return app;
    }
}

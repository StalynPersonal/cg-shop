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
            TenantUserService tenantUsers, ITenantContext tenant) =>
        {
            var failure = $"/cuenta/login?error=1&returnUrl={Uri.EscapeDataString(returnUrl ?? "")}";
            // Solo usuarios del host actual (tienda) o Super Admin en admin.*.
            var user = await tenantUsers.FindForLoginAsync(email, tenant);
            if (user is null)
                return Results.LocalRedirect(failure);

            var result = await signIn.PasswordSignInAsync(user, password ?? "", isPersistent: true, lockoutOnFailure: true);
            if (result.IsLockedOut)
                return Results.LocalRedirect("/cuenta/login?error=lockout");
            if (!result.Succeeded)
                return Results.LocalRedirect(failure);

            var roles = await users.GetRolesAsync(user);
            return Results.LocalRedirect(LoginRedirect.Resolve(roles, returnUrl, tenant.IsAdminHost));
        });

        app.MapPost("/cuenta/registro", async ([FromForm] string fullName, [FromForm] string email,
            [FromForm] string phone, [FromForm] string password, [FromForm] string confirmPassword,
            [FromForm] string? returnUrl, TenantUserService tenantUsers, SignInManager<ApplicationUser> signIn,
            ITenantContext tenant) =>
        {
            string Fail(string message) =>
                $"/cuenta/registro?error={Uri.EscapeDataString(message)}&returnUrl={Uri.EscapeDataString(returnUrl ?? "")}" +
                $"&nombre={Uri.EscapeDataString(fullName ?? "")}&correo={Uri.EscapeDataString(email ?? "")}" +
                $"&telefono={Uri.EscapeDataString(phone ?? "")}";

            if (!tenant.HasTenant)
                return Results.NotFound();
            if (password != confirmPassword)
                return Results.LocalRedirect(Fail("Las contraseñas no coinciden."));

            ApplicationUser user;
            try
            {
                user = await tenantUsers.RegisterCustomerAsync(new RegisterCustomerRequest(fullName ?? "", email ?? "",
                    phone ?? "", password ?? ""), tenant);
            }
            catch (CgShop.Application.Common.ValidationException ex)
            {
                return Results.LocalRedirect(Fail(string.Join(" ", ex.Errors)));
            }

            await signIn.SignInAsync(user, isPersistent: true);
            return Results.LocalRedirect(LoginRedirect.Resolve([Roles.Customer], returnUrl, isAdminHost: false));
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

}

/// <summary>
/// Destino tras iniciar sesión:
/// propietario/empleado → panel de administración (/admin); cliente → su panel (/mi-cuenta)
/// o la página de la que venía (p. ej. /checkout). En admin.* el Super Admin va al inicio.
/// </summary>
public static class LoginRedirect
{
    public const string AdminPanel = "/admin";
    public const string CustomerPanel = "/mi-cuenta";

    public static string Resolve(IEnumerable<string> roles, string? returnUrl, bool isAdminHost)
    {
        var local = IsLocal(returnUrl) ? returnUrl : null;
        if (isAdminHost)
            return local ?? "/";

        var roleSet = roles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isStoreStaff = roleSet.Contains(Roles.TenantAdmin) || roleSet.Contains(Roles.TenantStaff);
        if (isStoreStaff)
            return local is not null && local.StartsWith(AdminPanel, StringComparison.OrdinalIgnoreCase) ? local : AdminPanel;

        // Clientes: nunca al panel de administración.
        return local is not null && !local.StartsWith(AdminPanel, StringComparison.OrdinalIgnoreCase) && local != "/"
            ? local
            : CustomerPanel;
    }

    public static bool IsLocal(string? url) =>
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

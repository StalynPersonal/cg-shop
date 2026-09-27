using CgShop.Application.Catalog;
using CgShop.Application.Common;
using CgShop.Application.Orders;
using CgShop.Application.Tenancy;
using CgShop.Application.Tenants;
using CgShop.Domain.Catalog;
using CgShop.Domain.Common;
using CgShop.Domain.Orders;
using CgShop.Domain.Tenants;
using CgShop.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace CgShop.Api.Endpoints;

public sealed record LoginRequest(string Email, string Password);
public sealed record TokenResponse(string AccessToken, DateTime ExpiresAtUtc, string TokenType = "Bearer");
public sealed record NoteRequest(string? Note);
public sealed record StockAdjustRequest(int Delta, string Reason);
public sealed record CreateProductRequest(ProductUpsertDto Product, List<VariantUpsertDto> Variants);

public static class AuthEndpoints
{
    /// <summary>JWT para el tenant resuelto (subdominio o X-Tenant) o para admin.* (Super Admin).</summary>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/token", async (LoginRequest request, TenantUserService tenantUsers,
            SignInManager<ApplicationUser> signIn, ITenantContext tenant, JwtTokenService tokens) =>
        {
            var user = await tenantUsers.FindForLoginAsync(request.Email, tenant);
            if (user is null)
                return Results.Unauthorized();

            var check = await signIn.CheckPasswordSignInAsync(user, request.Password ?? "", lockoutOnFailure: true);
            if (!check.Succeeded)
                return Results.Unauthorized();

            var (token, expires) = await tokens.CreateAsync(user);
            return Results.Ok(new TokenResponse(token, expires));
        }).AllowAnonymous().WithTags("Auth");

        // Registro de clientes de la tienda resuelta; devuelve directamente el token.
        app.MapPost("/api/auth/register", async (RegisterCustomerRequest request, TenantUserService tenantUsers,
            ITenantContext tenant, JwtTokenService tokens) =>
        {
            var user = await tenantUsers.RegisterCustomerAsync(request, tenant);
            var (token, expires) = await tokens.CreateAsync(user);
            return Results.Created("/api/store/my-orders", new TokenResponse(token, expires));
        }).AllowAnonymous().WithTags("Auth").AddEndpointFilter(StoreEndpoints.RequireTenant);

        return app;
    }
}

public static class StoreEndpoints
{
    /// <summary>API pública de la tienda (requiere tenant resuelto).</summary>
    public static IEndpointRouteBuilder MapStoreEndpoints(this IEndpointRouteBuilder app)
    {
        var store = app.MapGroup("/api/store").WithTags("Tienda").AddEndpointFilter(RequireTenant);

        store.MapGet("/catalog", (CatalogService catalog, ProductCategory? category, string? search, string? size,
                string? color, CatalogSort? sort, int? page, int? pageSize, CancellationToken ct) =>
            catalog.SearchAsync(new CatalogQuery(category, search, size, color, Sort: sort ?? CatalogSort.Newest,
                Page: page ?? 1, PageSize: pageSize ?? 24), ct));

        store.MapGet("/catalog/{slug}", async (string slug, CatalogService catalog, CancellationToken ct) =>
            await catalog.GetBySlugAsync(slug, ct) is { } product ? Results.Ok(product) : Results.NotFound());

        app.MapGet("/imagenes/{id:guid}", async (Guid id, ProductImageService images, ITenantContext tenant,
            HttpContext http, CancellationToken ct) =>
        {
            if (!tenant.HasTenant)
                return Results.NotFound();
            var file = await images.OpenAsync(id, ct);
            if (file is null)
                return Results.NotFound();
            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.File(file.Value.Content, file.Value.ContentType);
        }).WithTags("Tienda");

        store.MapGet("/payment-methods",(CustomerOrderService orders, CancellationToken ct) =>
            orders.GetAvailablePaymentMethodsAsync(ct));

        // Comprar requiere cliente autenticado: el pedido queda asociado al usuario del token.
        store.MapPost("/orders", async (PlaceOrderRequest request, CheckoutService checkout, HttpContext http,
            CancellationToken ct) =>
        {
            request.CustomerUserId = http.User.ToActor().UserId; // nunca se confía en el valor del cuerpo
            var result = await checkout.PlaceOrderAsync(request, ct);
            return Results.Created($"/api/store/orders/{result.Number}?token={result.AccessToken}", result);
        }).RequireAuthorization();

        store.MapGet("/my-orders", (CustomerOrderService orders, HttpContext http, int? page, int? pageSize,
                CancellationToken ct) =>
            orders.ListMineAsync(http.User.ToActor().UserId, page ?? 1, pageSize ?? 10, ct)).RequireAuthorization();

        store.MapGet("/orders/{number}", async (string number, string token, CustomerOrderService orders,
                CancellationToken ct) =>
            await orders.GetAsync(number, token, ct) is { } order ? Results.Ok(order) : Results.NotFound());

        store.MapGet("/orders/{number}/payment-instructions", async (string number, string token,
            CustomerOrderService orders, CancellationToken ct) =>
        {
            var order = await orders.GetAsync(number, token, ct);
            return order is null
                ? Results.NotFound()
                : Results.Ok(await orders.GetPaymentInstructionsAsync(order.PaymentMethod, ct));
        });

        store.MapPost("/orders/{number}/receipts", async (string number, [FromQuery] string token, IFormFile file,
            [FromForm] string? reference, CustomerOrderService orders, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            var receipt = await orders.UploadReceiptAsync(number, token, file.FileName, stream, file.Length, reference, ct);
            return Results.Created($"/api/store/orders/{number}", receipt);
        }).DisableAntiforgery(); // API sin cookies: el token de la orden actúa como credencial

        return app;
    }

    internal static async ValueTask<object?> RequireTenant(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var tenant = ctx.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
        return tenant.HasTenant
            ? await next(ctx)
            : Results.Problem("Indique la tienda por subdominio o encabezado X-Tenant.", statusCode: 400);
    }
}

public static class AdminEndpoints
{
    /// <summary>Panel del tenant vía API (JWT). La validación de pagos exige TenantAdmin.</summary>
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").WithTags("Panel del tenant")
            .RequireAuthorization(ApiPolicies.TenantStaff)
            .AddEndpointFilter(StoreEndpoints.RequireTenant);

        admin.MapGet("/dashboard", (OrderAdminService orders, CancellationToken ct) => orders.GetDashboardAsync(ct: ct));

        admin.MapGet("/orders", (OrderAdminService orders, OrderStatus? status, string? search, int? page,
                int? pageSize, CancellationToken ct) =>
            orders.ListAsync(new OrderQuery(status, search, page ?? 1, pageSize ?? 25), ct));

        admin.MapGet("/orders/{id:guid}", (Guid id, OrderAdminService orders, CancellationToken ct) => orders.GetAsync(id, ct));

        admin.MapPost("/orders/{id:guid}/validate-payment", async (Guid id, NoteRequest body, OrderAdminService orders,
            HttpContext http, CancellationToken ct) =>
        {
            await orders.ValidatePaymentAsync(id, http.User.ToActor(), body.Note, ct);
            return Results.Ok(await orders.GetAsync(id, ct));
        }).RequireAuthorization(ApiPolicies.TenantAdmin);

        admin.MapPost("/orders/{id:guid}/reject-payment", async (Guid id, NoteRequest body, OrderAdminService orders,
            HttpContext http, CancellationToken ct) =>
        {
            await orders.RejectPaymentAsync(id, http.User.ToActor(), body.Note ?? "", ct);
            return Results.Ok(await orders.GetAsync(id, ct));
        }).RequireAuthorization(ApiPolicies.TenantAdmin);

        admin.MapPost("/orders/{id:guid}/prepare", async (Guid id, OrderAdminService orders, HttpContext http,
            CancellationToken ct) =>
        {
            await orders.StartPreparingAsync(id, http.User.ToActor(), ct);
            return Results.Ok(await orders.GetAsync(id, ct));
        });

        admin.MapPost("/orders/{id:guid}/ship", async (Guid id, NoteRequest body, OrderAdminService orders,
            HttpContext http, CancellationToken ct) =>
        {
            await orders.ShipAsync(id, http.User.ToActor(), body.Note, ct);
            return Results.Ok(await orders.GetAsync(id, ct));
        });

        admin.MapPost("/orders/{id:guid}/ready-for-pickup", async (Guid id, NoteRequest body, OrderAdminService orders,
            HttpContext http, CancellationToken ct) =>
        {
            await orders.MarkReadyForPickupAsync(id, http.User.ToActor(), body.Note, ct);
            return Results.Ok(await orders.GetAsync(id, ct));
        });

        admin.MapPost("/orders/{id:guid}/deliver", async (Guid id, OrderAdminService orders, HttpContext http,
            CancellationToken ct) =>
        {
            await orders.MarkDeliveredAsync(id, http.User.ToActor(), ct);
            return Results.Ok(await orders.GetAsync(id, ct));
        });

        admin.MapPost("/orders/{id:guid}/cancel", async (Guid id, NoteRequest body, OrderAdminService orders,
            HttpContext http, CancellationToken ct) =>
        {
            await orders.CancelAsync(id, http.User.ToActor(), body.Note ?? "", ct);
            return Results.Ok(await orders.GetAsync(id, ct));
        });

        admin.MapGet("/products", (ProductAdminService products, string? search, ProductCategory? category, int? page,
                int? pageSize, CancellationToken ct) =>
            products.ListAsync(search, category, page ?? 1, pageSize ?? 25, ct));

        admin.MapPost("/products", async (CreateProductRequest body, ProductAdminService products, HttpContext http,
            CancellationToken ct) =>
        {
            var id = await products.CreateAsync(body.Product, body.Variants, http.User.ToActor(), ct);
            return Results.Created($"/api/admin/products/{id}", new { id });
        });

        admin.MapGet("/products/{id:guid}/images", (Guid id, ProductImageService images, CancellationToken ct) =>
            images.ListAsync(id, ct));

        admin.MapPost("/products/{id:guid}/images", async (Guid id, IFormFileCollection files,
            ProductImageService images, HttpContext http, CancellationToken ct) =>
        {
            var uploaded = new List<ProductImageDto>();
            foreach (var file in files)
            {
                await using var stream = file.OpenReadStream();
                uploaded.Add(await images.UploadAsync(id, file.FileName, stream, file.Length, http.User.ToActor(), ct));
            }

            return Results.Created($"/api/admin/products/{id}/images", uploaded);
        }).DisableAntiforgery();

        admin.MapPost("/products/{id:guid}/images/{imageId:guid}/main", async (Guid id, Guid imageId,
            ProductImageService images, HttpContext http, CancellationToken ct) =>
        {
            await images.SetMainAsync(id, imageId, http.User.ToActor(), ct);
            return Results.NoContent();
        });

        admin.MapDelete("/products/{id:guid}/images/{imageId:guid}", async (Guid id, Guid imageId,
            ProductImageService images, HttpContext http, CancellationToken ct) =>
        {
            await images.DeleteAsync(id, imageId, http.User.ToActor(), ct);
            return Results.NoContent();
        });

        admin.MapGet("/inventory", (ProductAdminService products, string? search, int? maxAvailable, CancellationToken ct) =>
            products.GetInventoryAsync(search, maxAvailable, ct));

        admin.MapPost("/inventory/{variantId:guid}/adjust", async (Guid variantId, StockAdjustRequest body,
            ProductAdminService products, HttpContext http, CancellationToken ct) =>
            Results.Ok(new { stockOnHand = await products.AdjustStockAsync(variantId, body.Delta, body.Reason, http.User.ToActor(), ct) }));

        admin.MapGet("/payment-settings", (TenantSettingsService settings, CancellationToken ct) =>
            settings.GetPaymentSettingsAsync(ct));

        admin.MapPut("/payment-settings", async (PaymentSettings body, TenantSettingsService settings, HttpContext http,
            CancellationToken ct) =>
        {
            await settings.UpdatePaymentSettingsAsync(body, http.User.ToActor(), ct);
            return Results.NoContent();
        }).RequireAuthorization(ApiPolicies.TenantAdmin);

        return app;
    }
}

public static class SuperAdminEndpoints
{
    /// <summary>Gestión global de empresas. Solo en admin.* y con rol SuperAdmin.</summary>
    public static IEndpointRouteBuilder MapSuperAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/superadmin").WithTags("Super Admin")
            .RequireAuthorization(ApiPolicies.SuperAdmin)
            .AddEndpointFilter(async (ctx, next) =>
                ctx.HttpContext.RequestServices.GetRequiredService<ITenantContext>().IsAdminHost
                    ? await next(ctx)
                    : Results.NotFound());

        group.MapGet("/tenants", (SuperAdminTenantService tenants, string? search, CancellationToken ct) =>
            tenants.ListAsync(search, ct));

        group.MapGet("/stats", (SuperAdminTenantService tenants, CancellationToken ct) => tenants.GetStatsAsync(ct));

        group.MapPost("/tenants", async (CreateTenantRequest request, SuperAdminTenantService tenants, HttpContext http,
            CancellationToken ct) =>
        {
            var id = await tenants.CreateAsync(request, http.User.ToActor(), ct);
            return Results.Created($"/api/superadmin/tenants/{id}", await tenants.GetAsync(id, ct));
        });

        group.MapPut("/tenants/{id:guid}/branding", async (Guid id, UpdateBrandingRequest request,
            SuperAdminTenantService tenants, HttpContext http, CancellationToken ct) =>
        {
            await tenants.UpdateBrandingAsync(id, request, http.User.ToActor(), ct);
            return Results.Ok(await tenants.GetAsync(id, ct));
        });

        group.MapPost("/tenants/{id:guid}/status/{status}", async (Guid id, TenantStatus status,
            SuperAdminTenantService tenants, HttpContext http, CancellationToken ct) =>
        {
            await tenants.SetStatusAsync(id, status, http.User.ToActor(), ct);
            return Results.NoContent();
        });

        return app;
    }
}

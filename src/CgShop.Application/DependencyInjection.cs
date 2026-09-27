using CgShop.Application.Catalog;
using CgShop.Application.Orders;
using CgShop.Application.Tenants;
using Microsoft.Extensions.DependencyInjection;

namespace CgShop.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<CatalogService>();
        services.AddScoped<ProductAdminService>();
        services.AddScoped<ProductImageService>();
        services.AddScoped<CheckoutService>();
        services.AddScoped<CustomerOrderService>();
        services.AddScoped<OrderAdminService>();
        services.AddScoped<ReservationExpiryProcessor>();
        services.AddScoped<SuperAdminTenantService>();
        services.AddScoped<TenantSettingsService>();
        return services;
    }
}

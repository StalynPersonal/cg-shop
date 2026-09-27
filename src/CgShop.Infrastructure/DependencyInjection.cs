using CgShop.Application;
using CgShop.Application.Common;
using CgShop.Application.Tenancy;
using CgShop.Infrastructure.Files;
using CgShop.Infrastructure.Identity;
using CgShop.Infrastructure.Jobs;
using CgShop.Infrastructure.Notifications;
using CgShop.Infrastructure.Persistence;
using CgShop.Infrastructure.Platform;
using CgShop.Application.Platform;
using Microsoft.Extensions.Options;
using CgShop.Infrastructure.Tenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CgShop.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration,
        bool enableBackgroundJobs = true)
    {
        var connectionString = configuration.GetConnectionString("Default")
                               ?? throw new InvalidOperationException("Falta ConnectionStrings:Default.");

        services.AddApplication();
        services.Configure<TenantResolutionOptions>(configuration.GetSection(TenantResolutionOptions.Section));
        services.Configure<FileStorageOptions>(configuration.GetSection(FileStorageOptions.Section));

        // Parámetros de negocio editables por el Super Admin (con valores predeterminados de fábrica).
        // Los IOptions de pedidos y catálogo leen el valor vigente en cada uso.
        services.AddSingleton<IDbContextFactoryAdapter>(new PlatformDbContextFactory(connectionString));
        services.AddSingleton<IPlatformSettingsProvider>(sp => new PlatformSettingsStore(
            sp.GetRequiredService<IDbContextFactoryAdapter>(), sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PlatformSettingsStore>>()));
        services.AddSingleton<IOptions<OrderOptions>>(sp =>
        {
            var provider = sp.GetRequiredService<IPlatformSettingsProvider>();
            return new LiveOptions<OrderOptions>(() => provider.Current.ToOrderOptions());
        });
        services.AddSingleton<IOptions<CgShop.Application.Catalog.CatalogOptions>>(sp =>
        {
            var provider = sp.GetRequiredService<IPlatformSettingsProvider>();
            return new LiveOptions<CgShop.Application.Catalog.CatalogOptions>(() => provider.Current.ToCatalogOptions());
        });

        // Tenancy: un TenantContext por scope (petición HTTP o circuito Blazor).
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<ITenantStore, TenantStore>();
        services.AddMemoryCache();

        // Fábrica SCOPED: cada contexto creado recibe el ITenantContext del scope (petición/circuito),
        // y los servicios crean un contexto corto por operación (recomendado para Blazor Server).
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlServer(connectionString,
                sql => sql.EnableRetryOnFailure(3).MigrationsAssembly(typeof(AppDbContext).Assembly.FullName)),
            ServiceLifetime.Scoped);
        services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());
        services.AddScoped<IAppDbContextFactory, AppDbContextFactory>();

        services.AddDataProtection();
        services.AddHttpContextAccessor();
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                // El correo es único POR TIENDA (ver TenantUserService), no global.
                o.User.RequireUniqueEmail = false;
                o.Password.RequiredLength = 8;
                o.Password.RequireNonAlphanumeric = false;
                o.Lockout.MaxFailedAccessAttempts = 5;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager()
            .AddErrorDescriber<SpanishIdentityErrorDescriber>()
            .AddDefaultTokenProviders();
        services.AddScoped<IUserClaimsPrincipalFactory<ApplicationUser>, CgUserClaimsPrincipalFactory>();
        services.AddScoped<ITenantAdminProvisioner, IdentityTenantAdminProvisioner>();
        services.AddScoped<TenantUserService>();

        services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.AddScoped<INotificationService, LogNotificationService>();
        services.AddSingleton(TimeProvider.System);

        if (enableBackgroundJobs)
            services.AddHostedService<ReservationExpiryJob>();

        services.AddScoped<DataSeeder>();
        return services;
    }
}

using CgShop.Application.Tenancy;
using CgShop.Domain.Tenants;
using CgShop.Infrastructure;
using CgShop.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CgShop.IntegrationTests.Infrastructure;

/// <summary>
/// SQL Server real para pruebas de integración (sin Docker):
/// 1) <c>CGSHOP_TEST_SQL</c> (cadena explícita a cualquier SQL Server),
/// 2) LocalDB con una base temporal única que se elimina al terminar. Aplica las migraciones reales.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private string? _localDbName;

    public string ConnectionString { get; private set; } = "";
    public string Provider { get; private set; } = "";
    public string UploadsPath { get; } = Path.Combine(Path.GetTempPath(), "cgshop-tests", Guid.NewGuid().ToString("N"));
    public IServiceProvider Services { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var explicitConnection = Environment.GetEnvironmentVariable("CGSHOP_TEST_SQL");
        if (!string.IsNullOrWhiteSpace(explicitConnection))
        {
            ConnectionString = explicitConnection;
            Provider = "CGSHOP_TEST_SQL";
        }
        else
        {
            _localDbName = $"CgShopTests_{Guid.NewGuid():N}";
            ConnectionString =
                $@"Server=(localdb)\MSSQLLocalDB;Database={_localDbName};Trusted_Connection=True;TrustServerCertificate=True";
            Provider = "LocalDB";
        }

        Services = BuildServices(ConnectionString, UploadsPath);
        await using var scope = Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<DataSeeder>().SeedAsync(migrate: false); // roles + super admin + demo
    }

    public static IServiceProvider BuildServices(string connectionString, string uploadsPath)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = connectionString,
            ["FileStorage:RootPath"] = uploadsPath,
            ["Orders:ReservationHours"] = "48"
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddInfrastructure(config, enableBackgroundJobs: false);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>Scope con el tenant indicado activo (como lo haría el middleware).</summary>
    public AsyncServiceScope TenantScope(Tenant tenant) => TenantScope(TenantInfo.From(tenant));

    public AsyncServiceScope TenantScope(TenantInfo tenant)
    {
        var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(tenant);
        return scope;
    }

    public AsyncServiceScope NoTenantScope() => Services.CreateAsyncScope();

    public AsyncServiceScope SystemScope()
    {
        var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().EnterSystemScope();
        return scope;
    }

    /// <summary>Crea un tenant nuevo con métodos de pago habilitados.</summary>
    public async Task<Tenant> CreateTenantAsync(string? slug = null, string color = "#1565C0")
    {
        slug ??= $"t-{Guid.NewGuid():N}"[..20];
        var tenant = Tenant.Create($"Empresa {slug}", slug, color);
        tenant.UpdatePaymentSettings(new PaymentSettings
        {
            BankAccounts = [new BankAccount { BankName = "Banco", AccountNumber = "001", AccountHolder = "Empresa SRL" }],
            PaymentLinkUrl = "https://pagos.test/x"
        });
        await using var scope = NoTenantScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant;
    }

    public async Task DisposeAsync()
    {
        if (Services is IAsyncDisposable d)
            await d.DisposeAsync();

        if (_localDbName is not null)
        {
            SqlConnection.ClearAllPools();
            await using var conn = new SqlConnection(@"Server=(localdb)\MSSQLLocalDB;Database=master;Trusted_Connection=True;TrustServerCertificate=True");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER DATABASE [{_localDbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_localDbName}];";
            await cmd.ExecuteNonQueryAsync();
        }

        if (Directory.Exists(UploadsPath))
            Directory.Delete(UploadsPath, recursive: true);
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sqlserver";
}

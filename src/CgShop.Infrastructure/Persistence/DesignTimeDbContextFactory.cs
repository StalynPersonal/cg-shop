using CgShop.Application.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CgShop.Infrastructure.Persistence;

/// <summary>Usada solo por <c>dotnet ef</c> (migraciones).</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("CGSHOP_CONNECTION")
                         ?? @"Server=(localdb)\MSSQLLocalDB;Database=CgShop;Trusted_Connection=True;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options;
        return new AppDbContext(options, new TenantContext());
    }
}

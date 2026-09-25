using CgShop.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace CgShop.Infrastructure.Persistence;

/// <summary>Adaptador de <see cref="IDbContextFactory{TContext}"/> (registrada scoped) a la abstracción de Application.</summary>
public sealed class AppDbContextFactory(IDbContextFactory<AppDbContext> inner) : IAppDbContextFactory
{
    public IAppDbContext CreateDbContext() => inner.CreateDbContext();
}

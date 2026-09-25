using Microsoft.EntityFrameworkCore;

namespace CgShop.Application.Common;

/// <summary>
/// Ejecuta una unidad de trabajo con un DbContext nuevo por intento y reintenta ante
/// conflictos de concurrencia optimista (RowVersion).
/// </summary>
internal static class ConcurrencyRetry
{
    public const int MaxAttempts = 3;

    public static async Task<T> ExecuteAsync<T>(IAppDbContextFactory factory, Func<IAppDbContext, Task<T>> work,
        CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var db = factory.CreateDbContext();
            try
            {
                return await work(db);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(10, 40) * attempt), ct);
            }
        }
    }
}

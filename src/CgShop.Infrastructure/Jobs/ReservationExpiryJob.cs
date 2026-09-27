using CgShop.Application.Common;
using CgShop.Application.Orders;
using CgShop.Application.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CgShop.Infrastructure.Jobs;

/// <summary>Libera periódicamente el stock de órdenes cuyo plazo de pago venció.</summary>
public sealed class ReservationExpiryJob(
    IServiceScopeFactory scopeFactory,
    IOptions<OrderOptions> options,
    ILogger<ReservationExpiryJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantContext>().EnterSystemScope();
                await scope.ServiceProvider.GetRequiredService<ReservationExpiryProcessor>()
                    .ExpireOverdueAsync(ct: stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error en el job de expiración de reservas");
            }

            // La frecuencia se lee en cada vuelta: un cambio del Super Admin aplica sin reiniciar.
            await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, options.Value.ExpiryCheckMinutes)), stoppingToken);
        }
    }
}

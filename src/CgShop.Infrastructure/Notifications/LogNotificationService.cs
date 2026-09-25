using CgShop.Application.Common;
using CgShop.Domain.Orders;
using Microsoft.Extensions.Logging;

namespace CgShop.Infrastructure.Notifications;

/// <summary>Stub: el envío real de correos está fuera de alcance; se deja trazado en el log.</summary>
public sealed class LogNotificationService(ILogger<LogNotificationService> logger) : INotificationService
{
    public Task OrderStatusChangedAsync(Order order, CancellationToken ct = default)
    {
        logger.LogInformation("[Notificación] Orden {Number} para {Email}: {Status}", order.Number,
            order.CustomerEmail, order.Status.DisplayName());
        return Task.CompletedTask;
    }
}

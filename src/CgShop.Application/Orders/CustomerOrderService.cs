using System.Security.Cryptography;
using System.Text;
using CgShop.Application.Common;
using CgShop.Application.Tenancy;
using CgShop.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CgShop.Application.Orders;

/// <summary>
/// Operaciones del cliente final sobre su orden (sin login): consultar estado, ver instrucciones de pago
/// y subir comprobante. El acceso se autoriza con el token secreto de la orden.
/// </summary>
public sealed class CustomerOrderService(
    IAppDbContextFactory dbFactory,
    ITenantContext tenantContext,
    IFileStorage storage,
    TimeProvider clock,
    IOptions<OrderOptions> options,
    ILogger<CustomerOrderService> logger)
{
    public async Task<OrderDetailDto?> GetAsync(string number, string accessToken, CancellationToken ct = default)
    {
        await using var db = dbFactory.CreateDbContext();
        var order = await LoadAsync(db, number, accessToken, tracking: false, ct);
        return order is null ? null : OrderMapper.ToDetail(order);
    }

    public async Task<PaymentInstructionsDto> GetPaymentInstructionsAsync(PaymentMethod method,
        CancellationToken ct = default)
    {
        var tenantInfo = Guard.RequireTenant(tenantContext);
        await using var db = dbFactory.CreateDbContext();
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == tenantInfo.Id, ct);
        var ps = tenant.PaymentSettings;
        return new PaymentInstructionsDto(tenant.Name, tenant.Currency, method,
            method == PaymentMethod.BankTransfer ? ps.BankAccounts : [],
            method == PaymentMethod.PaymentLink ? ps.PaymentLinkUrl : null,
            ps.Instructions,
            ps.PickupAddress,
            ps.WhatsAppNumber);
    }

    public async Task<IReadOnlyList<PaymentMethod>> GetAvailablePaymentMethodsAsync(CancellationToken ct = default)
    {
        var tenantInfo = Guard.RequireTenant(tenantContext);
        await using var db = dbFactory.CreateDbContext();
        var ps = (await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == tenantInfo.Id, ct)).PaymentSettings;
        var methods = new List<PaymentMethod>();
        if (ps.AcceptsBankTransfer) methods.Add(PaymentMethod.BankTransfer);
        if (ps.AcceptsPaymentLink) methods.Add(PaymentMethod.PaymentLink);
        return methods;
    }

    /// <summary>Sube el comprobante de pago (JPG/PNG/PDF ≤ 5 MB) a la carpeta del tenant.</summary>
    public async Task<ReceiptDto> UploadReceiptAsync(string number, string accessToken, string fileName,
        Stream content, long size, string? reference, CancellationToken ct = default)
    {
        var tenantInfo = Guard.RequireTenant(tenantContext);

        // Leer a memoria (máx. 5 MB) para validar firma sin depender de streams no posicionables.
        var max = options.Value.MaxReceiptBytes;
        if (size > max)
            throw new ValidationException([$"El archivo supera el máximo de {max / (1024 * 1024)} MB."]);
        using var buffer = new MemoryStream();
        await CopyWithLimitAsync(content, buffer, max, ct);
        var (ext, contentType) = ReceiptFileValidator.Validate(fileName, buffer.GetBuffer().AsSpan(0, (int)buffer.Length),
            buffer.Length, max);

        await using var db = dbFactory.CreateDbContext();
        var order = await LoadAsync(db, number, accessToken, tracking: true, ct)
                    ?? throw new NotFoundException("Orden no encontrada.");

        buffer.Position = 0;
        var path = await storage.SaveAsync(tenantInfo.Id, "receipts", ext, buffer, ct);
        try
        {
            var safeName = Path.GetFileName(fileName);
            var receipt = order.AttachReceipt(safeName.Length > 200 ? safeName[^200..] : safeName, path,
                contentType, buffer.Length, string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(),
                clock.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Comprobante {Receipt} adjuntado a orden {Number}", receipt.Id, order.Number);
            return OrderMapper.ToDto(receipt);
        }
        catch
        {
            await storage.DeleteAsync(tenantInfo.Id, path, CancellationToken.None);
            throw;
        }
    }

    private static async Task CopyWithLimitAsync(Stream source, Stream target, long max, CancellationToken ct)
    {
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(chunk, ct)) > 0)
        {
            total += read;
            if (total > max)
                throw new ValidationException([$"El archivo supera el máximo de {max / (1024 * 1024)} MB."]);
            await target.WriteAsync(chunk.AsMemory(0, read), ct);
        }
    }

    private static async Task<Order?> LoadAsync(IAppDbContext db, string number, string accessToken, bool tracking,
        CancellationToken ct)
    {
        IQueryable<Order> query = db.Orders.Include(o => o.Items).Include(o => o.History).Include(o => o.Receipts);
        if (!tracking) query = query.AsNoTracking();
        var order = await query.FirstOrDefaultAsync(o => o.Number == number, ct);
        if (order is null)
            return null;

        // Comparación en tiempo constante para no filtrar información por timing.
        var expected = Encoding.ASCII.GetBytes(order.AccessToken);
        var given = Encoding.ASCII.GetBytes(accessToken ?? "");
        return CryptographicOperations.FixedTimeEquals(expected, given) ? order : null;
    }
}

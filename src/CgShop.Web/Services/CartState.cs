using CgShop.Application.Orders;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace CgShop.Web.Services;

/// <summary>
/// Carrito del circuito, persistido cifrado en localStorage del navegador.
/// localStorage es por origen, así que cada subdominio (tienda) tiene su propio carrito.
/// </summary>
public sealed class CartState(ProtectedLocalStorage storage, ILogger<CartState> logger)
{
    private const string Key = "cgshop.cart.v1";
    private List<CartLine> _lines = [];

    public event Action? Changed;

    public bool Loaded { get; private set; }
    public IReadOnlyList<CartLine> Lines => _lines;
    public int Count => _lines.Sum(l => l.Quantity);

    /// <summary>Carga desde el navegador. Solo puede llamarse tras el primer render (JS interop).</summary>
    public async Task EnsureLoadedAsync()
    {
        if (Loaded)
            return;
        try
        {
            var result = await storage.GetAsync<List<CartLine>>(Key);
            _lines = result.Success && result.Value is not null ? result.Value : [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "No se pudo leer el carrito; se reinicia");
            _lines = [];
        }

        Loaded = true;
        Changed?.Invoke();
    }

    public Task AddAsync(Guid variantId, int quantity)
    {
        var existing = _lines.FindIndex(l => l.VariantId == variantId);
        var newQty = Math.Min(CheckoutService.MaxQuantityPerLine,
            (existing >= 0 ? _lines[existing].Quantity : 0) + quantity);
        if (existing >= 0)
            _lines[existing] = _lines[existing] with { Quantity = newQty };
        else
            _lines.Add(new CartLine(variantId, newQty));
        return SaveAsync();
    }

    public Task SetQuantityAsync(Guid variantId, int quantity)
    {
        var index = _lines.FindIndex(l => l.VariantId == variantId);
        if (index < 0)
            return Task.CompletedTask;
        if (quantity <= 0)
            _lines.RemoveAt(index);
        else
            _lines[index] = _lines[index] with { Quantity = Math.Min(quantity, CheckoutService.MaxQuantityPerLine) };
        return SaveAsync();
    }

    public Task RemoveAsync(Guid variantId) => SetQuantityAsync(variantId, 0);

    public Task ClearAsync()
    {
        _lines = [];
        return SaveAsync();
    }

    private async Task SaveAsync()
    {
        Changed?.Invoke();
        try
        {
            await storage.SetAsync(Key, _lines);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "No se pudo guardar el carrito");
        }
    }
}

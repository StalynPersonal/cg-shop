using System.Globalization;
using CgShop.Application.Common;
using CgShop.Domain.Catalog;
using CgShop.Domain.Common;
using CgShop.Domain.Orders;
using MudBlazor;

namespace CgShop.Web.Services;

public static class Ui
{
    private static readonly CultureInfo DominicanCulture = CultureInfo.GetCultureInfo("es-DO");

    public static string Money(decimal amount, string currency = "DOP") =>
        currency == "DOP"
            ? $"RD$ {amount.ToString("N2", DominicanCulture)}"
            : $"{currency} {amount.ToString("N2", DominicanCulture)}";

    public static string Date(DateTime utc) =>
        utc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", DominicanCulture);

    public static string FileSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    public static Color StatusColor(OrderStatus status) => status switch
    {
        OrderStatus.PendingPaymentValidation => Color.Warning,
        OrderStatus.PaymentValidated => Color.Info,
        OrderStatus.Preparing => Color.Primary,
        OrderStatus.Shipped => Color.Secondary,
        OrderStatus.ReadyForPickup => Color.Tertiary,
        OrderStatus.Delivered => Color.Success,
        _ => Color.Error
    };

    public static string StatusIcon(OrderStatus status) => status switch
    {
        OrderStatus.PendingPaymentValidation => Icons.Material.Filled.HourglassTop,
        OrderStatus.PaymentValidated => Icons.Material.Filled.Verified,
        OrderStatus.Preparing => Icons.Material.Filled.Inventory,
        OrderStatus.Shipped => Icons.Material.Filled.LocalShipping,
        OrderStatus.ReadyForPickup => Icons.Material.Filled.Storefront,
        OrderStatus.Delivered => Icons.Material.Filled.CheckCircle,
        OrderStatus.PaymentRejected => Icons.Material.Filled.Block,
        OrderStatus.Expired => Icons.Material.Filled.TimerOff,
        _ => Icons.Material.Filled.Cancel
    };

    public static string CategoryIcon(ProductCategory category) => category switch
    {
        ProductCategory.Clothing => Icons.Material.Filled.Checkroom,
        ProductCategory.Caps => Icons.Material.Filled.Face,
        ProductCategory.Watches => Icons.Material.Filled.Watch,
        ProductCategory.Perfumes => Icons.Material.Filled.Spa,
        ProductCategory.Footwear => Icons.Material.Filled.DirectionsRun,
        _ => Icons.Material.Filled.Category
    };

    public static string CategorySlug(ProductCategory category) => category switch
    {
        ProductCategory.Clothing => "ropa",
        ProductCategory.Caps => "gorras",
        ProductCategory.Watches => "relojes",
        ProductCategory.Perfumes => "perfumes",
        ProductCategory.Footwear => "calzados",
        _ => category.ToString().ToLowerInvariant()
    };

    public static ProductCategory? CategoryFromSlug(string? slug) =>
        Enum.GetValues<ProductCategory>().Cast<ProductCategory?>().FirstOrDefault(c => CategorySlug(c!.Value) == slug);

    /// <summary>Ejecuta una acción mostrando los errores de negocio como Snackbar. Devuelve true si tuvo éxito.</summary>
    public static async Task<bool> TryAsync(this ISnackbar snackbar, Func<Task> action, string? successMessage = null)
    {
        try
        {
            await action();
            if (successMessage is not null)
                snackbar.Add(successMessage, Severity.Success);
            return true;
        }
        catch (ValidationException ex)
        {
            foreach (var error in ex.Errors)
                snackbar.Add(error, Severity.Warning);
        }
        catch (DomainException ex)
        {
            snackbar.Add(ex.Message, Severity.Warning);
        }
        catch (ForbiddenException ex)
        {
            snackbar.Add(ex.Message, Severity.Error);
        }
        catch (NotFoundException ex)
        {
            snackbar.Add(ex.Message, Severity.Error);
        }

        return false;
    }
}

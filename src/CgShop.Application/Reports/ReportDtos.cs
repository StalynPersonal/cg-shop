using CgShop.Domain.Catalog;
using CgShop.Domain.Orders;

namespace CgShop.Application.Reports;

public sealed record SalesSummaryDto(
    int PaidOrders,
    decimal Subtotal,
    decimal Tax,
    decimal Revenue,
    decimal AverageTicket,
    int UnitsSold,
    int PendingOrders,
    int CancelledOrders,
    int RejectedOrders,
    int ExpiredOrders);

public sealed record DailySalesDto(DateOnly Date, int Orders, int Units, decimal Revenue);

public sealed record ProductSalesDto(Guid ProductId, string ProductName, ProductCategory? Category, int Units, decimal Revenue);

public sealed record CategorySalesDto(ProductCategory? Category, int Units, decimal Revenue, decimal Share);

public sealed record BreakdownDto(string Label, int Orders, decimal Revenue);

public sealed record CustomerSalesDto(string Name, string Email, int Orders, decimal Revenue, DateOnly LastPurchase);

public sealed record AdjustmentsByUserDto(string UserName, string? UserRole, int Adjustments, int UnitsIn, int UnitsOut);

/// <summary>Reporte de ventas de un rango de fechas (días completos en la zona horaria de la tienda).</summary>
public sealed record SalesReportDto(
    DateOnly From,
    DateOnly To,
    SalesSummaryDto Summary,
    IReadOnlyList<DailySalesDto> Daily,
    IReadOnlyList<ProductSalesDto> TopProducts,
    IReadOnlyList<CategorySalesDto> ByCategory,
    IReadOnlyList<BreakdownDto> ByPaymentMethod,
    IReadOnlyList<BreakdownDto> ByDelivery,
    IReadOnlyList<CustomerSalesDto> TopCustomers,
    IReadOnlyList<AdjustmentsByUserDto> AdjustmentsByUser);

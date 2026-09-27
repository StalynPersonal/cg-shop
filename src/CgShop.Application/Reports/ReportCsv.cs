using System.Globalization;
using System.Text;
using CgShop.Domain.Catalog;

namespace CgShop.Application.Reports;

/// <summary>Exporta cada sección del reporte a CSV (UTF-8 con BOM para que Excel respete tildes).</summary>
public static class ReportCsv
{
    public static readonly IReadOnlyDictionary<string, string> Sections = new Dictionary<string, string>
    {
        ["resumen"] = "Resumen",
        ["diario"] = "Ventas por día",
        ["productos"] = "Productos más vendidos",
        ["categorias"] = "Ventas por categoría",
        ["pagos"] = "Pago y entrega",
        ["clientes"] = "Mejores clientes",
        ["ajustes"] = "Ajustes de inventario"
    };

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string FileName(string section, DateOnly from, DateOnly to) =>
        $"reporte-{section}-{from:yyyyMMdd}-{to:yyyyMMdd}.csv";

    public static byte[] Build(SalesReportDto r, string section)
    {
        var rows = section switch
        {
            "resumen" => Summary(r),
            "diario" => [["Fecha", "Pedidos", "Unidades", "Ventas"],
                .. r.Daily.Select(d => new[] { Date(d.Date), N(d.Orders), N(d.Units), M(d.Revenue) })],
            "productos" => [["Producto", "Categoría", "Unidades", "Ventas sin ITBIS"],
                .. r.TopProducts.Select(p => new[] { p.ProductName, Category(p.Category), N(p.Units), M(p.Revenue) })],
            "categorias" => [["Categoría", "Unidades", "Ventas sin ITBIS", "% de ventas"],
                .. r.ByCategory.Select(c => new[] { Category(c.Category), N(c.Units), M(c.Revenue), c.Share.ToString("0.0", Inv) })],
            "pagos" => [["Tipo", "Opción", "Pedidos", "Ventas"],
                .. r.ByPaymentMethod.Select(b => new[] { "Pago", b.Label, N(b.Orders), M(b.Revenue) }),
                .. r.ByDelivery.Select(b => new[] { "Entrega", b.Label, N(b.Orders), M(b.Revenue) })],
            "clientes" => [["Cliente", "Correo", "Pedidos", "Total comprado", "Última compra"],
                .. r.TopCustomers.Select(c => new[] { c.Name, c.Email, N(c.Orders), M(c.Revenue), Date(c.LastPurchase) })],
            "ajustes" => [["Usuario", "Rol", "Ajustes", "Unidades que entraron", "Unidades que salieron"],
                .. r.AdjustmentsByUser.Select(a => new[] { a.UserName, a.UserRole ?? "", N(a.Adjustments), N(a.UnitsIn), N(a.UnitsOut) })],
            _ => throw new ArgumentException($"Sección de reporte desconocida: {section}", nameof(section))
        };

        var sb = new StringBuilder();
        sb.AppendLine(Line([$"{Sections[section]} del {Date(r.From)} al {Date(r.To)}"]));
        foreach (var row in rows)
            sb.AppendLine(Line(row));
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static string[][] Summary(SalesReportDto r)
    {
        var s = r.Summary;
        return
        [
            ["Indicador", "Valor"],
            ["Pedidos pagados", N(s.PaidOrders)],
            ["Ventas (con ITBIS)", M(s.Revenue)],
            ["Subtotal", M(s.Subtotal)],
            ["ITBIS", M(s.Tax)],
            ["Ticket promedio", M(s.AverageTicket)],
            ["Unidades vendidas", N(s.UnitsSold)],
            ["Pendientes de pago", N(s.PendingOrders)],
            ["Cancelados", N(s.CancelledOrders)],
            ["Pagos rechazados", N(s.RejectedOrders)],
            ["Vencidos sin pago", N(s.ExpiredOrders)]
        ];
    }

    private static string N(int value) => value.ToString(Inv);
    private static string M(decimal value) => value.ToString("0.00", Inv);
    private static string Date(DateOnly d) => d.ToString("dd/MM/yyyy", Inv);
    private static string Category(ProductCategory? c) => c?.DisplayName() ?? "Producto eliminado";

    /// <summary>Campos entre comillas si llevan coma, comillas o saltos; evita fórmulas (=, +, -, @) al abrir en Excel.</summary>
    private static string Line(IEnumerable<string> fields) => string.Join(",", fields.Select(Escape));

    private static string Escape(string value)
    {
        if (value.Length > 0 && "=+-@".Contains(value[0]) && !decimal.TryParse(value, NumberStyles.Number, Inv, out _))
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }
}

# CG Shop — Prompt consolidado (fuente de verdad)

Actúa como un Arquitecto de Software Senior y Desarrollador Full-Stack .NET. Diseña, estructura y genera el código base, arquitectura inicial y componentes clave para una plataforma de **E-commerce Multi-tenant SaaS** altamente robusta.

## 1. Stack Tecnológico Obligatorio
* **Backend:** .NET 10 (Web API, Entity Framework Core 10).
* **Frontend Cliente & Admin:** Blazor .NET 10 (Interactive Server).
* **UI Library:** MudBlazor.
* **Base de Datos:** SQL Server — Shared Database with Tenant ID.
* **Testing:** xUnit, NSubstitute, FluentAssertions 7.x, bUnit, Microsoft.AspNetCore.Mvc.Testing, SQL Server LocalDB para integración (sin Docker).

## 2. Dominio del Negocio (E-commerce Multicategoría)
Ropa (tallas, colores, categorías), Gorras, Relojes, Perfumes (volúmenes, notas olfativas), Tenis y Calzados (gestión avanzada de tallas y variantes).

## 3. Características Clave y Reglas de Negocio

### A. Multi-tenant & Super Admin
* Resolución de tenant por subdominio (`tenant1.tudominio.com`) o encabezado/claim.
* Aislamiento con `HasQueryFilter` por `TenantId`.
* Panel Super Admin para registrar empresas. **Al crear el tenant es obligatorio definir el color primario**, que se aplica dinámicamente en el MudBlazor Theme Provider de esa tienda sin afectar a las demás.

### B. Panel de Administración por Tenant
* ABM de productos adaptado a las categorías.
* Inventario en tiempo real y seguimiento de órdenes.
* **Validación manual de pagos:** sin pasarelas automáticas; pago por transferencia o enlace externo. La orden nace en `Pendiente de Validación de Pago`; el stock se aparta temporalmente; la orden **no** se aprueba/procesa/despacha hasta que el administrador del tenant valide el comprobante y cambie manualmente el estado a `Pago Validado`.

### C. Carrito y Checkout
* Flujo optimizado con la marca/colores del tenant.
* Pantalla de instrucciones de pago (datos bancarios o link) y subida opcional de comprobante.

### D. Pruebas
* Unitarias: lógica de negocio, stock, transiciones de estado (una orden no pasa a aprobada sin validación del propietario).
* Integración: aislamiento multi-tenant a nivel BD.
* Componentes: renderizado y aplicación del color primario del tenant.

## 4. Entregables
1. Estructura de carpetas y proyectos (Clean Architecture).
2. Código clave: middleware de resolución de tenant, DbContext con filtro global, servicio de tema dinámico MudBlazor, servicio de órdenes con validación manual, ejemplos de pruebas unitarias e integración.
3. Instrucciones de configuración para .NET 10 y SQL Server.

## 5. Decisiones adicionales
* **Auth:** ASP.NET Core Identity + cookies (Blazor) y JWT (API). Roles: SuperAdmin, TenantAdmin, TenantStaff, Customer. Clientes aislados por tenant. Rechazar (403) si el claim `tenant_id` ≠ tenant del subdominio.
* **Blazor:** Interactive Server. Resolver tenant en la request inicial y persistirlo en un `ITenantContext` scoped para el circuito.
* **Catálogo:** Product → ProductVariant (SKU, talla, color, volumen, precio, stock). Atributos específicos por categoría (notas olfativas, material…) en columna JSON.
* **Stock:** reserva al crear orden con expiración configurable (48h); `BackgroundService` libera reservas vencidas; concurrencia con `RowVersion`.
* **Estados:** PendingPaymentValidation → PaymentValidated → Preparing → Shipped → Delivered; alternos: PaymentRejected (libera stock), Cancelled (libera stock), Expired. Transiciones inválidas lanzan `DomainException`. Solo TenantAdmin valida/rechaza pagos.
* **Auditoría:** `OrderStatusHistory` con usuario, fecha, estado origen/destino, nota y comprobante.
* **Comprobantes:** `IFileStorage` (local en dev), ruta por tenant, solo JPG/PNG/PDF ≤ 5 MB con verificación de firma; servidos por endpoint autorizado.
* **Tenant:** Name, Slug, PrimaryColor (hex obligatorio), SecondaryColor/Logo opcionales, estado Activo/Suspendido, TaxRate (ITBIS 18%), moneda DOP, configuración de pago (cuentas bancarias, URL de link de pago). Tenant inexistente/suspendido → 404 propio. Dev: `*.localhost` + header `X-Tenant`.
* **EF Core:** TenantId asignado en `SaveChanges`, bloqueo de escritura cruzada, índices únicos compuestos con TenantId, `IgnoreQueryFilters` solo en servicios de SuperAdmin.
* **Convenciones:** código en inglés, UI en español.
* **Fuera de alcance:** pasarelas automáticas, facturación SaaS, app móvil, envío real de emails (solo `INotificationService` con stub de log).

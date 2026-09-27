# CG Shop — E-commerce Multi-tenant SaaS

Plataforma e-commerce multi-tienda (ropa, gorras, relojes, perfumes, tenis y calzados) con **.NET 10**,
**Blazor Interactive Server + MudBlazor**, **EF Core 10** y **SQL Server** (base compartida con `TenantId`).

- Cada tienda vive en su **subdominio** (`verde.tudominio.com`) y usa su **color primario**, que define el Super Admin al crearla.
- **Sin pasarelas de pago**: el cliente paga por transferencia o enlace externo. El pedido queda en
  *Pendiente de validación de pago* con el stock apartado, y **solo el propietario (TenantAdmin)** puede validarlo manualmente.

El prompt completo con todas las decisiones de diseño está en [docs/PROMPT.md](docs/PROMPT.md).

---

## 1. Arquitectura (Clean Architecture)

```
cg-shop/
├─ CgShop.slnx · Directory.Build.props · Directory.Packages.props (versiones centralizadas)
├─ src/
│  ├─ CgShop.Domain/          Entidades y reglas puras: Tenant, Product/ProductVariant, Order (+máquina de estados)
│  ├─ CgShop.Application/     Casos de uso: catálogo, checkout, validación de pagos, Super Admin, tenancy
│  ├─ CgShop.Infrastructure/  EF Core (filtros globales), Identity, middleware de tenant, archivos, job de expiración
│  ├─ CgShop.Web/             Blazor Interactive Server + MudBlazor (tienda, panel del tenant, Super Admin)
│  └─ CgShop.Api/             Minimal APIs + JWT (misma lógica y mismo middleware de tenant)
└─ tests/
   ├─ Shared/TestData.cs         Generador determinista: cada funcionalidad se prueba con lotes de 100 registros
   ├─ CgShop.UnitTests/          Dominio, servicios (EF InMemory) y middleware
   ├─ CgShop.IntegrationTests/   SQL Server real (LocalDB, sin Docker) + WebApplicationFactory de la API
   └─ CgShop.ComponentTests/     bUnit: tema por tenant y páginas reales de la tienda y el panel
```

### Piezas clave

| Requisito | Implementación |
|---|---|
| Resolución de tenant por subdominio, header o claim | [TenantResolutionMiddleware.cs](src/CgShop.Infrastructure/Tenancy/TenantResolutionMiddleware.cs) |
| Filtro global `TenantId` y bloqueo de escritura cruzada | [AppDbContext.cs](src/CgShop.Infrastructure/Persistence/AppDbContext.cs) |
| Tema MudBlazor por color primario del tenant | [TenantThemeService.cs](src/CgShop.Web/Theming/TenantThemeService.cs), [TenantThemeProvider.razor](src/CgShop.Web/Theming/TenantThemeProvider.razor) |
| Tenant dentro del circuito Blazor | [App.razor](src/CgShop.Web/Components/App.razor) → [Routes.razor](src/CgShop.Web/Components/Routes.razor) |
| Flujo de pedidos y validación manual | [Order.cs](src/CgShop.Domain/Orders/Order.cs), [OrderStateMachine.cs](src/CgShop.Domain/Orders/OrderStateMachine.cs), [OrderAdminService.cs](src/CgShop.Application/Orders/OrderAdminService.cs) |
| Checkout con reserva de stock | [CheckoutService.cs](src/CgShop.Application/Orders/CheckoutService.cs) |
| Alta de empresa con color obligatorio | [SuperAdminTenantService.cs](src/CgShop.Application/Tenants/SuperAdminTenantService.cs), [TenantCreate.razor](src/CgShop.Web/Components/Pages/SuperAdmin/TenantCreate.razor) |

### Aislamiento multi-tenant (defensa en profundidad)

1. **Host**: `verde.*` resuelve el tenant; un tenant inexistente o suspendido responde 404. `admin.*` es la zona del Super Admin.
2. **Autenticación**: la cookie es *host-only*, es decir, una sesión por subdominio. Además, si el claim `tenant_id` del usuario (cookie o JWT) no coincide con el host, la respuesta es **403**.
3. **Datos**: `HasQueryFilter` con nombre (`TenantFilter`) sobre toda `ITenantEntity`. Si no hay tenant, el filtro usa `Guid.Empty` y no devuelve ninguna fila.
4. **Escritura**: `SaveChanges` asigna el `TenantId` y lanza `CrossTenantWriteException` ante cualquier alta, modificación o borrado de filas de otro tenant.
5. **Índices únicos compuestos** (`TenantId + Sku`, `TenantId + Slug`, `TenantId + Number`).
6. `IgnoreQueryFilters()` solo se usa en `SuperAdminTenantService` y en el job de sistema de expiración de reservas.

### Blazor Server y tenancy

El circuito de SignalR tiene su **propio scope de DI**, sin `HttpContext`. `App.razor` (render HTTP) pasa el slug
al componente raíz interactivo `Routes`, cuyos parámetros viajan protegidos. `Routes` reconstruye el `TenantContext`
del circuito y cascadea un `HostContext`. Los servicios usan una **fábrica de DbContext scoped**: crean un contexto
corto por operación que respeta el tenant del circuito y evitan un DbContext de larga vida.

### Ciclo de vida del pedido

```
PendingPaymentValidation ──(TenantAdmin valida)──► PaymentValidated ─► Preparing ─► Shipped ─► Delivered
        │                                                 │               │
        ├─(TenantAdmin rechaza)─► PaymentRejected         └───────────────┴─► Cancelled (reingresa stock)
        ├─(staff cancela)───────► Cancelled   (libera reserva)
        └─(vence la reserva)────► Expired     (job cada 10 min, libera reserva)
```

- Al **crear** el pedido, el stock se **reserva** (`StockReserved`). La concurrencia se controla con `RowVersion` y reintentos, así que nunca hay sobreventa.
- Al **validar** el pago, la reserva pasa a **salida definitiva** (`StockOnHand -= qty`).
- Cada transición queda en `OrderStatusHistory` con usuario, fecha, nota y comprobante.
- Comprobantes: JPG, PNG o PDF de hasta 5 MB. Se valida la **firma binaria**, se guardan en `uploads/{tenantId}/receipts/` y se sirven
  solo por el endpoint autorizado `/admin/comprobantes/{id}`.

---

## 2. Configuración inicial

### Requisitos

- **.NET SDK 10** (`dotnet --version` ≥ 10.0.100; el repo fija 10.0.401 en `global.json` con *roll-forward*).
- **SQL Server** instalado localmente: LocalDB (incluido con Visual Studio o descargable con SQL Server Express),
  o SQL Server Express/Developer. **No se requiere Docker.**
- Herramienta EF: `dotnet tool install -g dotnet-ef` (o `dotnet tool update -g dotnet-ef`).

### Paso a paso

```bash
# 1. Restaurar y compilar
dotnet build CgShop.slnx

# 2. Cadena de conexión (por defecto LocalDB). Para otro servidor, edite
#    src/CgShop.Web/appsettings.json y src/CgShop.Api/appsettings.json -> ConnectionStrings:Default
#    o use user-secrets:
dotnet user-secrets --project src/CgShop.Web set "ConnectionStrings:Default" "Server=.;Database=CgShop;Trusted_Connection=True;TrustServerCertificate=True"

# 3. Crear la base de datos (en Development también se aplica sola al arrancar)
dotnet ef database update --project src/CgShop.Infrastructure --startup-project src/CgShop.Web

# 4. Ejecutar la web (siembra datos demo en Development)
dotnet run --project src/CgShop.Web --launch-profile http
```

Con SQL Server Express o Developer instalado (alternativa a LocalDB):

```bash
# Autenticación de Windows
# ConnectionStrings:Default = "Server=.\SQLEXPRESS;Database=CgShop;Trusted_Connection=True;TrustServerCertificate=True"
# Autenticación SQL
# ConnectionStrings:Default = "Server=localhost;Database=CgShop;User Id=cgshop;Password=...;TrustServerCertificate=True"
```

### Subdominios en desarrollo

Los navegadores resuelven `*.localhost` a 127.0.0.1 sin tocar el archivo *hosts*:

| URL | Qué es |
|---|---|
| http://verde.localhost:5091 | Tienda Verde (tema `#2E7D32`) |
| http://rojo.localhost:5091 | Tienda Roja (tema `#C62828`) |
| http://verde.localhost:5091/admin | Panel del tenant |
| http://admin.localhost:5091 | Panel del Super Admin |
| http://localhost:5091 | Landing de la plataforma |

También se admite `*.lvh.me` (dominio público que apunta a 127.0.0.1). En desarrollo la API acepta además el header `X-Tenant: verde`.

### Usuarios demo (contraseña `Admin123!`)

| Usuario | Rol | Dónde entra |
|---|---|---|
| `superadmin@cgshop.local` | SuperAdmin | admin.localhost |
| `admin@verde.local` / `admin@rojo.local` | TenantAdmin (valida pagos) | verde. / rojo. |
| `staff@verde.local` / `staff@rojo.local` | TenantStaff (catálogo, inventario, despacho) | verde. / rojo. |
| `cliente@verde.local` / `cliente@rojo.local` | Customer (compra y ve sus pedidos) | verde. / rojo. |

**Cuentas de cliente.** Para comprar hay que iniciar sesión. Cualquier persona puede registrarse en
`/cuenta/registro`. Las cuentas son **por tienda**: el mismo correo puede registrarse en tiendas distintas.
Al iniciar sesión, el propietario y los empleados van al panel (`/admin`), y el cliente va a su panel
`/mi-cuenta` ("Mis pedidos") o vuelve a la página de la que venía (por ejemplo, el checkout).

### API

```bash
dotnet run --project src/CgShop.Api --launch-profile http   # http://localhost:5102
```

El archivo [CgShop.Api.http](src/CgShop.Api/CgShop.Api.http) contiene ejemplos: catálogo, token JWT, pedidos pendientes y alta de empresa.
Rutas principales: `/api/auth/token`, `/api/store/*` (pública), `/api/admin/*` (JWT del tenant) y `/api/superadmin/*` (solo `admin.*`).

---

## 3. Pruebas

```bash
dotnet test CgShop.slnx
```

| Proyecto | Qué valida |
|---|---|
| **UnitTests** | Máquina de estados (una orden **no** se aprueba sin el propietario), stock (reservar, liberar, confirmar), branding y colores, servicios con EF InMemory, middleware (404, 403, subdominios) y validación de comprobantes |
| **IntegrationTests** | Aislamiento por `TenantId` en SQL Server real (100 productos por tenant), bloqueo de escritura cruzada, 100 compras concurrentes sin sobreventa, expiración, Super Admin (100 empresas) y API HTTP completa |
| **ComponentTests** | bUnit: 100 tenants, cada uno renderiza su `--mud-palette-primary`; páginas reales de la tienda y el panel con 100 pedidos y productos; validación de pago desde el diálogo |

**Base de datos de las pruebas de integración** (sin Docker):
1. `CGSHOP_TEST_SQL`, si la variable existe (cadena de conexión explícita a cualquier SQL Server).
2. En caso contrario, **LocalDB**, con una base temporal `CgShopTests_{guid}` que se elimina al terminar.

```bash
# Ejemplo contra SQL Server Express (PowerShell)
$env:CGSHOP_TEST_SQL = "Server=.\SQLEXPRESS;Database=CgShopTests;Trusted_Connection=True;TrustServerCertificate=True"
dotnet test tests/CgShop.IntegrationTests
```

---

## 4. Producción

- **DNS comodín** `*.tudominio.com` y certificado TLS comodín. Configure `Tenancy:RootDomains = ["tudominio.com"]`.
- `Tenancy:AllowHeaderFallback = false` en la web. En la API, actívelo solo detrás de un gateway de confianza.
- `Jwt:Key` (≥ 32 caracteres) y la cadena de conexión deben venir de **secretos** (Key Vault o variables de entorno), no de `appsettings`.
- Data Protection: persista las claves (por ejemplo en Azure Blob o Redis) si hay varias instancias. Blazor Server necesita *sticky sessions*.
- `IFileStorage` tiene una implementación local. Para producción, implemente la variante en Azure Blob o S3 respetando el prefijo por tenant.
- Envío de correos: `INotificationService` es un stub que escribe en el log (fuera de alcance).

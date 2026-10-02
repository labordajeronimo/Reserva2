# 📅 Reserva2 — Plataforma de Gestión de Turnos

Plataforma SaaS multi-tenant de reservas de turnos online. Cada negocio
(peluquería, barbería, estudio de tatuajes, cancha de fútbol 5, etc.) gestiona
su propia agenda desde un panel aislado y comparte un link público
(`reservados2.com/tu-negocio`) donde sus clientes reservan turnos sin necesidad
de crear cuenta.

*Desarrollado en Rosario, Santa Fe, Argentina — proyecto de diploma de
Jerónimo Laborda (Ingeniería en Sistemas).*

## 🚀 Arquitectura del proyecto

Monorepo con dos partes:

- **`Backend/Reserva2.Api`** — C# / .NET 10, API REST minimal API + Entity
  Framework Core sobre SQL Server.
- **`Frontend/reserva2-app`** — Angular (standalone components, señales,
  control flow `@if`/`@for`), sin recargas de página.

## 👥 Roles del sistema

| Rol | Quién | Accede a |
|---|---|---|
| **Super Admin** | Jerónimo (dueño de la plataforma) | `/super-admin` — métricas globales, activar/pausar comercios, cambiar su plan |
| **Admin Cliente** | Dueño de cada negocio (paga la suscripción) | `/panel` — sus turnos, servicios, profesionales y horarios |
| **Usuario Final** | Cliente que reserva un turno | `/tu-negocio-alias` — sin login, elige servicio/profesional, horario y deja sus datos |

## 💳 Planes de suscripción

| | Gratuito | Básico — $15.000/mes | Premium — $25.000/mes |
|---|---|---|---|
| Profesionales/agendas | 1 | hasta 2 | ilimitados |
| Turnos por mes | hasta 60 | ilimitados | ilimitados |
| Recordatorios por WhatsApp | ❌ | ❌ | ✅ |
| Cobro de seña automático (MercadoPago) | add-on opcional, no incluido por defecto en ningún plan |

Si un comercio no renueva el pago, el Super Admin lo marca como **Inactivo**:
el link público muestra "esta agenda no está disponible temporalmente" pero
no se pierde ningún dato — el panel del dueño sigue funcionando.

## 🔌 API — resumen de endpoints

**Auth**
`POST /api/auth/register` · `POST /api/auth/login` · `POST /api/auth/super-admin/login`

**Super Admin** (requiere rol `SuperAdmin`)
`GET /api/admin/metricas` · `GET /api/comercios` · `PATCH /api/comercios/{id}/estado` · `PATCH /api/comercios/{id}/plan`

**Admin Cliente** (requiere rol `AdminCliente`, dueño del recurso)
`POST/GET/DELETE /api/servicios` · `POST/GET/DELETE /api/comercios/{id}/profesionales` ·
`POST/GET/DELETE /api/comercios/{id}/horarios` · `GET /api/comercios/{id}/turnos` ·
`PATCH /api/turnos/{id}/confirmar` · `PATCH /api/turnos/{id}/cancelar`

**Público** (sin login)
`GET /api/comercios/alias/{alias}` · `GET /api/comercios/{id}/profesionales` ·
`GET /api/comercios/{id}/disponibilidad` · `POST /api/turnos`


## 💸 Mercado Pago

Dos usos, cada uno con su cuenta:

- **Seña de los turnos**: cada comercio conecta *su* cuenta desde Perfil
  (OAuth) y elige si la seña se paga por Mercado Pago, por transferencia con
  comprobante, o las dos. La plata va directo al comercio. El turno queda
  apartado 30 minutos y se confirma solo cuando Mercado Pago aprueba el pago.
- **Plan del comercio**: desde Mi plan, el comercio le paga a Reserva2 con
  Checkout Pro. Al aprobarse se renueva (o cambia) el plan, se reactiva el
  comercio, queda en el historial de pagos y llega un mail al Super Admin para
  emitir la Factura C.

Variables de entorno del servicio `reserva2-api` (nunca en el repo):

| Variable | Qué es |
|---|---|
| `MercadoPago__ClientId` / `MercadoPago__ClientSecret` | De la aplicación de Reserva2 en Mercado Pago Developers (para conectar las cuentas de los comercios). Redirect URL de la app: `https://reservados2.com/api/mercadopago/oauth/callback` |
| `MercadoPago__AccessToken` | Access token de producción de la cuenta de Reserva2 (cobro de planes) |
| `DataProtection__Ruta` | Carpeta persistente para las claves que cifran los tokens de los comercios, ej. `/var/lib/reserva2/claves`. Si se pierde, cada comercio tiene que volver a conectar su cuenta |

Webhooks (los arma la API sola en cada pago, no hace falta configurarlos):
`POST /api/mercadopago/webhook/senias` y `POST /api/mercadopago/webhook/planes`.

## 📌 Estado actual y roadmap

Ver [`spec-pendiente.md`](./spec-pendiente.md) para el detalle completo de lo
que falta. Resumen rápido — **ya construido**: modelo de datos completo
(Profesional, planes, estado activo/inactivo), JWT con roles, límites por
plan (turnos/mes, cantidad de profesionales), panel de Admin Cliente completo
(turnos, servicios, profesionales, horarios), panel de Super Admin (métricas
+ activar/pausar/cambiar plan), página pública con disponibilidad
multi-profesional. **Pendiente**: integración real con WhatsApp Cloud API,
integración real con MercadoPago para el add-on de cobro automático,
expiración de tokens JWT, guards de ruta en el frontend (hoy si entrás a
`/panel` sin sesión ves una pantalla de login en vez de que te redirija
sola), tests automatizados.

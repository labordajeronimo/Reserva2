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

# Reserva2 — Lo que falta construir (spec técnica)

Este documento traduce el plan de negocio completo (roles, planes de suscripción,
multi-profesional) a cambios concretos sobre el código que ya existe. Es la
lista de referencia para seguir construyendo, acá o en Claude Code.

## 1. Qué cambia del modelo de datos

**`Comercio`** — agregar:
- `Activo` (bool, default true) — lo apaga el Super Admin si no paga
- `PlanActual` (string o int: "Gratuito" | "Basico" | "Premium")
- `FechaProximoPago` (DateTime?)

**`Servicio`** — agregar:
- `Precio` (decimal) — precio total del servicio
- `MontoSeña` pasa a ser **opcional** (nullable `decimal?`) — hoy es obligatorio

**`Turno`** — agregar:
- `ClienteEmail` (string) — hoy el cliente final solo deja nombre + WhatsApp

**`Profesional`** (entidad nueva) — para Básico (1-2) y Premium (ilimitados):
- `Id`, `ComercioId`, `Nombre`
- `Horario` pasa a tener `ProfesionalId` (hoy cuelga directo de `ComercioId`)
- `Turno` pasa a tener `ProfesionalId` (nullable, para no romper comercios de
  un solo profesional)
- El Plan Gratuito sigue limitado a 1 solo profesional (se valida en el backend,
  no hace falta un campo aparte — simplemente no dejar crear un 2do si
  `PlanActual == "Gratuito"`)

**`Plan`** (tabla de referencia, no haría falta si se hardcodean los 3 planes
en el backend — más simple para el tamaño actual del proyecto. Si más adelante
el Super Admin necesita crear/editar planes desde el panel, ahí sí conviene
una tabla)

## 2. Reglas de negocio nuevas a validar en el backend

- **Tope de 30 turnos/mes en el plan Gratuito**: al crear un turno, contar
  cuántos turnos tiene el comercio en el mes calendario actual (cualquier
  estado excepto Cancelado) y rechazar si `PlanActual == "Gratuito"` y ya
  llegó a 30.
- **Tope de profesionales por plan**: Gratuito = 1, Básico = 2, Premium =
  ilimitado. Se valida al crear un `Profesional`.
- **Recordatorios por WhatsApp solo en Premium**: en Gratuito/Básico el punto
  de enganche de notificaciones no debería dispararse (o debería usar el
  canal de Email en Básico — ver más abajo).
- **Comercio inactivo bloquea el link público**: `GET /api/comercios/alias/{alias}`
  debe devolver algo como 403 con un mensaje genérico si `Activo == false`,
  y la vista pública debe mostrar "Esta agenda no está disponible
  temporalmente" en vez de la grilla de turnos. El panel del Admin Cliente
  sigue funcionando igual (no pierde datos), solo no puede recibir turnos
  nuevos.

## 3. Endpoints nuevos o modificados

- `PATCH /api/comercios/{id}/estado` — Super Admin activa/pausa una tienda
- `GET /api/admin/metricas` — cantidad de comercios, activos/inactivos,
  turnos totales del mes (para el dashboard del Super Admin)
- `GET /api/admin/comercios` — listado completo con plan y estado (a
  diferencia de `GET /api/comercios`, que hoy no filtra nada — hay que
  decidir si ese endpoint general queda solo para Super Admin o se saca)
- `POST /api/comercios/{id}/profesionales`, `GET .../profesionales`,
  `DELETE /api/profesionales/{id}`
- `Horario` y la lógica de disponibilidad pasan a filtrar por
  `ProfesionalId` en vez de solo `ComercioId`
- `POST /api/turnos` — sumar `ClienteEmail` al body, y el chequeo de tope
  mensual del plan Gratuito

**Importante sobre seguridad**: hoy el login devuelve solo
`{comercioId, nombre, aliasUrl}` sin token — alcanza para que un Admin
Cliente use su propio panel, pero **no alcanza para el Super Admin**: sin un
JWT real con un rol adentro, cualquiera podría llamar a mano a
`/api/admin/metricas` o `/api/comercios/{id}/estado` sin loguearse. Antes de
construir el panel de Super Admin hay que meter autenticación de verdad
(JWT con claim de rol), no la sesión simple de localStorage que se armó
para el MVP del Admin Cliente.

## 4. Vistas nuevas en Angular

- **`pages/super-admin/`** (ruta `/super-admin`, protegida por rol):
  - Dashboard con métricas globales (tarjetas: comercios activos, turnos
    del mes, etc.)
  - Tabla de comercios con su plan y estado, botón para activar/pausar
- **`pages/panel/`**: agregar una pestaña "Profesionales" (crear/listar,
  cada uno con sus propios Horarios) — los Horarios y la disponibilidad
  pública pasan a pedir también `profesionalId`
- **`pages/reserva-publica/`**: el formulario final suma el campo Email;
  si el comercio está inactivo, mostrar el estado "no disponible" en vez
  del flujo de reserva
- **Landing**: las 3 cards de precio deberían reflejar los límites reales
  (30 turnos/mes en el Gratuito, 1-2 profesionales en el Básico, etc.) en
  vez del texto genérico que tienen hoy

## 5. Orden sugerido para construir esto

1. Modelo de datos + migración (Comercio.Activo/PlanActual, Servicio.Precio,
   Turno.ClienteEmail, entidad Profesional)
2. JWT real con rol (Admin Cliente vs Super Admin) — bloquea todo lo demás
   de Super Admin si no está
3. Endpoints de Profesional + disponibilidad multi-profesional
4. Endpoints y lógica de límites por plan (tope turnos, tope profesionales)
5. Panel del Admin Cliente: pestaña Profesionales
6. Vista pública: campo Email + estado "no disponible"
7. Panel de Super Admin (nuevo, de cero)
8. Actualizar el copy de las cards de precio en la landing para que sea
   consistente con los límites reales

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reserva2.Api.Migrations
{
    /// <summary>
    /// Seguridad por filas (Row-Level Security) en SQL Server: cuando la conexión trae
    /// SESSION_CONTEXT('ComercioId') (lo fija la API en los pedidos del panel de un comercio,
    /// ver Seguridad/SeguridadPorFilas.cs), solo se ven y solo se pueden escribir filas de ese
    /// comercio. Sin SESSION_CONTEXT (página pública, webhooks, Super Admin) no restringe nada.
    /// No toca datos: solo agrega la función y la política.
    /// Para apagarla de emergencia sin borrar nada:
    ///   ALTER SECURITY POLICY seguridad.PoliticaComercio WITH (STATE = OFF);
    /// </summary>
    public partial class AgregarSeguridadPorFilas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("EXEC(N'CREATE SCHEMA [seguridad]');");

            // Van dentro de EXEC: CREATE FUNCTION / SECURITY POLICY tienen que ser lo primero de
            // su lote, y el script idempotente los mete adentro de un IF.
            migrationBuilder.Sql(@"EXEC(N'
CREATE FUNCTION [seguridad].[fn_AccesoComercio](@ComercioId int)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT 1 AS Permitido
    WHERE SESSION_CONTEXT(N''ComercioId'') IS NULL
       OR @ComercioId = CAST(SESSION_CONTEXT(N''ComercioId'') AS int);');");

            migrationBuilder.Sql(@"EXEC(N'
CREATE SECURITY POLICY [seguridad].[PoliticaComercio]
    ADD FILTER PREDICATE [seguridad].[fn_AccesoComercio]([Id]) ON [dbo].[Comercios],
    ADD BLOCK PREDICATE [seguridad].[fn_AccesoComercio]([Id]) ON [dbo].[Comercios] AFTER INSERT,
    ADD BLOCK PREDICATE [seguridad].[fn_AccesoComercio]([Id]) ON [dbo].[Comercios] AFTER UPDATE,
    ADD FILTER PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[Servicios],
    ADD BLOCK PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[Servicios] AFTER INSERT,
    ADD BLOCK PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[Servicios] AFTER UPDATE,
    ADD FILTER PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[Profesionales],
    ADD BLOCK PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[Profesionales] AFTER INSERT,
    ADD BLOCK PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[Profesionales] AFTER UPDATE,
    ADD FILTER PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[Sucursales],
    ADD BLOCK PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[Sucursales] AFTER INSERT,
    ADD BLOCK PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[Sucursales] AFTER UPDATE,
    ADD FILTER PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[Horarios],
    ADD BLOCK PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[Horarios] AFTER INSERT,
    ADD BLOCK PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[Horarios] AFTER UPDATE,
    ADD FILTER PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[Turnos],
    ADD BLOCK PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[Turnos] AFTER INSERT,
    ADD BLOCK PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[Turnos] AFTER UPDATE,
    ADD FILTER PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[WhatsAppConfigs],
    ADD BLOCK PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[WhatsAppConfigs] AFTER INSERT,
    ADD BLOCK PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[WhatsAppConfigs] AFTER UPDATE,
    ADD FILTER PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[Pagos],
    ADD BLOCK PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[Pagos] AFTER INSERT,
    ADD BLOCK PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[Pagos] AFTER UPDATE,
    ADD FILTER PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[MensajesWhatsApp],
    ADD BLOCK PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[MensajesWhatsApp] AFTER INSERT,
    ADD BLOCK PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[MensajesWhatsApp] AFTER UPDATE,
    ADD FILTER PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[PasswordResetTokens],
    ADD BLOCK PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[PasswordResetTokens] AFTER INSERT,
    ADD BLOCK PREDICATE [seguridad].[fn_AccesoComercio]([ComercioId]) ON [dbo].[PasswordResetTokens] AFTER UPDATE
WITH (STATE = ON, SCHEMABINDING = ON);');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP SECURITY POLICY IF EXISTS [seguridad].[PoliticaComercio];");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS [seguridad].[fn_AccesoComercio];");
            migrationBuilder.Sql("DROP SCHEMA IF EXISTS [seguridad];");
        }
    }
}

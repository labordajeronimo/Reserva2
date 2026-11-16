BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116023036_AgregarSeguridadPorFilas'
)
BEGIN
    EXEC(N'CREATE SCHEMA [seguridad]');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116023036_AgregarSeguridadPorFilas'
)
BEGIN
    EXEC(N'
    CREATE FUNCTION [seguridad].[fn_AccesoComercio](@ComercioId int)
    RETURNS TABLE
    WITH SCHEMABINDING
    AS
    RETURN
        SELECT 1 AS Permitido
        WHERE SESSION_CONTEXT(N''ComercioId'') IS NULL
           OR @ComercioId = CAST(SESSION_CONTEXT(N''ComercioId'') AS int);');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116023036_AgregarSeguridadPorFilas'
)
BEGIN
    EXEC(N'
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
    WITH (STATE = ON, SCHEMABINDING = ON);');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261116023036_AgregarSeguridadPorFilas'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261116023036_AgregarSeguridadPorFilas', N'10.0.15');
END;

COMMIT;
GO


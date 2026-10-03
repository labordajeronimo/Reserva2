BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261002060302_AgregarMercadoPagoYPagos'
)
BEGIN
    ALTER TABLE [Turnos] ADD [MercadoPagoPagoId] bigint NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261002060302_AgregarMercadoPagoYPagos'
)
BEGIN
    ALTER TABLE [Turnos] ADD [MontoMercadoPago] decimal(18,2) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261002060302_AgregarMercadoPagoYPagos'
)
BEGIN
    ALTER TABLE [Turnos] ADD [SeñaMedio] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261002060302_AgregarMercadoPagoYPagos'
)
BEGIN
    ALTER TABLE [Comercios] ADD [AddonCobrosOnline] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261002060302_AgregarMercadoPagoYPagos'
)
BEGIN
    ALTER TABLE [Comercios] ADD [CobroMercadoPagoTotal] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261002060302_AgregarMercadoPagoYPagos'
)
BEGIN
    ALTER TABLE [Comercios] ADD [MercadoPagoAccessToken] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261002060302_AgregarMercadoPagoYPagos'
)
BEGIN
    ALTER TABLE [Comercios] ADD [MercadoPagoRefreshToken] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261002060302_AgregarMercadoPagoYPagos'
)
BEGIN
    ALTER TABLE [Comercios] ADD [MercadoPagoTokenVence] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261002060302_AgregarMercadoPagoYPagos'
)
BEGIN
    ALTER TABLE [Comercios] ADD [MercadoPagoUserId] bigint NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261002060302_AgregarMercadoPagoYPagos'
)
BEGIN
    ALTER TABLE [Comercios] ADD [SeñaPorMercadoPago] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261002060302_AgregarMercadoPagoYPagos'
)
BEGIN
    ALTER TABLE [Comercios] ADD [SeñaPorTransferencia] bit NOT NULL DEFAULT CAST(1 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261002060302_AgregarMercadoPagoYPagos'
)
BEGIN
    CREATE TABLE [Pagos] (
        [Id] int NOT NULL IDENTITY,
        [ComercioId] int NOT NULL,
        [Fecha] datetime2 NOT NULL,
        [Monto] decimal(18,2) NOT NULL,
        [Plan] nvarchar(max) NOT NULL,
        [Ciclo] nvarchar(max) NOT NULL,
        [Medio] nvarchar(max) NOT NULL,
        [Estado] nvarchar(max) NOT NULL,
        [MercadoPagoPagoId] bigint NULL,
        [FechaAprobacion] datetime2 NULL,
        CONSTRAINT [PK_Pagos] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261002060302_AgregarMercadoPagoYPagos'
)
BEGIN
    CREATE INDEX [IX_Pagos_ComercioId] ON [Pagos] ([ComercioId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261002060302_AgregarMercadoPagoYPagos'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261002060302_AgregarMercadoPagoYPagos', N'10.0.11');
END;

COMMIT;
GO


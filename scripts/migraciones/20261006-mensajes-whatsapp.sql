BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006231154_AgregarMensajesWhatsApp'
)
BEGIN
    CREATE TABLE [MensajesWhatsApp] (
        [Id] int NOT NULL IDENTITY,
        [ComercioId] int NOT NULL,
        [TurnoId] int NULL,
        [Plantilla] nvarchar(max) NOT NULL,
        [FechaEnvio] datetime2 NOT NULL,
        [MetaMensajeId] nvarchar(max) NULL,
        CONSTRAINT [PK_MensajesWhatsApp] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006231154_AgregarMensajesWhatsApp'
)
BEGIN
    CREATE INDEX [IX_MensajesWhatsApp_ComercioId_FechaEnvio] ON [MensajesWhatsApp] ([ComercioId], [FechaEnvio]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261006231154_AgregarMensajesWhatsApp'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261006231154_AgregarMensajesWhatsApp', N'10.0.11');
END;

COMMIT;
GO


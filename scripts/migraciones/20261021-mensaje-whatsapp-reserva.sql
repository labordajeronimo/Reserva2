BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021040037_AgregarMensajeWhatsAppReserva'
)
BEGIN
    ALTER TABLE [Comercios] ADD [MensajeWhatsAppReserva] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261021040037_AgregarMensajeWhatsAppReserva'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261021040037_AgregarMensajeWhatsAppReserva', N'10.0.11');
END;

COMMIT;
GO


BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261003012643_AgregarOrigenATurno'
)
BEGIN
    ALTER TABLE [Turnos] ADD [Origen] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261003012643_AgregarOrigenATurno'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261003012643_AgregarOrigenATurno', N'10.0.11');
END;

COMMIT;
GO


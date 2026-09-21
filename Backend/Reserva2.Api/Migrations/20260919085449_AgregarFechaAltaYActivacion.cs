using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reserva2.Api.Migrations
{
    /// <inheritdoc />
    public partial class AgregarFechaAltaYActivacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FechaActivacion",
                table: "Comercios",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FechaAlta",
                table: "Comercios",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // Backfill: los comercios que ya existían no tienen forma de recuperar su fecha
            // de alta real, así que quedan con la fecha de esta migración (mejor que 0001-
            // 01-01, que rompería cualquier cálculo de antigüedad). A los que ya están
            // activos se les marca también la fecha de activación, para que no queden
            // clasificados como "pendientes" sin haberlo estado nunca.
            migrationBuilder.Sql(@"
                UPDATE Comercios SET FechaAlta = GETUTCDATE() WHERE FechaAlta = '0001-01-01';
                UPDATE Comercios SET FechaActivacion = GETUTCDATE() WHERE Activo = 1 AND FechaActivacion IS NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FechaActivacion",
                table: "Comercios");

            migrationBuilder.DropColumn(
                name: "FechaAlta",
                table: "Comercios");
        }
    }
}

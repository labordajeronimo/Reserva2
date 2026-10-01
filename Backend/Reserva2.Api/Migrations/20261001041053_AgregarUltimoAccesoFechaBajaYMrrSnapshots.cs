using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reserva2.Api.Migrations
{
    /// <inheritdoc />
    public partial class AgregarUltimoAccesoFechaBajaYMrrSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FechaBaja",
                table: "Comercios",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UltimoAcceso",
                table: "Comercios",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MrrSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Anio = table.Column<int>(type: "int", nullable: false),
                    Mes = table.Column<int>(type: "int", nullable: false),
                    Monto = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    FechaActualizacion = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MrrSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MrrSnapshots_Anio_Mes",
                table: "MrrSnapshots",
                columns: new[] { "Anio", "Mes" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MrrSnapshots");

            migrationBuilder.DropColumn(
                name: "FechaBaja",
                table: "Comercios");

            migrationBuilder.DropColumn(
                name: "UltimoAcceso",
                table: "Comercios");
        }
    }
}

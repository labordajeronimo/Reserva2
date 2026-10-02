using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reserva2.Api.Migrations
{
    /// <inheritdoc />
    public partial class AgregarMercadoPagoYPagos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "MercadoPagoPagoId",
                table: "Turnos",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MontoMercadoPago",
                table: "Turnos",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SeñaMedio",
                table: "Turnos",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CobroMercadoPagoTotal",
                table: "Comercios",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MercadoPagoAccessToken",
                table: "Comercios",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MercadoPagoRefreshToken",
                table: "Comercios",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MercadoPagoTokenVence",
                table: "Comercios",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MercadoPagoUserId",
                table: "Comercios",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SeñaPorMercadoPago",
                table: "Comercios",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SeñaPorTransferencia",
                table: "Comercios",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "Pagos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ComercioId = table.Column<int>(type: "int", nullable: false),
                    Fecha = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Monto = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Plan = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Ciclo = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Medio = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Estado = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MercadoPagoPagoId = table.Column<long>(type: "bigint", nullable: true),
                    FechaAprobacion = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pagos", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Pagos_ComercioId",
                table: "Pagos",
                column: "ComercioId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Pagos");

            migrationBuilder.DropColumn(
                name: "MercadoPagoPagoId",
                table: "Turnos");

            migrationBuilder.DropColumn(
                name: "MontoMercadoPago",
                table: "Turnos");

            migrationBuilder.DropColumn(
                name: "SeñaMedio",
                table: "Turnos");

            migrationBuilder.DropColumn(
                name: "CobroMercadoPagoTotal",
                table: "Comercios");

            migrationBuilder.DropColumn(
                name: "MercadoPagoAccessToken",
                table: "Comercios");

            migrationBuilder.DropColumn(
                name: "MercadoPagoRefreshToken",
                table: "Comercios");

            migrationBuilder.DropColumn(
                name: "MercadoPagoTokenVence",
                table: "Comercios");

            migrationBuilder.DropColumn(
                name: "MercadoPagoUserId",
                table: "Comercios");

            migrationBuilder.DropColumn(
                name: "SeñaPorMercadoPago",
                table: "Comercios");

            migrationBuilder.DropColumn(
                name: "SeñaPorTransferencia",
                table: "Comercios");
        }
    }
}

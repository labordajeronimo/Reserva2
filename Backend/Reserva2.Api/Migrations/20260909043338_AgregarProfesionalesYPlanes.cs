using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reserva2.Api.Migrations
{
    /// <inheritdoc />
    public partial class AgregarProfesionalesYPlanes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClienteEmail",
                table: "Turnos",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ProfesionalId",
                table: "Turnos",
                type: "int",
                nullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "MontoSeña",
                table: "Servicios",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)");

            migrationBuilder.AddColumn<decimal>(
                name: "Precio",
                table: "Servicios",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "ProfesionalId",
                table: "Horarios",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Activo",
                table: "Comercios",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FechaProximoPago",
                table: "Comercios",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlanActual",
                table: "Comercios",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "Gratuito");

            migrationBuilder.CreateTable(
                name: "Profesionales",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ComercioId = table.Column<int>(type: "int", nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Profesionales", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Profesionales");

            migrationBuilder.DropColumn(
                name: "ClienteEmail",
                table: "Turnos");

            migrationBuilder.DropColumn(
                name: "ProfesionalId",
                table: "Turnos");

            migrationBuilder.DropColumn(
                name: "Precio",
                table: "Servicios");

            migrationBuilder.DropColumn(
                name: "ProfesionalId",
                table: "Horarios");

            migrationBuilder.DropColumn(
                name: "Activo",
                table: "Comercios");

            migrationBuilder.DropColumn(
                name: "FechaProximoPago",
                table: "Comercios");

            migrationBuilder.DropColumn(
                name: "PlanActual",
                table: "Comercios");

            migrationBuilder.AlterColumn<decimal>(
                name: "MontoSeña",
                table: "Servicios",
                type: "decimal(18,2)",
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "decimal(18,2)",
                oldPrecision: 18,
                oldScale: 2,
                oldNullable: true);
        }
    }
}

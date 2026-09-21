using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reserva2.Api.Migrations
{
    /// <inheritdoc />
    public partial class AgregarCantidadesContratadas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CantidadProfesionalesContratada",
                table: "Comercios",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CantidadSucursalesContratada",
                table: "Comercios",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Backfill: los comercios que ya existían quedan en 1/1 (el default del modelo)
            // en vez de en 0, que no tiene sentido para algo que ya está operando.
            migrationBuilder.Sql(@"
                UPDATE Comercios SET CantidadProfesionalesContratada = 1 WHERE CantidadProfesionalesContratada = 0;
                UPDATE Comercios SET CantidadSucursalesContratada = 1 WHERE CantidadSucursalesContratada = 0;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CantidadProfesionalesContratada",
                table: "Comercios");

            migrationBuilder.DropColumn(
                name: "CantidadSucursalesContratada",
                table: "Comercios");
        }
    }
}

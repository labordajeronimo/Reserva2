using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reserva2.Api.Migrations
{
    /// <inheritdoc />
    public partial class AgregarComprobanteYSeniaVerificadaATurno : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ComprobanteArchivo",
                table: "Turnos",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SeñaVerificada",
                table: "Turnos",
                type: "bit",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ComprobanteArchivo",
                table: "Turnos");

            migrationBuilder.DropColumn(
                name: "SeñaVerificada",
                table: "Turnos");
        }
    }
}

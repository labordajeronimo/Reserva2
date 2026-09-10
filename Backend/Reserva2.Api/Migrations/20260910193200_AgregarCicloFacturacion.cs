using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reserva2.Api.Migrations
{
    /// <inheritdoc />
    public partial class AgregarCicloFacturacion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CicloFacturacion",
                table: "Comercios",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "Mensual");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CicloFacturacion",
                table: "Comercios");
        }
    }
}

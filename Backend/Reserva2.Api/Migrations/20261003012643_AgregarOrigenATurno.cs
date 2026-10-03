using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reserva2.Api.Migrations
{
    /// <inheritdoc />
    public partial class AgregarOrigenATurno : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Origen",
                table: "Turnos",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Origen",
                table: "Turnos");
        }
    }
}

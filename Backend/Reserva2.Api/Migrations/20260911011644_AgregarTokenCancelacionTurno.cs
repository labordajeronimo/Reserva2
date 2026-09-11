using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reserva2.Api.Migrations
{
    /// <inheritdoc />
    public partial class AgregarTokenCancelacionTurno : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TokenCancelacion",
                table: "Turnos",
                type: "nvarchar(450)",
                nullable: false,
                defaultValue: "");

            // Los turnos ya existentes quedaron todos con el mismo valor por defecto (""),
            // lo que rompería el índice único de abajo. Les generamos un valor único a cada uno.
            migrationBuilder.Sql("UPDATE Turnos SET TokenCancelacion = CONVERT(nvarchar(450), NEWID()) WHERE TokenCancelacion = '';");

            migrationBuilder.CreateIndex(
                name: "IX_Turnos_TokenCancelacion",
                table: "Turnos",
                column: "TokenCancelacion",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Turnos_TokenCancelacion",
                table: "Turnos");

            migrationBuilder.DropColumn(
                name: "TokenCancelacion",
                table: "Turnos");
        }
    }
}

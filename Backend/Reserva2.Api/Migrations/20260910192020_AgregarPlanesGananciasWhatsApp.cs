using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reserva2.Api.Migrations
{
    /// <inheritdoc />
    public partial class AgregarPlanesGananciasWhatsApp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "MontoMensualAcordado",
                table: "Comercios",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WhatsAppConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ComercioId = table.Column<int>(type: "int", nullable: false),
                    Activado = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WhatsAppConfigs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppConfigs_ComercioId",
                table: "WhatsAppConfigs",
                column: "ComercioId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WhatsAppConfigs");

            migrationBuilder.DropColumn(
                name: "MontoMensualAcordado",
                table: "Comercios");
        }
    }
}

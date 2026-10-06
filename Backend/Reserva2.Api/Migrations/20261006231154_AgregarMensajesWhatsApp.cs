using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reserva2.Api.Migrations
{
    /// <inheritdoc />
    public partial class AgregarMensajesWhatsApp : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MensajesWhatsApp",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ComercioId = table.Column<int>(type: "int", nullable: false),
                    TurnoId = table.Column<int>(type: "int", nullable: true),
                    Plantilla = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FechaEnvio = table.Column<DateTime>(type: "datetime2", nullable: false),
                    MetaMensajeId = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MensajesWhatsApp", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MensajesWhatsApp_ComercioId_FechaEnvio",
                table: "MensajesWhatsApp",
                columns: new[] { "ComercioId", "FechaEnvio" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MensajesWhatsApp");
        }
    }
}

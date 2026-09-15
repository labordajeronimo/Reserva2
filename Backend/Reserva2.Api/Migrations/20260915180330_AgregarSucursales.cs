using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reserva2.Api.Migrations
{
    /// <inheritdoc />
    public partial class AgregarSucursales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SucursalId",
                table: "Profesionales",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Sucursales",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ComercioId = table.Column<int>(type: "int", nullable: false),
                    Nombre = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Direccion = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Telefono = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Activa = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sucursales", x => x.Id);
                });

            // Backfill: ningún comercio existente puede perder su configuración de
            // profesionales/horarios al agregar sucursales, así que a cada comercio ya
            // creado se le arma una sucursal "Principal" y se le asignan todos sus
            // profesionales actuales.
            migrationBuilder.Sql(@"
                INSERT INTO Sucursales (ComercioId, Nombre, Direccion, Telefono, Activa)
                SELECT Id, 'Principal', '', NULL, 1
                FROM Comercios;

                UPDATE p
                SET p.SucursalId = s.Id
                FROM Profesionales p
                INNER JOIN Sucursales s ON s.ComercioId = p.ComercioId AND s.Nombre = 'Principal'
                WHERE p.SucursalId IS NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Sucursales");

            migrationBuilder.DropColumn(
                name: "SucursalId",
                table: "Profesionales");
        }
    }
}

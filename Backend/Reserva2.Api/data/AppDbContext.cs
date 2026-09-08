using Microsoft.EntityFrameworkCore;
using Reserva2.Api.Models;

namespace Reserva2.Api.Data
{
    public class AppDbContext : DbContext
    {
        // Constructor vacío necesario para las migraciones en tiempo de diseño
        public AppDbContext() { }

        // Constructor que usa la API cuando está en ejecución
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<Comercio> Comercios { get; set; }
        public DbSet<Servicio> Servicios { get; set; }
        public DbSet<Turno> Turnos { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                optionsBuilder.UseSqlServer("Server=LAPTOP-MB9RTSIF\\SQLEXPRESS03;Database=Reserva2Db;Trusted_Connection=True;TrustServerCertificate=True;");
            }
        }
    }
}
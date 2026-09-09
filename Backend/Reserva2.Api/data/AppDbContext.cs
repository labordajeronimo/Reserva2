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
        public DbSet<Horario> Horarios { get; set; }
        public DbSet<Profesional> Profesionales { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                optionsBuilder.UseSqlServer("Server=LAPTOP-MB9RTSIF\\SQLEXPRESS03;Database=Reserva2Db;Trusted_Connection=True;TrustServerCertificate=True;");
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Servicio>().Property(s => s.Precio).HasPrecision(18, 2);
            modelBuilder.Entity<Servicio>().Property(s => s.MontoSeña).HasPrecision(18, 2);

            // Los inicializadores de C# (= true, = "Gratuito") no generan un DEFAULT en SQL;
            // hay que declararlo acá para que las filas existentes no queden en false/"" al migrar.
            modelBuilder.Entity<Comercio>().Property(c => c.Activo).HasDefaultValue(true);
            modelBuilder.Entity<Comercio>().Property(c => c.PlanActual).HasDefaultValue("Gratuito");
        }
    }
}
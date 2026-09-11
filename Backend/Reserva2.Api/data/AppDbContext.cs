using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
        public DbSet<PasswordResetToken> PasswordResetTokens { get; set; }
        public DbSet<WhatsAppConfig> WhatsAppConfigs { get; set; }

        // Solo se usa en tiempo de diseño (ej. `dotnet ef migrations add`), cuando nadie
        // pasó un DbContextOptions ya configurado desde Program.cs. Lee la misma
        // configuración real (appsettings + variables de entorno + user-secrets) en vez
        // de tener un connection string hardcodeado acá.
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                var entorno = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Development";
                var config = new ConfigurationBuilder()
                    .SetBasePath(Directory.GetCurrentDirectory())
                    .AddJsonFile("appsettings.json", optional: true)
                    .AddJsonFile($"appsettings.{entorno}.json", optional: true)
                    .AddEnvironmentVariables()
                    .AddUserSecrets<AppDbContext>(optional: true)
                    .Build();

                var connectionString = config.GetConnectionString("DefaultConnection")
                    ?? throw new InvalidOperationException("Falta configurar ConnectionStrings:DefaultConnection.");

                optionsBuilder.UseSqlServer(connectionString);
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Servicio>().Property(s => s.Precio).HasPrecision(18, 2);
            modelBuilder.Entity<Servicio>().Property(s => s.MontoSeña).HasPrecision(18, 2);
            modelBuilder.Entity<Turno>().Property(t => t.MontoCobrado).HasPrecision(18, 2);
            modelBuilder.Entity<Comercio>().Property(c => c.MontoMensualAcordado).HasPrecision(18, 2);

            // Los inicializadores de C# (= true, = "Gratuito") no generan un DEFAULT en SQL;
            // hay que declararlo acá para que las filas existentes no queden en false/"" al migrar.
            modelBuilder.Entity<Comercio>().Property(c => c.Activo).HasDefaultValue(true);
            modelBuilder.Entity<Comercio>().Property(c => c.PlanActual).HasDefaultValue("Gratuito");
            modelBuilder.Entity<Comercio>().Property(c => c.CicloFacturacion).HasDefaultValue("Mensual");

            modelBuilder.Entity<PasswordResetToken>().HasIndex(t => t.Token).IsUnique();
            modelBuilder.Entity<WhatsAppConfig>().HasIndex(w => w.ComercioId).IsUnique();
            modelBuilder.Entity<Turno>().HasIndex(t => t.TokenCancelacion).IsUnique();
        }
    }
}
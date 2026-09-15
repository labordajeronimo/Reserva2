namespace Reserva2.Api.Models
{
    // Plan Básico: 1-2 profesionales por comercio. Premium: ilimitados.
    // El plan Gratuito no usa esta entidad (Horario/Turno quedan con ProfesionalId nulo).
    public class Profesional
    {
        public int Id { get; set; }
        public int ComercioId { get; set; }
        public string Nombre { get; set; } = string.Empty;

        // Sucursal a la que pertenece (Horario ya cuelga de Profesional, así que queda ligado
        // a la sucursal automáticamente sin tocar esa entidad). Nulo = comercios de antes de
        // la feature de sucursales, que siguen funcionando igual que hoy sin selector.
        public int? SucursalId { get; set; }
    }
}

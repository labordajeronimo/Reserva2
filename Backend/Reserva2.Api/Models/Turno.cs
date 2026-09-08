namespace Reserva2.Api.Models
{
    public class Turno
    {
        public int Id { get; set; }
        public int ComercioId { get; set; }
        public int? ServicioId { get; set; } // Tiene un "?" porque puede ser nulo si el turno es de día completo
        public DateTime FechaHoraInicio { get; set; }
        public DateTime FechaHoraFin { get; set; }
        
        // 1 = Pre-Reservado, 2 = Confirmado, 3 = Cancelado
        public int EstadoReserva { get; set; } = 1; 
        
        // Datos del cliente final (sin fricción, no necesitan cuenta)
        public string ClienteNombre { get; set; } = string.Empty;
        public string ClienteWhatsApp { get; set; } = string.Empty;
    }
}
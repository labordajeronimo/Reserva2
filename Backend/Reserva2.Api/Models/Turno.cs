namespace Reserva2.Api.Models
{
    public class Turno
    {
        public int Id { get; set; }
        public int ComercioId { get; set; }
        public int? ServicioId { get; set; } // Tiene un "?" porque puede ser nulo si el turno es de día completo

        // Nulo en comercios de un solo profesional (no todos usan la entidad Profesional)
        public int? ProfesionalId { get; set; }

        public DateTime FechaHoraInicio { get; set; }
        public DateTime FechaHoraFin { get; set; }

        // 1 = Pre-Reservado, 2 = Confirmado, 3 = Cancelado
        public int EstadoReserva { get; set; } = 1;

        // Precio del servicio al momento de la reserva/carga, para que el historial de
        // ingresos no cambie retroactivamente si después se edita el precio del servicio.
        public decimal? MontoCobrado { get; set; }

        // Datos del cliente final (sin fricción, no necesitan cuenta)
        public string ClienteNombre { get; set; } = string.Empty;
        public string ClienteWhatsApp { get; set; } = string.Empty;
        public string ClienteEmail { get; set; } = string.Empty;

        // Momento en que se creó la pre-reserva. Si pasan 2hs sin pasar a
        // EstadoReserva = 2 (Confirmado), el horario se considera liberado.
        public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

        // Token de un solo uso que va en el link de cancelación del mail de confirmación,
        // para que el cliente final pueda cancelar su turno sin necesitar una cuenta.
        public string TokenCancelacion { get; set; } = Guid.NewGuid().ToString("N");
    }
}
namespace Reserva2.Api.Models
{
    // Define la disponibilidad semanal de un comercio.
    // Cada fila es un bloque horario dentro de un día de la semana
    // (ej: Lunes de 10:00 a 19:00). La grilla de turnos se genera
    // combinando esto con la duración del Servicio elegido.
    public class Horario
    {
        public int Id { get; set; }
        public int ComercioId { get; set; }

        // Usa los mismos valores que System.DayOfWeek:
        // 0 = Domingo, 1 = Lunes, 2 = Martes ... 6 = Sábado
        public int DiaSemana { get; set; }

        public TimeSpan HoraInicio { get; set; }
        public TimeSpan HoraFin { get; set; }
    }
}
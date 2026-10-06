namespace Reserva2.Api.Models
{
    // Un registro por cada mensaje de WhatsApp que Meta aceptó enviar en nombre de un
    // comercio (los que fallan no se guardan: no se cobran). Sirve para saber cuántos
    // mensajes usa cada comercio por mes, ya que Meta cobra por mensaje.
    public class MensajeWhatsApp
    {
        public int Id { get; set; }
        public int ComercioId { get; set; }
        public int? TurnoId { get; set; }
        public string Plantilla { get; set; } = string.Empty;

        // En UTC, igual que Pago.Fecha.
        public DateTime FechaEnvio { get; set; } = DateTime.UtcNow;

        // Id que devuelve Meta (wamid...), por si hay que rastrear un envío puntual.
        public string? MetaMensajeId { get; set; }
    }
}

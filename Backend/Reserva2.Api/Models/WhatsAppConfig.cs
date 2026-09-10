namespace Reserva2.Api.Models
{
    // Placeholder hasta conectar la Cloud API real de Meta (falta credenciales).
    // Por ahora solo guarda si el comercio activó o no el bot de WhatsApp.
    public class WhatsAppConfig
    {
        public int Id { get; set; }
        public int ComercioId { get; set; }
        public bool Activado { get; set; }
    }
}

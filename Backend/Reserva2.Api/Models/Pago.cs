namespace Reserva2.Api.Models
{
    // Historial de pagos del plan de cada comercio (lo que el comercio le paga a Reserva2,
    // no las señas de sus clientes). Entra una fila por cada renovación: las de Mercado Pago
    // se crean "Pendiente" al iniciar el pago y pasan a "Aprobado" cuando Mercado Pago lo
    // confirma; las transferencias las registra el Super Admin con "Marcar pago recibido".
    public class Pago
    {
        public int Id { get; set; }
        public int ComercioId { get; set; }
        public DateTime Fecha { get; set; } = DateTime.UtcNow;
        public decimal Monto { get; set; }

        // Plan y ciclo que se pagaron (pueden ser distintos del plan que tenía el comercio
        // si pagó un cambio de plan).
        public string Plan { get; set; } = string.Empty;
        public string Ciclo { get; set; } = "Mensual";

        // "MercadoPago" | "Transferencia"
        public string Medio { get; set; } = "Transferencia";

        // "Pendiente" | "Aprobado" | "Rechazado"
        public string Estado { get; set; } = "Aprobado";

        public long? MercadoPagoPagoId { get; set; }
        public DateTime? FechaAprobacion { get; set; }
    }
}

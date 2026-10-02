using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

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

        // Seña: el comprobante (captura de la transferencia) que sube el cliente al reservar un
        // servicio con seña. Es el nombre del archivo dentro de la carpeta privada de
        // comprobantes; nunca se manda en las respuestas JSON (lo ve el comercio por su endpoint).
        [JsonIgnore]
        public string? ComprobanteArchivo { get; set; }

        // null = el servicio no pedía seña; false = seña a verificar; true = el comercio ya la
        // verificó contra su cuenta.
        public bool? SeñaVerificada { get; set; }

        // Cómo se pagó (o se está pagando) la seña: "Transferencia" (comprobante) o
        // "MercadoPago". Null si el servicio no pedía seña. Un turno con seña por Mercado Pago
        // queda en EstadoReserva = 1 (pre-reserva) hasta que Mercado Pago avisa que se aprobó
        // el pago; si no se paga a tiempo, el horario se libera solo (ver EsPreReservaVigente).
        public string? SeñaMedio { get; set; }

        // Id del pago aprobado en Mercado Pago (para no procesarlo dos veces y poder buscarlo).
        public long? MercadoPagoPagoId { get; set; }

        // Monto que se cobró (o se está cobrando) por Mercado Pago: la seña o el precio
        // completo del servicio, según lo que eligió el comercio al momento de reservar.
        public decimal? MontoMercadoPago { get; set; }

        [NotMapped]
        public bool TieneComprobante => ComprobanteArchivo is not null;
    }
}
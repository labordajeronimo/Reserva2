using System.ComponentModel.DataAnnotations;

namespace Reserva2.Api.Models
{
    public class Servicio
    {
        public int Id { get; set; }
        public int ComercioId { get; set; } // Vincula el servicio con su dueño
        public string Nombre { get; set; } = string.Empty;
        public int DuracionMinutos { get; set; }
        public decimal Precio { get; set; }
        public decimal? MontoSeña { get; set; }
        public bool Activo { get; set; } = true;

        // Opcional, agrupa los servicios en la página pública (ej.: "Limpiezas faciales",
        // "Cortes"). Es texto libre: las categorías del comercio son las distintas que haya
        // cargado en sus servicios. Nulo = sin categoría.
        [MaxLength(40)]
        public string? Categoria { get; set; }
    }
}
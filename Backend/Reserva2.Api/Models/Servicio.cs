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
    }
}
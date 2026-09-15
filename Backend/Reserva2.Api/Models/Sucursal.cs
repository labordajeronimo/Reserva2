namespace Reserva2.Api.Models
{
    public class Sucursal
    {
        public int Id { get; set; }
        public int ComercioId { get; set; } // Vincula la sucursal con su dueño
        public string Nombre { get; set; } = string.Empty;
        public string Direccion { get; set; } = string.Empty;
        public string? Telefono { get; set; }
        public bool Activa { get; set; } = true;
    }
}

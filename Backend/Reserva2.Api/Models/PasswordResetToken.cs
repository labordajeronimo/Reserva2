namespace Reserva2.Api.Models
{
    // Token de un solo uso para el flujo de "olvidé mi contraseña" del Admin Cliente.
    public class PasswordResetToken
    {
        public int Id { get; set; }
        public int ComercioId { get; set; }
        public string Token { get; set; } = string.Empty;
        public DateTime FechaExpiracion { get; set; }
        public bool Usado { get; set; }
    }
}

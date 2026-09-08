namespace Reserva2.Api.Models
{
    // Plataforma desarrollada por JERONIMO LABORDA
    public class Comercio
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = string.Empty;
        public string AliasUrl { get; set; } = string.Empty;
        
        // Acá diferenciamos si es Peluquería, Cancha o Tatuador
        public string TipoPlantilla { get; set; } = "Servicio"; 
        
        public string TelefonoNotificaciones { get; set; } = string.Empty;
        public string DatosBancarios { get; set; } = string.Empty;
        public bool PagoAlDia { get; set; } = true;
    }
}
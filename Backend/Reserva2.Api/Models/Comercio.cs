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

        // Ruta relativa al logo subido (ej. "/uploads/logos/12.png"). Null si no cargó uno,
        // en cuyo caso la página pública sigue mostrando las iniciales del negocio.
        public string? LogoUrl { get; set; }

        // Login del dueño del comercio para entrar al panel
        public string Email { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;

        // El Super Admin apaga esto si el comercio no paga; bloquea el link público
        public bool Activo { get; set; } = true;

        // "Gratuito" | "Basico" | "Premium"
        public string PlanActual { get; set; } = "Gratuito";
        public DateTime? FechaProximoPago { get; set; }

        // Monto mensual que el Super Admin acordó puntualmente con este comercio (Básico y
        // Premium se cobran distinto según cantidad de profesionales o pago anual con
        // descuento, así que no hay un precio único por plan: lo carga el Super Admin a mano).
        public decimal? MontoMensualAcordado { get; set; }

        // "Mensual" | "Anual". Solo registro informativo para que el Super Admin sepa cómo
        // cobrarle a cada comercio; no dispara ninguna renovación ni cobro automático todavía.
        public string CicloFacturacion { get; set; } = "Mensual";
    }
}
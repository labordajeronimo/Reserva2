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

        // Fecha de registro del comercio (para la tendencia de altas del dashboard del
        // Super Admin y la ficha de detalle). Los comercios que ya existían antes de este
        // campo quedaron con la fecha de la migración, no con su alta real.
        public DateTime FechaAlta { get; set; } = DateTime.UtcNow;

        // Nula hasta que el Super Admin activa el comercio por primera vez (todo registro
        // nuevo entra pausado, ver Activo más arriba). Sirve para distinguir "pendiente de
        // activación" (Activo == false && FechaActivacion == null) de "pausado después de
        // haber estado activo" (Activo == false && FechaActivacion != null).
        public DateTime? FechaActivacion { get; set; }

        // Último login del dueño al panel (UTC). Nulo si no entró desde que existe el campo.
        // Lo usa el Super Admin para las alertas de "sin actividad" y la salud del comercio.
        public DateTime? UltimoAcceso { get; set; }

        // Cuándo se pausó por última vez (hora de Argentina, igual que FechaActivacion). Se
        // borra si se vuelve a activar. Sirve para las "bajas por mes" del Super Admin; un
        // comercio eliminado desaparece de la base y no cuenta como baja.
        public DateTime? FechaBaja { get; set; }

        // Cantidad de profesionales y sucursales que el comercio declaró tener al
        // registrarse (no es lo mismo que la cantidad real cargada en Profesionales/
        // Sucursales, que puede cambiar después). Sirve para saber cuánto cobrarle sin
        // tener que volver a pedírselo cuando se conecte el cobro real (MercadoPago).
        public int CantidadProfesionalesContratada { get; set; } = 1;
        public int CantidadSucursalesContratada { get; set; } = 1;

        // Cuenta de Mercado Pago del comercio, conectada por OAuth desde su panel, para que
        // sus clientes paguen la seña con Mercado Pago y la plata vaya directo a esa cuenta.
        // Los tokens se guardan cifrados con DataProtection (nunca salen en ninguna respuesta).
        public long? MercadoPagoUserId { get; set; }
        public string? MercadoPagoAccessToken { get; set; }
        public string? MercadoPagoRefreshToken { get; set; }
        public DateTime? MercadoPagoTokenVence { get; set; }

        // Cómo pueden pagar la seña sus clientes. Transferencia = el flujo de siempre (alias +
        // captura del comprobante); Mercado Pago solo cuenta si la cuenta está conectada.
        public bool SeñaPorMercadoPago { get; set; }
        public bool SeñaPorTransferencia { get; set; } = true;

        // Qué se cobra por Mercado Pago al reservar: false = la seña del servicio (solo en los
        // servicios que la piden); true = el servicio completo (en todos los que tienen precio).
        public bool CobroMercadoPagoTotal { get; set; }
    }
}
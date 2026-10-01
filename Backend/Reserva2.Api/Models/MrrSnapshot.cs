namespace Reserva2.Api.Models
{
    // Foto mensual del ingreso recurrente (MRR = suma de montos acordados de comercios
    // activos). El MRR "en vivo" solo se puede calcular para hoy, así que para comparar
    // contra meses anteriores hace falta guardarlo: el resumen del Super Admin actualiza la
    // fila del mes en curso cada vez que se consulta, y la última actualización del mes
    // queda como su valor de cierre.
    public class MrrSnapshot
    {
        public int Id { get; set; }
        public int Anio { get; set; }
        public int Mes { get; set; }
        public decimal Monto { get; set; }
        public DateTime FechaActualizacion { get; set; } = DateTime.UtcNow;
    }
}

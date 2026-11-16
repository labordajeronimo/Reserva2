using System.Collections.Concurrent;

namespace Reserva2.Api.Seguridad;

// Límite de intentos fallidos POR CUENTA (el rate limiting de Program.cs es por IP). Así un
// ataque repartido entre muchas IPs contra un mismo email también se frena: tras
// MaximoFallos intentos fallidos en VentanaFallos, la cuenta queda bloqueada BloqueoDuracion
// para nuevos intentos (aunque la contraseña sea la correcta). Vive en memoria: alcanza para
// un solo servidor, y un reinicio de la API solo "perdona" los intentos en curso.
public sealed class IntentosLogin
{
    public const int MaximoFallos = 5;
    public static readonly TimeSpan VentanaFallos = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan BloqueoDuracion = TimeSpan.FromMinutes(15);

    private sealed record Estado(int Fallos, DateTime PrimerFallo, DateTime? BloqueadoHasta);

    private readonly ConcurrentDictionary<string, Estado> _estados = new();
    private readonly TimeProvider _reloj;

    public IntentosLogin(TimeProvider? reloj = null) => _reloj = reloj ?? TimeProvider.System;

    private static string Clave(string ambito, string? cuenta) =>
        $"{ambito}:{(cuenta ?? string.Empty).Trim().ToLowerInvariant()}";

    // Minutos que faltan para poder volver a intentar, o null si la cuenta no está bloqueada.
    public int? MinutosBloqueada(string ambito, string? cuenta)
    {
        var ahora = _reloj.GetUtcNow().UtcDateTime;
        if (_estados.TryGetValue(Clave(ambito, cuenta), out var estado) && estado.BloqueadoHasta > ahora)
            return (int)Math.Ceiling((estado.BloqueadoHasta.Value - ahora).TotalMinutes);
        return null;
    }

    public void RegistrarFallo(string ambito, string? cuenta)
    {
        var ahora = _reloj.GetUtcNow().UtcDateTime;
        _estados.AddOrUpdate(Clave(ambito, cuenta),
            _ => new Estado(1, ahora, null),
            (_, previo) =>
            {
                // Ventana vencida (o bloqueo cumplido): se empieza a contar de nuevo.
                if (ahora - previo.PrimerFallo > VentanaFallos || previo.BloqueadoHasta <= ahora)
                    previo = new Estado(0, ahora, null);
                var fallos = previo.Fallos + 1;
                return new Estado(fallos, previo.PrimerFallo, fallos >= MaximoFallos ? ahora + BloqueoDuracion : null);
            });

        // Limpieza oportunista para que el diccionario no crezca sin fin con emails inventados.
        if (_estados.Count > 10_000)
            foreach (var (clave, estado) in _estados)
                if (ahora - estado.PrimerFallo > VentanaFallos && !(estado.BloqueadoHasta > ahora))
                    _estados.TryRemove(clave, out _);
    }

    public void RegistrarExito(string ambito, string? cuenta) => _estados.TryRemove(Clave(ambito, cuenta), out _);
}

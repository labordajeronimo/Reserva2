using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Reserva2.Api.Seguridad;

// Hash de contraseñas con Argon2id (la primera recomendación de OWASP), en formato PHC:
//   $argon2id$v=19$m=19456,t=2,p=1$<sal base64>$<hash base64>
// Las contraseñas viejas (PBKDF2-SHA256, "sal.hash") se siguen aceptando y se rehashean con
// Argon2id la próxima vez que el usuario entra bien, así nadie tiene que resetear nada.
public static class Contrasenias
{
    // Parámetros mínimos recomendados por OWASP para Argon2id: 19 MiB, 2 pasadas, 1 hilo.
    private const int MemoriaKiB = 19456;
    private const int Iteraciones = 2;
    private const int Paralelismo = 1;
    private const int LargoSal = 16;
    private const int LargoHash = 32;
    private const string Prefijo = "$argon2id$";

    public static string Hashear(string password)
    {
        var sal = RandomNumberGenerator.GetBytes(LargoSal);
        var hash = Argon2id(password, sal, MemoriaKiB, Iteraciones, Paralelismo, LargoHash);
        return $"{Prefijo}v=19$m={MemoriaKiB},t={Iteraciones},p={Paralelismo}${Convert.ToBase64String(sal)}${Convert.ToBase64String(hash)}";
    }

    // Nunca tira excepción: un hash mal formado (o vacío) es simplemente "no coincide".
    public static bool Verificar(string? password, string? guardado)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(guardado)) return false;
        try
        {
            return guardado.StartsWith(Prefijo, StringComparison.Ordinal)
                ? VerificarArgon2(password, guardado)
                : VerificarPbkdf2Viejo(password, guardado);
        }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
        catch (OverflowException) { return false; }
    }

    // true si el hash guardado es del formato viejo (o con parámetros más débiles que los
    // actuales) y conviene reemplazarlo ahora que se conoce la contraseña.
    public static bool NecesitaRehash(string guardado) =>
        !guardado.StartsWith($"{Prefijo}v=19$m={MemoriaKiB},t={Iteraciones},p={Paralelismo}$", StringComparison.Ordinal);

    // Huella corta del hash: va dentro del token de sesión. Si la contraseña cambia, la huella
    // deja de coincidir y todas las sesiones abiertas con la contraseña anterior se cierran.
    public static string Huella(string guardado) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(guardado)))[..16];

    private static bool VerificarArgon2(string password, string guardado)
    {
        // "", "argon2id", "v=19", "m=..,t=..,p=..", sal, hash
        var partes = guardado.Split('$');
        if (partes.Length != 6 || partes[2] != "v=19") return false;

        int m = 0, t = 0, p = 0;
        foreach (var parametro in partes[3].Split(','))
        {
            var kv = parametro.Split('=');
            if (kv.Length != 2) return false;
            var valor = int.Parse(kv[1]);
            switch (kv[0])
            {
                case "m": m = valor; break;
                case "t": t = valor; break;
                case "p": p = valor; break;
                default: return false;
            }
        }
        // Topes para que un hash adulterado no pueda pedir gigas de memoria.
        if (m is < 8 or > 262144 || t is < 1 or > 10 || p is < 1 or > 8) return false;

        var sal = Convert.FromBase64String(partes[4]);
        var esperado = Convert.FromBase64String(partes[5]);
        if (esperado.Length is < 16 or > 64) return false;

        var calculado = Argon2id(password, sal, m, t, p, esperado.Length);
        return CryptographicOperations.FixedTimeEquals(esperado, calculado);
    }

    private static bool VerificarPbkdf2Viejo(string password, string guardado)
    {
        var partes = guardado.Split('.');
        if (partes.Length != 2) return false;
        var sal = Convert.FromBase64String(partes[0]);
        var esperado = Convert.FromBase64String(partes[1]);
        var calculado = Rfc2898DeriveBytes.Pbkdf2(password, sal, 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(esperado, calculado);
    }

    private static byte[] Argon2id(string password, byte[] sal, int memoriaKiB, int iteraciones, int paralelismo, int largo)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = sal,
            MemorySize = memoriaKiB,
            Iterations = iteraciones,
            DegreeOfParallelism = paralelismo
        };
        return argon.GetBytes(largo);
    }
}

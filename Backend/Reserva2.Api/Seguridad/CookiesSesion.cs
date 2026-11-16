namespace Reserva2.Api.Seguridad;

// El token de sesión viaja en una cookie HttpOnly (el JavaScript de la página no la puede
// leer, así que un script inyectado no puede robar la sesión), Secure (solo por HTTPS) y
// SameSite=Strict (el navegador no la manda en pedidos que vienen de otro sitio).
// Como defensa extra contra CSRF, la API solo acepta la cookie si el pedido trae además el
// header X-Reserva2-Sesion, que un formulario o un link de otro sitio no pueden agregar.
// Ese mismo header dice qué sesión usar: "comercio" (panel) o "superadmin".
public static class CookiesSesion
{
    public const string Header = "X-Reserva2-Sesion";
    public const string CookieComercio = "r2_sesion";
    public const string CookieSuperAdmin = "r2_admin";

    public static string? TokenDelPedido(HttpRequest request)
    {
        var tipo = request.Headers[Header].ToString();
        return tipo switch
        {
            "comercio" => request.Cookies[CookieComercio],
            "superadmin" => request.Cookies[CookieSuperAdmin],
            _ => null
        };
    }

    public static void Guardar(HttpResponse response, string cookie, string token, TimeSpan duracion) =>
        response.Cookies.Append(cookie, token, Opciones(response.HttpContext, DateTimeOffset.UtcNow.Add(duracion)));

    public static void Borrar(HttpResponse response, string cookie) =>
        response.Cookies.Delete(cookie, Opciones(response.HttpContext, null));

    private static CookieOptions Opciones(HttpContext http, DateTimeOffset? vence) => new()
    {
        HttpOnly = true,
        // En desarrollo (http://localhost) los navegadores aceptan Secure igual.
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/api",
        Expires = vence,
        IsEssential = true
    };
}

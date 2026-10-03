using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reserva2.Api.Utils
{
    // Cliente mínimo de la API de Mercado Pago (sin el SDK oficial: son 4 llamadas HTTP).
    // Se usa para dos cosas distintas, cada una con su propio access token:
    //  - Señas: con el token de CADA comercio (conectado por OAuth), así la plata de la seña
    //    va directo a la cuenta del comercio.
    //  - Planes: con el token de la cuenta de Reserva2 (MercadoPago:AccessToken), para que el
    //    comercio le pague su plan a Reserva2.
    // Nunca se confía en lo que llega por el webhook: siempre se vuelve a consultar el pago.
    public class MercadoPagoClient
    {
        private const string ApiBase = "https://api.mercadopago.com";
        private const string AuthBase = "https://auth.mercadopago.com/authorization";

        private static readonly JsonSerializerOptions Json = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
        private readonly ILogger logger;

        public MercadoPagoClient(ILogger<MercadoPagoClient> logger) => this.logger = logger;

        public static string UrlAutorizacion(string clientId, string redirectUri, string state) =>
            $"{AuthBase}?client_id={Uri.EscapeDataString(clientId)}&response_type=code&platform_id=mp" +
            $"&state={Uri.EscapeDataString(state)}&redirect_uri={Uri.EscapeDataString(redirectUri)}";

        public Task<TokenOAuthMp?> CanjearCodigo(string clientId, string clientSecret, string code, string redirectUri) =>
            PedirToken(new { client_id = clientId, client_secret = clientSecret, grant_type = "authorization_code", code, redirect_uri = redirectUri });

        public Task<TokenOAuthMp?> RefrescarToken(string clientId, string clientSecret, string refreshToken) =>
            PedirToken(new { client_id = clientId, client_secret = clientSecret, grant_type = "refresh_token", refresh_token = refreshToken });

        private async Task<TokenOAuthMp?> PedirToken(object cuerpo)
        {
            var respuesta = await http.PostAsJsonAsync($"{ApiBase}/oauth/token", cuerpo);
            if (!respuesta.IsSuccessStatusCode)
            {
                logger.LogError("Mercado Pago devolvió {StatusCode} al pedir un token OAuth: {Detalle}",
                    respuesta.StatusCode, await respuesta.Content.ReadAsStringAsync());
                return null;
            }
            return await respuesta.Content.ReadFromJsonAsync<TokenOAuthMp>(Json);
        }

        public async Task<PreferenciaCreadaMp?> CrearPreferencia(string accessToken, PreferenciaMp preferencia)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/checkout/preferences")
            {
                Content = JsonContent.Create(preferencia, options: Json)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var respuesta = await http.SendAsync(request);
            if (!respuesta.IsSuccessStatusCode)
            {
                logger.LogError("Mercado Pago devolvió {StatusCode} al crear la preferencia {Referencia}: {Detalle}",
                    respuesta.StatusCode, preferencia.ExternalReference, await respuesta.Content.ReadAsStringAsync());
                return null;
            }
            return await respuesta.Content.ReadFromJsonAsync<PreferenciaCreadaMp>(Json);
        }

        // Null si el pago no existe para ese token (ej. un id inventado o de otra cuenta).
        // Tira excepción si Mercado Pago no responde, para que el webhook devuelva error y
        // Mercado Pago lo reintente más tarde.
        public async Task<PagoMp?> ObtenerPago(string accessToken, long pagoId)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/v1/payments/{pagoId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            var respuesta = await http.SendAsync(request);
            if (respuesta.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return null;
            respuesta.EnsureSuccessStatusCode();
            return await respuesta.Content.ReadFromJsonAsync<PagoMp>(Json);
        }
    }

    public record TokenOAuthMp(string AccessToken, string? RefreshToken, long ExpiresIn, long UserId);

    public record PreferenciaMp(
        List<ItemPreferenciaMp> Items,
        string ExternalReference,
        string NotificationUrl,
        BackUrlsMp BackUrls,
        string AutoReturn = "approved",
        PagadorMp? Payer = null,
        MediosDePagoMp? PaymentMethods = null,
        bool? Expires = null,
        string? ExpirationDateTo = null,
        string? StatementDescriptor = null);

    public record ItemPreferenciaMp(string Title, int Quantity, decimal UnitPrice, string CurrencyId = "ARS");
    public record BackUrlsMp(string Success, string Pending, string Failure);
    public record PagadorMp(string? Email);
    public record MediosDePagoMp(List<TipoDePagoMp> ExcludedPaymentTypes);
    public record TipoDePagoMp(string Id);

    public record PreferenciaCreadaMp(string Id, string InitPoint);

    // status: approved | pending | in_process | authorized | rejected | cancelled | refunded | charged_back
    public record PagoMp(long Id, string Status, string? ExternalReference, decimal TransactionAmount);
}

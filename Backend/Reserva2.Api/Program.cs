using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Reserva2.Api.Data;
using Reserva2.Api.Models;
using Reserva2.Api.Utils;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;

// ==========================================
// MODO CONSOLA: dotnet run -- hash-password "clave"
// Genera el hash PBKDF2 de una contraseña sin levantar el servidor web.
// Útil para cargar SuperAdmin:PasswordHash sin scripts sueltos.
// ==========================================
if (args.Length > 0 && args[0] == "hash-password")
{
    if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
    {
        Console.WriteLine("Uso: dotnet run -- hash-password \"tu-contraseña\"");
        return;
    }

    Console.WriteLine(HashPassword(args[1]));
    return;
}

// wwwroot nunca se commitea (solo tiene contenido subido por usuarios, ej. logos), pero
// WebApplication.CreateBuilder ya necesita que la carpeta EXISTA en este punto — si no,
// tira DirectoryNotFoundException al arrancar (StaticWebAssetsLoader la busca de entrada).
// Por eso se crea acá, antes de todo lo demás, no de forma perezosa en el endpoint de
// subida ni más adelante en el pipeline: para entonces ya es tarde en los dos casos.
Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "logos"));

var builder = WebApplication.CreateBuilder(args);

// Archivos subidos por los usuarios (logos y comprobantes de seña). Tienen que vivir FUERA de
// la carpeta de la app: deploy.ps1 borra /root/reserva2-api entero en cada deploy, y con eso
// se perdían los logos. En producción la ruta viene de Uploads__Ruta (override de systemd,
// /var/lib/reserva2/uploads); en desarrollo, si no está configurada, se usa ./uploads (fuera
// de wwwroot, para que los comprobantes nunca queden servidos como archivos estáticos).
var rutaUploads = builder.Configuration["Uploads:Ruta"] ?? Path.Combine(builder.Environment.ContentRootPath, "uploads");
var rutaLogos = Path.Combine(rutaUploads, "logos");
var rutaComprobantes = Path.Combine(rutaUploads, "comprobantes");
var rutaFotosProfesionales = Path.Combine(rutaUploads, "profesionales");
Directory.CreateDirectory(rutaLogos);
Directory.CreateDirectory(rutaComprobantes);
Directory.CreateDirectory(rutaFotosProfesionales);

// Configuración de la base de datos SQL Server
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// === JWT: Admin (dueño de un comercio) y Super Admin (vos, gestionás todos los comercios) ===
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Falta configurar Jwt:Key (dotnet user-secrets set \"Jwt:Key\" \"...\").");
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("AdminCliente", p => p.RequireRole("AdminCliente"))
    .AddPolicy("SuperAdmin", p => p.RequireRole("SuperAdmin"));

// System.Text.Json no serializa TimeSpan por defecto (lo usan los Horarios).
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new TimeSpanJsonConverter());
});

// === Configuración de CORS para Angular ===
// Lista de orígenes permitidos en Cors:AllowedOrigins (appsettings o variable de entorno
// Cors__AllowedOrigins__1, etc.). Si no hay nada configurado, solo se permite el dev server local.
var origenesPermitidos = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:4200"];

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular", policy =>
    {
        policy.WithOrigins(origenesPermitidos)
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// === IP real del cliente detrás de nginx ===
// nginx le pasa los pedidos a la API desde la misma máquina, así que sin esto
// RemoteIpAddress es siempre 127.0.0.1 y el rate limiting termina siendo un único cupo
// compartido por toda la plataforma. Solo se confía en X-Forwarded-For si el pedido viene
// del propio servidor (el proxy local); si alguien manda ese header desde afuera, se ignora.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
});

// === Rate limiting: frena fuerza bruta en login y spam de reservas ===
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Intentos de login/registro: pocos por minuto y por IP, para dificultar fuerza bruta.
    options.AddPolicy("login", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1)
        }));

    // Reservas públicas: más margen que el login, pero corta scripts que floodean turnos.
    options.AddPolicy("turnos", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1)
        }));
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Claves con las que se cifran los tokens de Mercado Pago de los comercios. Tienen que vivir
// fuera de la carpeta de la app (deploy.ps1 la borra entera) y no perderse nunca: sin ellas
// los tokens guardados no se pueden descifrar y cada comercio tendría que volver a conectar
// su cuenta. En producción: DataProtection__Ruta (ej. /var/lib/reserva2/claves).
var rutaClaves = builder.Configuration["DataProtection:Ruta"]
    ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(rutaUploads).TrimEnd(Path.DirectorySeparatorChar))!, "claves");
builder.Services.AddDataProtection()
    .SetApplicationName("Reserva2")
    .PersistKeysToFileSystem(new DirectoryInfo(rutaClaves));
builder.Services.AddSingleton<MercadoPagoClient>();

var app = builder.Build();

var mercadoPago = app.Services.GetRequiredService<MercadoPagoClient>();
var protectorTokensMp = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("MercadoPago.Tokens");
var protectorEstadoOAuthMp = app.Services.GetRequiredService<IDataProtectionProvider>()
    .CreateProtector("MercadoPago.OAuthState").ToTimeLimitedDataProtector();

// Primero que todo: el resto del pipeline (rate limiting incluido) ya ve la IP real.
app.UseForwardedHeaders();

// Configuración para poder probar la API visualmente
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
// Solo los logos y las fotos de los profesionales son públicos (/uploads/logos/... y
// /uploads/profesionales/...). Los comprobantes de seña NO se sirven como archivos
// estáticos: se leen por un endpoint que valida que sea el comercio dueño.
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(rutaLogos),
    RequestPath = "/uploads/logos"
});
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(rutaFotosProfesionales),
    RequestPath = "/uploads/profesionales"
});
app.UseCors("AllowAngular");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// ==========================================
// CONSTANTES DE NEGOCIO
// ==========================================
const int HorasLimiteParaConfirmar = 2;
// Una reserva con seña por Mercado Pago aparta el horario este tiempo mientras el cliente
// paga; si no llega el pago, se libera sola. El link de pago vence un poco antes.
const int MinutosParaPagarSeniaMercadoPago = 30;
// Precio mensual del extra "Cobros automáticos con Mercado Pago" (se suma al plan).
const decimal PrecioAddonCobrosMensual = 10000m;
const int TopeTurnosMensualesGratuito = 60;
const int DuracionTokenHoras = 24;
// El cliente puede cancelar por el link del mail hasta 2 horas antes del turno; después
// tiene que hablar con el comercio (que ya no llega a ofrecer ese horario a otro).
const int HorasMinimasParaCancelar = 2;
const int LargoMinimoPassword = 8;
const int LargoMaximoNombreCliente = 80;
// Valores de Turno.Origen.
const string OrigenPaginaPublica = "PaginaPublica";
const string OrigenPanel = "Panel";

var PlanesValidos = new[] { "Gratuito", "Basico", "Premium" };

// Gratuito: 1 profesional. Básico: 2. Premium (o cualquier otro plan): sin límite.
static int TopeProfesionales(string plan) => plan switch
{
    "Gratuito" => 1,
    "Basico" => 2,
    _ => int.MaxValue
};

// Gratuito y Básico: 1 sola sucursal. Premium: sin límite.
static int TopeSucursales(string plan) => plan switch
{
    "Gratuito" => 1,
    "Basico" => 1,
    _ => int.MaxValue
};

// Precios de lista mensuales (los mismos que se muestran en la landing y en el selector de
// plan compartido). El monto real que se le cobra a cada comercio puede diferir si el Super
// Admin acordó un monto puntual (Comercio.MontoMensualAcordado); esto es la tarifa de lista.
var PreciosPlanMensual = new Dictionary<string, (decimal Unico, decimal PorProfesional)>
{
    ["Gratuito"] = (0m, 0m),
    ["Basico"] = (7000m, 4800m),
    ["Premium"] = (10000m, 7500m)
};

// "Unidades" = profesionales + (sucursales - 1): la primera sucursal no suma nada, cada
// sucursal extra pesa igual que un profesional más. Con 1 sola unidad se cobra la tarifa
// "único profesional"; con 2 o más, la tarifa "por profesional" multiplicada por la
// cantidad de unidades. El ciclo Anual aplica el mismo 25% de descuento que ya se muestra
// en la landing (equivale a pagar 9 de 12 meses), calculado sobre este total en vez del
// precio fijo de un solo profesional. Se va a reutilizar el lunes desde la integración de
// MercadoPago, así que no hardcodear esta cuenta en ningún otro lado.
decimal CalcularPrecioPlan(string plan, string ciclo, int cantidadProfesionales, int cantidadSucursales)
{
    var precios = PreciosPlanMensual.TryGetValue(plan, out var p) ? p : PreciosPlanMensual["Gratuito"];
    var unidades = Math.Max(1, cantidadProfesionales + (cantidadSucursales - 1));
    var totalMensual = unidades <= 1 ? precios.Unico : precios.PorProfesional * unidades;

    return ciclo == "Anual" ? Math.Round(totalMensual * 12 * 0.75m, 2) : totalMensual;
}

// Turno.FechaHoraInicio/FechaHoraFin se guardan como hora local de Argentina "pelada" (sin
// offset: el cliente manda literalmente "2026-09-14T16:00:00"), no como UTC. Argentina no
// tiene horario de verano desde 2009 y su offset es siempre UTC-3, así que para comparar
// "ahora" contra esas columnas hay que restarle 3 horas a UtcNow en vez de usarlo crudo
// (si no, un despliegue en un servidor con reloj en UTC corre el día/las franjas ~3hs).
static DateTime AhoraArgentina() => DateTime.UtcNow.AddHours(-3);

// Próxima fecha de pago según el ciclo de facturación: un mes o un año desde hoy.
// Se recalcula cada vez que cambia el plan/ciclo, y también al marcar un pago recibido
// (ahí no se extiende desde la fecha vieja, se vuelve a contar un ciclo entero desde hoy).
static DateTime CalcularProximoPago(string cicloFacturacion) =>
    cicloFacturacion == "Anual" ? AhoraArgentina().Date.AddYears(1) : AhoraArgentina().Date.AddMonths(1);

bool EsPreReservaVigente(Turno t) =>
    t.EstadoReserva == 1 && t.FechaCreacion > (t.SeñaMedio == "MercadoPago"
        ? DateTime.UtcNow.AddMinutes(-(MinutosParaPagarSeniaMercadoPago + 5))
        : DateTime.UtcNow.AddHours(-HorasLimiteParaConfirmar));

bool OcupaHorario(Turno t) => t.EstadoReserva == 2 || EsPreReservaVigente(t);

// Bloques de disponibilidad semanal de un comercio (o de un profesional puntual) para un día dado.
async Task<List<Horario>> ObtenerBloquesHorario(AppDbContext context, int comercioId, int? profesionalId, int diaSemana) =>
    await context.Horarios
        .Where(h => h.ComercioId == comercioId && h.ProfesionalId == profesionalId && h.DiaSemana == diaSemana)
        .ToListAsync();

// El turno completo (inicio a fin) tiene que caer dentro de un único bloque horario, no a caballo de dos.
static bool EstaDentroDeAlgunHorario(IEnumerable<Horario> bloques, DateTime inicio, DateTime fin)
{
    var fechaBase = inicio.Date;
    return bloques.Any(b => inicio >= fechaBase + b.HoraInicio && fin <= fechaBase + b.HoraFin);
}

// Especialidad del profesional: sin espacios de más; vacía = sin especialidad (null).
static string? NormalizarEspecialidad(string? especialidad) =>
    string.IsNullOrWhiteSpace(especialidad) ? null : especialidad.Trim();

// Borra del disco la foto de un profesional (si tenía). Se llama después de guardar en la
// base, para no perder el archivo si falla el SaveChanges.
void BorrarFotoProfesional(string? fotoUrl)
{
    if (fotoUrl is null) return;
    var ruta = Path.Combine(rutaFotosProfesionales, Path.GetFileName(fotoUrl));
    if (File.Exists(ruta)) File.Delete(ruta);
}

static bool EmailValido(string email) =>
    System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^\s@]+@[^\s@]+\.[^\s@]+$") && email.Length <= 254;

// Alias del link público (reservados2.com/{alias}): minúsculas, números y guiones, de 3 a
// 40 caracteres. Los reservados chocarían con rutas del frontend o del backend/nginx.
var AliasReservados = new[] { "panel", "super-admin", "terminos", "privacidad", "cancelar-turno", "api", "uploads" };

string? ErrorAlias(string alias)
{
    if (!System.Text.RegularExpressions.Regex.IsMatch(alias, "^[a-z0-9-]{3,40}$"))
        return "El link tiene que tener entre 3 y 40 caracteres: solo minúsculas, números y guiones.";
    if (AliasReservados.Contains(alias))
        return "Ese link está reservado. Elegí otro.";
    return null;
}

// Mismas reglas para el alta y la edición de un servicio. Una duración de 0 dejaba colgado
// el armado de la grilla de disponibilidad (el cursor nunca avanzaba).
static string? ErrorServicio(string? nombre, int duracionMinutos, decimal precio, decimal? montoSenia)
{
    if (string.IsNullOrWhiteSpace(nombre)) return "El nombre del servicio no puede estar vacío.";
    if (duracionMinutos < 5 || duracionMinutos > 600) return "La duración tiene que estar entre 5 y 600 minutos.";
    if (precio < 0) return "El precio no puede ser negativo.";
    if (montoSenia < 0) return "La seña no puede ser negativa.";
    if (montoSenia > precio) return "La seña no puede ser mayor que el precio.";
    return null;
}

// DiaSemana como DayOfWeek (0 = domingo ... 6 = sábado); el bloque tiene que terminar después
// de empezar y dentro del mismo día.
static string? ErrorHorario(HorarioRequest req)
{
    if (req.DiaSemana < 0 || req.DiaSemana > 6) return "El día de la semana tiene que estar entre 0 (domingo) y 6 (sábado).";
    if (req.HoraInicio < TimeSpan.Zero || req.HoraFin >= TimeSpan.FromDays(1)) return "El horario tiene que estar dentro del día.";
    if (req.HoraFin <= req.HoraInicio) return "La hora de fin tiene que ser posterior a la de inicio.";
    return null;
}

// El cliente cancela por el link solo si el turno no está cancelado y faltan al menos
// HorasMinimasParaCancelar horas para que empiece (esto también deja afuera los ya pasados).
bool PuedeCancelarOnline(Turno t) =>
    t.EstadoReserva != 3 && t.FechaHoraInicio - AhoraArgentina() >= TimeSpan.FromHours(HorasMinimasParaCancelar);

// Lock exclusivo sobre la agenda (comercio + profesional) hasta el fin de la transacción
// actual: dos reservas al mismo tiempo para la misma agenda se hacen de a una, así la
// segunda ya ve el turno de la primera al chequear choques. Va con sp_getapplock en vez de
// aislamiento serializable para no terminar en deadlocks entre las dos reservas.
static async Task BloquearAgenda(AppDbContext context, int comercioId, int? profesionalId)
{
    var recurso = $"reserva2-agenda-{comercioId}-{profesionalId?.ToString() ?? "general"}";
    await context.Database.ExecuteSqlInterpolatedAsync($@"
        DECLARE @resultado int;
        EXEC @resultado = sp_getapplock @Resource = {recurso}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000;
        IF @resultado < 0 THROW 50001, 'No se pudo bloquear la agenda para reservar.', 1;");
}

static string FormatoFechaTurno(DateTime fecha) =>
    fecha.ToString("dddd d 'de' MMMM 'a las' HH:mm", new System.Globalization.CultureInfo("es-AR"));

// Decodifica la captura del comprobante de seña y verifica que de verdad sea una imagen
// PNG, JPG o WEBP (por su contenido, no por un nombre o tipo que manda el navegador) y que
// no pase de 5MB. Devuelve null si falta o no es válida.
static (byte[] Contenido, string Extension)? LeerComprobante(string? base64)
{
    if (string.IsNullOrWhiteSpace(base64)) return null;
    var datos = base64.Trim();
    var coma = datos.IndexOf(',');
    if (datos.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && coma >= 0) datos = datos[(coma + 1)..];

    byte[] bytes;
    try { bytes = Convert.FromBase64String(datos); }
    catch (FormatException) { return null; }

    const int tamañoMaximo = 5 * 1024 * 1024;
    if (bytes.Length == 0 || bytes.Length > tamañoMaximo) return null;

    var extension = ExtensionDeImagen(bytes);
    return extension is null ? null : (bytes, extension);
}

// Extensión según los primeros bytes del archivo (PNG, JPG o WEBP); null si no es ninguna.
static string? ExtensionDeImagen(byte[] bytes)
{
    if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
    if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
    if (bytes.Length >= 12 && bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
        && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P') return ".webp";
    return null;
}

// Un comercio pausado (no pagó) no puede recibir turnos nuevos por el link público,
// pero el panel del Admin Cliente sigue mostrando sus datos sin restricciones.
static IResult ResultadoComercioInactivo() =>
    Results.Json(new { mensaje = "Esta agenda no está disponible temporalmente." }, statusCode: StatusCodes.Status403Forbidden);

// ==========================================
// HASHING DE CONTRASEÑAS (PBKDF2)
// ==========================================
static string HashPassword(string password)
{
    var salt = RandomNumberGenerator.GetBytes(16);
    var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
    return $"{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
}

static bool VerifyPassword(string password, string stored)
{
    var partes = stored.Split('.');
    if (partes.Length != 2) return false;
    var salt = Convert.FromBase64String(partes[0]);
    var hashEsperado = Convert.FromBase64String(partes[1]);
    var hashIngresado = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
    return CryptographicOperations.FixedTimeEquals(hashEsperado, hashIngresado);
}

// ==========================================
// EMAIL (recuperación de contraseña, confirmación de turno)
// ==========================================
// DigitalOcean bloquea de fábrica los puertos SMTP salientes (25/465/587) por política
// antiabuso — un SmtpClient directo nunca va a conectar ahí, no es arreglable desde acá ni
// desde la config del droplet. Por eso se manda por la API HTTP de Brevo (HTTPS normal en
// el puerto 443, no depende de ningún puerto SMTP). La configuración vieja de Smtp:Host/
// Port/User/Password queda en appsettings.json sin usarse, por si hace falta en el futuro.
var brevoHttpClient = new HttpClient();

// Si no hay Brevo:ApiKey configurado (ej. en desarrollo sin credenciales todavía), no falla:
// simplemente no se envía el mail y queda logueado el motivo. "adjunto" es opcional (ej. el
// .ics de un turno) — Brevo lo soporta nativo en el mismo POST, en base64. "links" son las
// URLs que armamos nosotros (cancelación, calendario, panel...): son las únicas que quedan
// clickeables en el mail.
async Task EnviarEmail(IConfiguration config, ILogger logger, string destinatario, string asunto, string cuerpo,
    (string NombreArchivo, string ContenidoBase64)? adjunto = null, IEnumerable<string>? links = null)
{
    var apiKey = config["Brevo:ApiKey"];
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        logger.LogWarning("Brevo:ApiKey no está configurado; no se envió el email a {Destinatario} ({Asunto}).", destinatario, asunto);
        return;
    }

    var remitente = config["Smtp:From"] ?? config["Smtp:User"] ?? "no-reply@reservados2.com";

    var body = new Dictionary<string, object>
    {
        ["sender"] = new { email = remitente, name = "Reserva2" },
        ["to"] = new[] { new { email = destinatario } },
        ["subject"] = asunto,
        ["htmlContent"] = CuerpoEmailHtml(cuerpo, links ?? [])
    };
    if (adjunto is not null)
        body["attachment"] = new[] { new { name = adjunto.Value.NombreArchivo, content = adjunto.Value.ContenidoBase64 } };

    using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email")
    {
        Content = JsonContent.Create(body)
    };
    request.Headers.Add("api-key", apiKey);

    var respuesta = await brevoHttpClient.SendAsync(request);
    if (!respuesta.IsSuccessStatusCode)
    {
        var detalle = await respuesta.Content.ReadAsStringAsync();
        logger.LogError("Brevo devolvió {StatusCode} al enviar el email a {Destinatario} ({Asunto}): {Detalle}",
            respuesta.StatusCode, destinatario, asunto, detalle);
    }
}

// Los cuerpos se arman como texto plano con "\n" y traen datos que escribe cualquiera
// (nombre del cliente, del servicio, del comercio), así que primero se escapa TODO el texto
// —nadie puede meter HTML ni links propios en un mail que sale a nombre de Reserva2— y
// recién después se convierten en <a> solo las URLs nuestras y los saltos de línea en <br>.
static string CuerpoEmailHtml(string cuerpo, IEnumerable<string> links)
{
    var html = WebUtility.HtmlEncode(cuerpo.Replace("\r\n", "\n"));

    var patron = string.Join("|", links
        .Where(l => !string.IsNullOrWhiteSpace(l))
        .Select(l => WebUtility.HtmlEncode(l))
        .Distinct()
        .OrderByDescending(l => l.Length)
        .Select(System.Text.RegularExpressions.Regex.Escape));
    if (patron.Length > 0)
        html = System.Text.RegularExpressions.Regex.Replace(html, patron, m => $"<a href=\"{m.Value}\">{m.Value}</a>");

    return html.Replace("\n", "<br>");
}

// Fire-and-forget: el pedido que lo dispara ya terminó su trabajo (el turno está guardado o
// cancelado), así que un mail lento o caído no puede demorar ni hacer fallar la respuesta.
// Todo lo que se use acá adentro tiene que venir ya capturado en valores, no en entidades
// del DbContext del request.
void EnviarEmailEnSegundoPlano(IConfiguration config, ILogger logger, string destinatario, string asunto, string cuerpo,
    string descripcion, IEnumerable<string>? links = null)
{
    _ = Task.Run(async () =>
    {
        try
        {
            await EnviarEmail(config, logger, destinatario, asunto, cuerpo, links: links);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo enviar el email de {Descripcion}.", descripcion);
        }
    });
}

// ==========================================
// CALENDARIO (link "Agregar a Google Calendar" + archivo .ics adjunto al mail de
// confirmación). Sin credenciales de Google: es solo un link con el evento precargado y un
// archivo .ics estándar, que funciona igual con Google/Apple/Outlook/lo que sea.
// ==========================================
// FechaHoraInicio/Fin de Turno se guardan en hora Argentina "pelada" (ver AhoraArgentina más
// arriba); para un link o archivo de calendario hace falta UTC real, así que se le suman las
// 3 horas de vuelta.
static string EscaparTextoIcs(string texto) =>
    texto.Replace("\\", "\\\\").Replace(",", "\\,").Replace(";", "\\;").Replace("\n", "\\n");

static string GenerarIcs(string titulo, DateTime inicioLocal, DateTime finLocal, string descripcion, string ubicacion)
{
    string Fmt(DateTime d) => d.ToString("yyyyMMdd'T'HHmmss'Z'");
    var inicioUtc = inicioLocal.AddHours(3);
    var finUtc = finLocal.AddHours(3);

    var ics =
        "BEGIN:VCALENDAR\r\n" +
        "VERSION:2.0\r\n" +
        "PRODID:-//Reserva2//ES\r\n" +
        "CALSCALE:GREGORIAN\r\n" +
        "BEGIN:VEVENT\r\n" +
        $"UID:{Guid.NewGuid()}@reservados2.com\r\n" +
        $"DTSTAMP:{Fmt(DateTime.UtcNow)}\r\n" +
        $"DTSTART:{Fmt(inicioUtc)}\r\n" +
        $"DTEND:{Fmt(finUtc)}\r\n" +
        $"SUMMARY:{EscaparTextoIcs(titulo)}\r\n" +
        $"DESCRIPTION:{EscaparTextoIcs(descripcion)}\r\n" +
        $"LOCATION:{EscaparTextoIcs(ubicacion)}\r\n" +
        "END:VEVENT\r\n" +
        "END:VCALENDAR\r\n";

    return ics;
}

static string LinkGoogleCalendar(string titulo, DateTime inicioLocal, DateTime finLocal, string descripcion, string ubicacion)
{
    string Fmt(DateTime d) => d.ToString("yyyyMMdd'T'HHmmss'Z'");
    var inicioUtc = inicioLocal.AddHours(3);
    var finUtc = finLocal.AddHours(3);

    var query = string.Join("&", new[]
    {
        "action=TEMPLATE",
        $"text={Uri.EscapeDataString(titulo)}",
        $"dates={Fmt(inicioUtc)}/{Fmt(finUtc)}",
        $"details={Uri.EscapeDataString(descripcion)}",
        $"location={Uri.EscapeDataString(ubicacion)}"
    });
    return $"https://calendar.google.com/calendar/render?{query}";
}

// ==========================================
// WHATSAPP (confirmación de turno — exclusivo plan Premium)
// ==========================================
var whatsAppHttpClient = new HttpClient();

// Saca espacios/guiones/paréntesis, antepone el código de país (54) si no lo tiene, y
// asegura el "9" extra que los celulares argentinos necesitan después del 54 para que
// WhatsApp entregue el mensaje (549 + código de área + número) — sin ese "9" el envío
// falla silenciosamente del lado de Meta. Si el número ya lo trae (el cliente lo escribió
// con 9 adelante, o ya venía como 549...), no se duplica.
static string FormatearNumeroWhatsApp(string numero)
{
    var limpio = new string(numero.Where(char.IsDigit).ToArray());
    if (!limpio.StartsWith("54")) limpio = "54" + limpio;
    if (limpio.Length < 3 || limpio[2] != '9') limpio = limpio.Insert(2, "9");
    return limpio;
}

// Meta rechaza la plantilla entera si una variable viene vacía, o trae saltos de línea,
// tabs o más de 4 espacios seguidos (error 132018). Ej.: un comercio con seña que todavía
// no cargó su alias/CBU haría fallar el aviso completo; en ese caso se manda "-".
static string LimpiarParametroWhatsApp(string? valor)
{
    var limpio = System.Text.RegularExpressions.Regex.Replace(valor ?? string.Empty, @"\s+", " ").Trim();
    return limpio.Length == 0 ? "-" : limpio;
}

// Si falta configuración (ej. en desarrollo sin credenciales todavía), no falla: simplemente
// no se envía nada y queda logueado el motivo. "parametros" van en el mismo orden que las
// variables numeradas de la plantilla en WhatsApp Business.
async Task EnviarPlantillaWhatsApp(IConfiguration config, ILogger logger, string numeroDestino, string nombrePlantilla, IEnumerable<string> parametros)
{
    var token = config["WhatsApp:Token"];
    var phoneNumberId = config["WhatsApp:PhoneNumberId"];
    if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(phoneNumberId))
    {
        logger.LogWarning("WhatsApp:Token o WhatsApp:PhoneNumberId no están configurados; no se envió la plantilla {Plantilla} a {Numero}.", nombrePlantilla, numeroDestino);
        return;
    }

    var numeroFormateado = FormatearNumeroWhatsApp(numeroDestino);

    using var request = new HttpRequestMessage(HttpMethod.Post, $"https://graph.facebook.com/v21.0/{phoneNumberId}/messages")
    {
        Content = JsonContent.Create(new
        {
            messaging_product = "whatsapp",
            to = numeroFormateado,
            type = "template",
            template = new
            {
                name = nombrePlantilla,
                language = new { code = "es_AR" },
                components = new[]
                {
                    new
                    {
                        type = "body",
                        parameters = parametros.Select(p => new { type = "text", text = LimpiarParametroWhatsApp(p) }).ToArray()
                    }
                }
            }
        })
    };
    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

    var respuesta = await whatsAppHttpClient.SendAsync(request);
    if (!respuesta.IsSuccessStatusCode)
    {
        var detalle = await respuesta.Content.ReadAsStringAsync();
        logger.LogError("WhatsApp Cloud API devolvió {StatusCode} al enviar la plantilla {Plantilla} a {Numero}: {Detalle}",
            respuesta.StatusCode, nombrePlantilla, numeroFormateado, detalle);
    }
}

// ==========================================
// AVISOS DE TURNO RESERVADO (mail al comercio, mail al cliente, WhatsApp Premium)
// ==========================================
// Se llama cuando un turno de la página pública queda reservado: al momento de reservar, o
// recién cuando Mercado Pago aprueba la seña si se pagó por ahí (antes de eso el turno es
// solo una pre-reserva que se libera sola si no se paga, y no se le avisa a nadie).
async Task NotificarTurnoReservado(AppDbContext context, IConfiguration config, ILogger logger, Turno turno, Comercio comercio, Servicio servicio)
{
    var seniaPorMercadoPago = turno.SeñaMedio == "MercadoPago";
    var montoSenia = turno.SeñaMedio is null ? null : seniaPorMercadoPago ? turno.MontoMercadoPago ?? servicio.MontoSeña : servicio.MontoSeña;
    var montoSeniaTexto = montoSenia?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) ?? "";
    // Por Mercado Pago el comercio puede cobrar el servicio completo en vez de la seña.
    var pagoCompleto = seniaPorMercadoPago && montoSenia >= servicio.Precio;
    var queSePago = pagoCompleto ? "El turno ya está pagado" : "La seña ya está pagada";

    // Aviso al comercio: hasta ahora solo se enteraba entrando al panel.
    if (EmailValido(comercio.Email))
    {
        var nombreProfesional = turno.ProfesionalId is null
            ? "Sin asignar"
            : await context.Profesionales.Where(p => p.Id == turno.ProfesionalId).Select(p => p.Nombre).FirstOrDefaultAsync() ?? "Sin asignar";
        var linkPanel = $"{config["Frontend:BaseUrl"] ?? "http://localhost:4200"}/panel";
        var textoSeniaComercio = montoSenia is null ? ""
            : seniaPorMercadoPago ? $"{queSePago} con Mercado Pago (${montoSeniaTexto}): la plata está en tu cuenta.\n\n"
            : "Seña a verificar: revisá el comprobante en el panel.\n\n";

        EnviarEmailEnSegundoPlano(config, logger, comercio.Email, $"Nuevo turno: {turno.ClienteNombre} - {FormatoFechaTurno(turno.FechaHoraInicio)}",
            $"Tenés un turno nuevo reservado desde tu link de Reserva2.\n\n" +
            $"Cliente: {turno.ClienteNombre}\n" +
            $"Servicio: {servicio.Nombre}\n" +
            $"Fecha y hora: {FormatoFechaTurno(turno.FechaHoraInicio)}\n" +
            $"Profesional: {nombreProfesional}\n" +
            $"WhatsApp: {(string.IsNullOrWhiteSpace(turno.ClienteWhatsApp) ? "-" : turno.ClienteWhatsApp.Trim())}\n" +
            $"Email: {turno.ClienteEmail}\n\n" +
            textoSeniaComercio +
            $"Ver tus turnos: {linkPanel}",
            "aviso de turno nuevo al comercio",
            links: [linkPanel]);
    }

    if (!string.IsNullOrWhiteSpace(turno.ClienteEmail))
    {
        // El turno YA está guardado en este punto — lo que pase con el mail de acá en más
        // no puede hacer fallar la respuesta al cliente. Se dispara sin esperarlo (fire-and-
        // forget) para que ni siquiera lo demore: si el envío tarda o el SMTP no responde
        // (con el timeout ya puesto en EnviarEmail, como mucho 8s), el cliente ya tiene su
        // confirmación hace rato. Se capturan los valores que hacen falta antes de arrancar
        // la tarea de fondo porque las entidades del DbContext del request pueden no seguir
        // vivas igual de simple una vez que termina.
        var clienteEmail = turno.ClienteEmail;
        var clienteNombre = turno.ClienteNombre;
        var fechaHoraInicio = turno.FechaHoraInicio;
        var fechaHoraFin = turno.FechaHoraFin;
        var nombreComercio = comercio.Nombre;
        var nombreServicio = servicio.Nombre;
        var tokenCancelacion = turno.TokenCancelacion;

        _ = Task.Run(async () =>
        {
            try
            {
                var fechaTexto = fechaHoraInicio.ToString("dddd d 'de' MMMM 'a las' HH:mm", new System.Globalization.CultureInfo("es-AR"));
                var baseUrlCancelacion = config["Frontend:BaseUrl"] ?? "http://localhost:4200";
                var linkCancelacion = $"{baseUrlCancelacion}/cancelar-turno?token={tokenCancelacion}";

                var tituloEvento = $"{nombreServicio} en {nombreComercio}";
                var descripcionEvento = $"Turno reservado con Reserva2. Cancelalo acá: {linkCancelacion}";
                var linkGoogleCalendar = LinkGoogleCalendar(tituloEvento, fechaHoraInicio, fechaHoraFin, descripcionEvento, nombreComercio);
                var icsBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    GenerarIcs(tituloEvento, fechaHoraInicio, fechaHoraFin, descripcionEvento, nombreComercio)));

                var textoSenia = montoSenia is null ? ""
                    : seniaPorMercadoPago ? (pagoCompleto ? $"Tu turno quedó pagado con Mercado Pago (${montoSeniaTexto}).\n\n" : $"Tu seña de ${montoSeniaTexto} quedó pagada con Mercado Pago.\n\n")
                    : $"Recibimos el comprobante de tu seña de ${montoSeniaTexto}. {nombreComercio} lo va a verificar.\n\n";

                await EnviarEmail(config, logger, clienteEmail, $"Turno reservado en {nombreComercio}",
                    $"Hola {clienteNombre},\n\n" +
                    $"Tu turno para \"{nombreServicio}\" en {nombreComercio} quedó reservado para el {fechaTexto}.\n\n" +
                    textoSenia +
                    $"¿No podés ir? Cancelalo acá: {linkCancelacion}\n\n" +
                    $"¿Querés agregarlo a tu calendario? {linkGoogleCalendar}\n" +
                    "(También te dejamos un archivo adjunto que sirve para cualquier calendario, no solo Google.)\n\n" +
                    "Gracias por reservar con Reserva2.",
                    ("turno.ics", icsBase64),
                    [linkCancelacion, linkGoogleCalendar]);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "No se pudo enviar el email de confirmación de turno.");
            }
        });
    }

    // Confirmación de turno por WhatsApp — exclusivo plan Premium, y solo si el comercio
    // activó el bot. Si no se cumple cualquiera de las dos, no se intenta nada (ni se
    // loguea): simplemente no aplica para este comercio.
    if (comercio.PlanActual == "Premium")
    {
        var whatsAppConfig = await context.WhatsAppConfigs.FirstOrDefaultAsync(w => w.ComercioId == comercio.Id);
        if (whatsAppConfig?.Activado == true)
        {
            // Mismo criterio que en el mail: se capturan los valores antes de arrancar la
            // tarea de fondo, y el envío es fire-and-forget con su propio try/catch — un
            // error de la API de WhatsApp (ej. plantilla todavía no aprobada) no puede
            // demorar ni hacer fallar la respuesta al cliente, que ya tiene su turno guardado.
            var clienteWhatsApp = turno.ClienteWhatsApp;
            var clienteNombre = turno.ClienteNombre;
            var fechaHoraInicio = turno.FechaHoraInicio;
            var nombreComercio = comercio.Nombre;
            var datosBancarios = comercio.DatosBancarios;
            // La plantilla con seña le pide al cliente que transfiera; si ya la pagó con
            // Mercado Pago, va la confirmación común.
            var senia = seniaPorMercadoPago ? null : montoSenia;

            _ = Task.Run(async () =>
            {
                try
                {
                    var fechaTexto = fechaHoraInicio.ToString("dddd d 'de' MMMM", new System.Globalization.CultureInfo("es-AR"));
                    var horaTexto = fechaHoraInicio.ToString("HH:mm");

                    if (senia is not null)
                    {
                        await EnviarPlantillaWhatsApp(config, logger, clienteWhatsApp, "confirmacion_turno_sena", new[]
                        {
                            clienteNombre,
                            nombreComercio,
                            fechaTexto,
                            horaTexto,
                            senia.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                            datosBancarios
                        });
                    }
                    else
                    {
                        await EnviarPlantillaWhatsApp(config, logger, clienteWhatsApp, "confirmacion_turno", new[]
                        {
                            clienteNombre,
                            nombreComercio,
                            fechaTexto,
                            horaTexto
                        });
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "No se pudo enviar la confirmación de turno por WhatsApp.");
                }
            });
        }
    }
}

// ==========================================
// MERCADO PAGO
// ==========================================
// Dos usos, con cuentas distintas:
//  - Seña de un turno: el comercio conecta SU cuenta (OAuth, desde Perfil) y la plata de la
//    seña va directo a él. Hace falta la aplicación de Reserva2 en Mercado Pago Developers:
//    MercadoPago:ClientId y MercadoPago:ClientSecret.
//  - Plan del comercio: el comercio le paga a Reserva2 desde "Mi plan", con la cuenta de
//    Reserva2: MercadoPago:AccessToken.
// Las notificaciones (webhooks) y la vuelta del checkout llegan a la API en el mismo dominio
// del frontend (nginx manda /api al backend); se puede pisar con MercadoPago:UrlApi.
string UrlFrontend(IConfiguration config) => (config["Frontend:BaseUrl"] ?? "http://localhost:4200").TrimEnd('/');
string UrlApi(IConfiguration config) => (config["MercadoPago:UrlApi"] ?? config["Frontend:BaseUrl"] ?? "http://localhost:5267").TrimEnd('/');
string RedirectUriOAuthMp(IConfiguration config) => $"{UrlApi(config)}/api/mercadopago/oauth/callback";

bool OAuthMercadoPagoConfigurado(IConfiguration config) =>
    !string.IsNullOrWhiteSpace(config["MercadoPago:ClientId"]) && !string.IsNullOrWhiteSpace(config["MercadoPago:ClientSecret"]);

// Mercado Pago pide fechas con offset; Argentina es siempre UTC-3 (ver AhoraArgentina).
static string FechaMercadoPago(DateTime horaArgentina) => horaArgentina.ToString("yyyy-MM-dd'T'HH:mm:ss.fff") + "-03:00";

// Los medios en efectivo (Rapipago, Pago Fácil) se acreditan días después: no sirven para
// una seña que tiene que llegar en minutos ni para reactivar un plan en el momento.
static MediosDePagoMp SinPagosEnEfectivo() => new([new TipoDePagoMp("ticket"), new TipoDePagoMp("atm")]);

void GuardarTokensMercadoPago(Comercio comercio, TokenOAuthMp token)
{
    comercio.MercadoPagoUserId = token.UserId;
    comercio.MercadoPagoAccessToken = protectorTokensMp.Protect(token.AccessToken);
    comercio.MercadoPagoRefreshToken = token.RefreshToken is null ? null : protectorTokensMp.Protect(token.RefreshToken);
    comercio.MercadoPagoTokenVence = DateTime.UtcNow.AddSeconds(token.ExpiresIn);
}

// Access token (descifrado) de la cuenta de Mercado Pago del comercio. Los tokens de OAuth
// duran 180 días: si al usarlo le falta menos de una semana, se renueva con el refresh token
// y se guarda el nuevo. Null si el comercio no conectó su cuenta o ya no se puede usar.
async Task<string?> AccessTokenMercadoPagoComercio(AppDbContext context, IConfiguration config, ILogger logger, Comercio comercio)
{
    if (comercio.MercadoPagoAccessToken is null) return null;

    try
    {
        if (comercio.MercadoPagoTokenVence < DateTime.UtcNow.AddDays(7) && comercio.MercadoPagoRefreshToken is not null && OAuthMercadoPagoConfigurado(config))
        {
            var nuevo = await mercadoPago.RefrescarToken(config["MercadoPago:ClientId"]!, config["MercadoPago:ClientSecret"]!,
                protectorTokensMp.Unprotect(comercio.MercadoPagoRefreshToken));
            if (nuevo is not null)
            {
                GuardarTokensMercadoPago(comercio, nuevo);
                await context.SaveChangesAsync();
            }
        }
        return comercio.MercadoPagoTokenVence > DateTime.UtcNow ? protectorTokensMp.Unprotect(comercio.MercadoPagoAccessToken) : null;
    }
    catch (System.Security.Cryptography.CryptographicException ex)
    {
        // Se perdieron las claves de DataProtection: el comercio tiene que reconectar.
        logger.LogError(ex, "No se pudo descifrar el token de Mercado Pago del comercio {ComercioId}.", comercio.Id);
        return null;
    }
}

bool CobraSeniaPorMercadoPago(Comercio comercio) =>
    comercio.AddonCobrosOnline && comercio.SeñaPorMercadoPago && comercio.MercadoPagoAccessToken is not null;

// Cuánto se cobra por Mercado Pago al reservar este servicio: el precio completo si el
// comercio eligió cobrar el servicio entero, si no la seña. Null si no se cobra nada.
decimal? MontoMercadoPagoServicio(Comercio comercio, Servicio servicio)
{
    if (!CobraSeniaPorMercadoPago(comercio)) return null;
    if (comercio.CobroMercadoPagoTotal && servicio.Precio > 0) return servicio.Precio;
    return servicio.MontoSeña is > 0 ? servicio.MontoSeña : null;
}

// El id del pago llega en el query (?data.id=...&type=payment, o ?id=...&topic=payment en el
// formato viejo) y/o en el cuerpo JSON ({"type":"payment","data":{"id":"123"}}). Null si la
// notificación no es de un pago (ej. merchant_order), que se ignora.
static async Task<long?> IdPagoDeNotificacionMp(HttpRequest request)
{
    string? tipo = request.Query["type"].FirstOrDefault() ?? request.Query["topic"].FirstOrDefault();
    string? id = request.Query["data.id"].FirstOrDefault() ?? request.Query["id"].FirstOrDefault();

    try
    {
        using var cuerpo = await JsonDocument.ParseAsync(request.Body);
        var raiz = cuerpo.RootElement;
        if (raiz.ValueKind == JsonValueKind.Object)
        {
            if (raiz.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String) tipo = t.GetString();
            if (raiz.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object && d.TryGetProperty("id", out var i))
                id = i.ValueKind == JsonValueKind.Number ? i.GetRawText() : i.GetString();
        }
    }
    catch (JsonException)
    {
        // Cuerpo vacío o no JSON: queda lo que vino en el query.
    }

    return tipo == "payment" && long.TryParse(id, out var pagoId) ? pagoId : null;
}

// Consulta el pago en Mercado Pago (con la cuenta del comercio) y, si está aprobado y es la
// seña de un turno de ese comercio, confirma el turno y recién ahí avisa a todos. Es
// idempotente: lo llaman tanto el webhook como la página pública cuando el cliente vuelve
// del checkout, y el que llega segundo no hace nada. Devuelve el estado del turno para la
// página pública: "confirmado", "pendiente", "rechazado", "horario_ocupado" o "desconocido".
async Task<string> ProcesarPagoSenia(AppDbContext context, IConfiguration config, ILogger logger, Comercio comercio, long pagoId)
{
    var accessToken = await AccessTokenMercadoPagoComercio(context, config, logger, comercio);
    if (accessToken is null) return "desconocido";

    var pago = await mercadoPago.ObtenerPago(accessToken, pagoId);
    if (pago?.ExternalReference is not { } referencia || !referencia.StartsWith("turno-")
        || !int.TryParse(referencia["turno-".Length..], out var turnoId))
        return "desconocido";

    var turno = await context.Turnos.FirstOrDefaultAsync(t => t.Id == turnoId && t.ComercioId == comercio.Id && t.SeñaMedio == "MercadoPago");
    if (turno is null) return "desconocido";

    if (pago.Status != "approved")
    {
        if (turno.MercadoPagoPagoId is not null) return turno.EstadoReserva == 2 ? "confirmado" : "horario_ocupado";
        return pago.Status is "pending" or "in_process" or "authorized" ? "pendiente" : "rechazado";
    }

    // Mismo lock que al reservar: si el webhook y la vuelta del cliente llegan juntos, el
    // segundo espera y ve que el pago ya se procesó; y si el horario se liberó mientras el
    // cliente pagaba, nadie más lo puede tomar justo ahora.
    await using var transaccion = await context.Database.BeginTransactionAsync();
    await BloquearAgenda(context, turno.ComercioId, turno.ProfesionalId);
    await context.Entry(turno).ReloadAsync();
    if (turno.MercadoPagoPagoId is not null) return turno.EstadoReserva == 2 ? "confirmado" : "horario_ocupado";

    turno.MercadoPagoPagoId = pago.Id;
    turno.SeñaVerificada = true;

    // Si el pago llegó tarde (la pre-reserva ya se había liberado o el cliente la abandonó),
    // el turno se recupera solo si nadie tomó ese horario mientras tanto.
    var chocaConOtro = (await context.Turnos
        .Where(t => t.Id != turno.Id
                 && t.ComercioId == turno.ComercioId
                 && t.ProfesionalId == turno.ProfesionalId
                 && t.FechaHoraInicio < turno.FechaHoraFin
                 && t.FechaHoraFin > turno.FechaHoraInicio)
        .ToListAsync()).Any(OcupaHorario);

    if (!chocaConOtro) turno.EstadoReserva = 2;
    await context.SaveChangesAsync();
    await transaccion.CommitAsync();

    var servicio = await context.Servicios.FindAsync(turno.ServicioId);
    if (turno.EstadoReserva == 2)
    {
        if (servicio is not null) await NotificarTurnoReservado(context, config, logger, turno, comercio, servicio);
        return "confirmado";
    }

    // El cliente pagó pero el horario ya es de otro: el comercio tiene que devolverle la
    // seña o acordar otro horario. Se avisa a los dos.
    logger.LogWarning("Seña aprobada en Mercado Pago (pago {PagoId}) para el turno {TurnoId}, pero el horario ya estaba ocupado.", pago.Id, turno.Id);
    var detalle = $"{servicio?.Nombre ?? "Turno"} - {FormatoFechaTurno(turno.FechaHoraInicio)}";
    if (EmailValido(comercio.Email))
        EnviarEmailEnSegundoPlano(config, logger, comercio.Email, $"Seña pagada sin horario: {turno.ClienteNombre}",
            $"{turno.ClienteNombre} pagó la seña con Mercado Pago (pago {pago.Id}) después de que se venció su reserva, y ese horario ya lo tomó otra persona.\n\n" +
            $"Reserva: {detalle}\nWhatsApp: {turno.ClienteWhatsApp}\nEmail: {turno.ClienteEmail}\n\n" +
            "Escribile para acordar otro horario o devolvele la seña desde tu cuenta de Mercado Pago.",
            "aviso de seña pagada sin horario al comercio");
    if (EmailValido(turno.ClienteEmail))
        EnviarEmailEnSegundoPlano(config, logger, turno.ClienteEmail, $"Tu pago a {comercio.Nombre} llegó tarde",
            $"Hola {turno.ClienteNombre},\n\nRecibimos tu seña, pero tu reserva se había vencido y ese horario ({detalle}) ya lo tomó otra persona.\n\n" +
            $"Ya le avisamos a {comercio.Nombre} para que te escriba y acuerden otro horario o te devuelva la seña.",
            "aviso de seña pagada sin horario al cliente");
    return "horario_ocupado";
}

// Monto que se le cobra a un comercio por un plan y ciclo. Si renueva el mismo plan y ciclo
// que tiene y el Super Admin le acordó un monto puntual, se respeta ese monto (es mensual:
// el anual son 12 meses de ese monto, y tiene que incluir el extra de cobros si lo tiene).
// Si no, la tarifa de lista con sus profesionales y sucursales reales, más el extra de
// cobros automáticos si lo tiene activo (con el mismo 25% de descuento si es anual).
async Task<decimal> MontoPlanComercio(AppDbContext context, Comercio comercio, string plan, string ciclo)
{
    if (plan == comercio.PlanActual && ciclo == comercio.CicloFacturacion && comercio.MontoMensualAcordado is > 0)
        return ciclo == "Anual" ? comercio.MontoMensualAcordado.Value * 12 : comercio.MontoMensualAcordado.Value;

    var profesionales = await context.Profesionales.CountAsync(p => p.ComercioId == comercio.Id);
    var sucursales = await context.Sucursales.CountAsync(s => s.ComercioId == comercio.Id);
    var precio = CalcularPrecioPlan(plan, ciclo, Math.Max(1, profesionales), Math.Max(1, sucursales));
    if (comercio.AddonCobrosOnline && precio > 0)
        precio += ciclo == "Anual" ? Math.Round(PrecioAddonCobrosMensual * 12 * 0.75m, 2) : PrecioAddonCobrosMensual;
    return precio;
}

// Aplica un pago de plan aprobado: cambia plan/ciclo si eligió otro, corre el vencimiento un
// ciclo (desde el vencimiento actual si renovó antes de tiempo el mismo plan, si no desde
// hoy) y reactiva el comercio si estaba pausado.
void AplicarPagoPlan(Comercio comercio, Pago pago)
{
    var hoy = AhoraArgentina().Date;
    var mismoPlan = comercio.PlanActual == pago.Plan && comercio.CicloFacturacion == pago.Ciclo;
    var desde = mismoPlan && comercio.FechaProximoPago > hoy ? comercio.FechaProximoPago.Value : hoy;

    if (!mismoPlan || comercio.MontoMensualAcordado is null)
        comercio.MontoMensualAcordado = pago.Ciclo == "Anual" ? Math.Round(pago.Monto / 12, 2) : pago.Monto;
    comercio.PlanActual = pago.Plan;
    comercio.CicloFacturacion = pago.Ciclo;
    comercio.FechaProximoPago = pago.Ciclo == "Anual" ? desde.AddYears(1) : desde.AddMonths(1);
    if (!comercio.Activo)
    {
        comercio.Activo = true;
        comercio.FechaBaja = null;
        comercio.FechaActivacion ??= AhoraArgentina();
    }
}

// Consulta el pago en la cuenta de Reserva2 y, si está aprobado, lo aplica al comercio una
// sola vez (el webhook y la vuelta del comercio al panel pueden llegar juntos). Devuelve el
// Pago (o null si no es un pago de plan de Reserva2).
async Task<Pago?> ProcesarPagoPlan(AppDbContext context, IConfiguration config, ILogger logger, long pagoMpId)
{
    var accessToken = config["MercadoPago:AccessToken"];
    if (string.IsNullOrWhiteSpace(accessToken)) return null;

    var pagoMp = await mercadoPago.ObtenerPago(accessToken, pagoMpId);
    if (pagoMp?.ExternalReference is not { } referencia || !referencia.StartsWith("pago-")
        || !int.TryParse(referencia["pago-".Length..], out var pagoId))
        return null;

    var pago = await context.Pagos.FirstOrDefaultAsync(p => p.Id == pagoId && p.Medio == "MercadoPago");
    if (pago is null || pago.Estado == "Aprobado") return pago;

    if (pagoMp.Status != "approved")
    {
        if (pagoMp.Status is "rejected" or "cancelled")
        {
            pago.Estado = "Rechazado";
            pago.MercadoPagoPagoId = pagoMp.Id;
            await context.SaveChangesAsync();
        }
        return pago;
    }

    if (pagoMp.TransactionAmount < pago.Monto)
    {
        logger.LogError("El pago {PagoMpId} de Mercado Pago es de {Monto} pero el plan costaba {Esperado} (pago {PagoId}); no se aplicó.",
            pagoMp.Id, pagoMp.TransactionAmount, pago.Monto, pago.Id);
        return pago;
    }

    // "Reclamo" atómico del pago: si dos pedidos llegan juntos, solo uno cambia la fila.
    var reclamado = await context.Pagos
        .Where(p => p.Id == pago.Id && p.Estado != "Aprobado")
        .ExecuteUpdateAsync(u => u
            .SetProperty(p => p.Estado, "Aprobado")
            .SetProperty(p => p.MercadoPagoPagoId, pagoMp.Id)
            .SetProperty(p => p.FechaAprobacion, DateTime.UtcNow));
    await context.Entry(pago).ReloadAsync();
    if (reclamado == 0) return pago;

    var comercio = await context.Comercios.FindAsync(pago.ComercioId);
    if (comercio is null) return pago;
    AplicarPagoPlan(comercio, pago);
    await context.SaveChangesAsync();

    // Aviso para emitir la Factura C a mano en ARCA.
    var emailSuperAdmin = config["SuperAdmin:Email"];
    if (!string.IsNullOrWhiteSpace(emailSuperAdmin) && EmailValido(emailSuperAdmin))
        EnviarEmailEnSegundoPlano(config, logger, emailSuperAdmin, $"Pago recibido: {comercio.Nombre} - ${pago.Monto.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}",
            $"{comercio.Nombre} ({comercio.Email}) pagó su plan con Mercado Pago.\n\n" +
            $"Plan: {pago.Plan} ({pago.Ciclo})\nMonto: ${pago.Monto.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}\n" +
            $"Pago de Mercado Pago: {pagoMp.Id}\nPróximo vencimiento: {comercio.FechaProximoPago:dd/MM/yyyy}\n\n" +
            "Acordate de emitir la Factura C.",
            "aviso de pago de plan al Super Admin");

    return pago;
}

// ==========================================
// JWT: emisión y lectura de claims
// ==========================================
string GenerarToken(string rol, int? comercioId = null)
{
    var claims = new List<Claim> { new(ClaimTypes.Role, rol) };
    if (comercioId is not null)
        claims.Add(new Claim("comercioId", comercioId.Value.ToString()));

    var credenciales = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);
    var token = new JwtSecurityToken(
        claims: claims,
        expires: DateTime.UtcNow.AddHours(DuracionTokenHoras),
        signingCredentials: credenciales);
    return new JwtSecurityTokenHandler().WriteToken(token);
}

// El comercio dueño de la sesión actual (null si el token no trae claim, ej. Super Admin)
static int? ComercioIdDelToken(ClaimsPrincipal user) =>
    int.TryParse(user.FindFirst("comercioId")?.Value, out var id) ? id : null;

// Público (se usa desde el formulario de registro, antes de que exista una sesión) para
// mostrar el precio en tiempo real a medida que el usuario cambia plan/ciclo/cantidades.
// Misma cuenta que se va a reutilizar el lunes desde la integración de MercadoPago.
app.MapGet("/api/precio-plan", (string plan, string ciclo, int profesionales, int sucursales) =>
{
    if (!PlanesValidos.Contains(plan))
        return Results.BadRequest(new { mensaje = "Plan inválido. Tiene que ser Gratuito, Basico o Premium." });
    if (ciclo != "Mensual" && ciclo != "Anual")
        return Results.BadRequest(new { mensaje = "El ciclo de facturación tiene que ser Mensual o Anual." });

    var precio = CalcularPrecioPlan(plan, ciclo, Math.Max(1, profesionales), Math.Max(1, sucursales));
    return Results.Ok(new { precio });
});

// ==========================================
// ENDPOINTS DE AUTENTICACIÓN (panel de admin)
// ==========================================
app.MapPost("/api/auth/register", async (AppDbContext context, RegistroRequest req) =>
{
    var nombre = req.Nombre?.Trim() ?? string.Empty;
    var email = req.Email?.Trim() ?? string.Empty;
    var alias = req.AliasUrl?.Trim().ToLowerInvariant() ?? string.Empty;

    if (nombre.Length == 0)
        return Results.BadRequest(new { mensaje = "Ingresá el nombre del negocio." });
    if (!EmailValido(email))
        return Results.BadRequest(new { mensaje = "Ingresá un email válido." });
    if ((req.Password?.Length ?? 0) < LargoMinimoPassword)
        return Results.BadRequest(new { mensaje = $"La contraseña tiene que tener al menos {LargoMinimoPassword} caracteres." });
    if (ErrorAlias(alias) is { } errorAlias)
        return Results.BadRequest(new { mensaje = errorAlias });

    if (await context.Comercios.AnyAsync(c => c.Email == email))
        return Results.Conflict(new { mensaje = "Ya existe una cuenta con ese email." });

    if (await context.Comercios.AnyAsync(c => c.AliasUrl == alias))
        return Results.Conflict(new { mensaje = "Ese link ya está en uso por otro negocio." });

    var planElegido = req.PlanActual ?? "Gratuito";
    if (!PlanesValidos.Contains(planElegido))
        return Results.BadRequest(new { mensaje = "Plan inválido. Tiene que ser Gratuito, Basico o Premium." });

    var cicloElegido = req.CicloFacturacion ?? "Mensual";
    if (cicloElegido != "Mensual" && cicloElegido != "Anual")
        return Results.BadRequest(new { mensaje = "El ciclo de facturación tiene que ser Mensual o Anual." });

    var cantidadProfesionales = Math.Max(1, req.CantidadProfesionales ?? 1);
    var cantidadSucursales = Math.Max(1, req.CantidadSucursales ?? 1);

    var topeSucursalesRegistro = TopeSucursales(planElegido);
    if (cantidadSucursales > topeSucursalesRegistro)
        return Results.BadRequest(new { mensaje = $"El plan {planElegido} permite hasta {topeSucursalesRegistro} sucursal(es)." });

    var comercio = new Comercio
    {
        Nombre = nombre,
        AliasUrl = alias,
        TipoPlantilla = req.TipoPlantilla,
        TelefonoNotificaciones = req.TelefonoNotificaciones,
        DatosBancarios = req.DatosBancarios,
        Email = email,
        PasswordHash = HashPassword(req.Password!),
        PlanActual = planElegido,
        CicloFacturacion = cicloElegido,
        FechaProximoPago = CalcularProximoPago(cicloElegido),
        CantidadProfesionalesContratada = cantidadProfesionales,
        CantidadSucursalesContratada = cantidadSucursales,
        // Todo registro nuevo entra pausado: el dueño ya puede loguearse y armar su panel,
        // pero su página pública no recibe reservas de clientes reales hasta que el Super
        // Admin lo active a mano (confirmado por WhatsApp), como filtro de entrada.
        Activo = false
    };

    context.Comercios.Add(comercio);
    await context.SaveChangesAsync();

    return Results.Created($"/api/comercios/{comercio.Id}",
        new LoginResponse(comercio.Id, comercio.Nombre, comercio.AliasUrl, GenerarToken("AdminCliente", comercio.Id), comercio.PlanActual, comercio.CicloFacturacion,
            comercio.TelefonoNotificaciones, comercio.DatosBancarios, comercio.LogoUrl, comercio.FechaProximoPago, comercio.Activo));
}).RequireRateLimiting("login");

app.MapPost("/api/auth/login", async (AppDbContext context, LoginRequest req) =>
{
    var comercio = await context.Comercios.FirstOrDefaultAsync(c => c.Email == req.Email);
    if (comercio is null || !VerifyPassword(req.Password, comercio.PasswordHash))
        return Results.Unauthorized();

    comercio.UltimoAcceso = DateTime.UtcNow;
    await context.SaveChangesAsync();

    return Results.Ok(new LoginResponse(comercio.Id, comercio.Nombre, comercio.AliasUrl, GenerarToken("AdminCliente", comercio.Id), comercio.PlanActual, comercio.CicloFacturacion,
            comercio.TelefonoNotificaciones, comercio.DatosBancarios, comercio.LogoUrl, comercio.FechaProximoPago, comercio.Activo));
}).RequireRateLimiting("login");

app.MapPost("/api/auth/forgot-password", async (AppDbContext context, IConfiguration config, ILogger<Program> logger, ForgotPasswordRequest req) =>
{
    var comercio = await context.Comercios.FirstOrDefaultAsync(c => c.Email == req.Email);
    if (comercio is not null)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        context.PasswordResetTokens.Add(new PasswordResetToken
        {
            ComercioId = comercio.Id,
            Token = token,
            FechaExpiracion = DateTime.UtcNow.AddHours(1),
            Usado = false
        });
        await context.SaveChangesAsync();

        var baseUrl = config["Frontend:BaseUrl"] ?? "http://localhost:4200";
        var link = $"{baseUrl}/panel/reset-password?token={token}";

        try
        {
            await EnviarEmail(config, logger, comercio.Email, "Restablecer tu contraseña - Reserva2",
                $"Recibimos un pedido para restablecer la contraseña de tu panel Reserva2.\n\n" +
                $"Hacé click en este link para elegir una nueva contraseña (válido por 1 hora):\n{link}\n\n" +
                "Si no lo pediste vos, podés ignorar este mensaje.",
                links: [link]);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo enviar el email de restablecimiento de contraseña.");
        }
    }

    // Mismo mensaje exista o no la cuenta, para no revelar qué emails están registrados.
    return Results.Ok(new { mensaje = "Si el email existe, te enviamos un link para restablecer tu contraseña." });
}).RequireRateLimiting("login");

app.MapPost("/api/auth/reset-password", async (AppDbContext context, ResetPasswordRequest req) =>
{
    if ((req.NuevaPassword?.Length ?? 0) < LargoMinimoPassword)
        return Results.BadRequest(new { mensaje = $"La contraseña tiene que tener al menos {LargoMinimoPassword} caracteres." });

    var tokenValido = await context.PasswordResetTokens.FirstOrDefaultAsync(t => t.Token == req.Token);
    if (tokenValido is null || tokenValido.Usado || tokenValido.FechaExpiracion < DateTime.UtcNow)
        return Results.BadRequest(new { mensaje = "El link para restablecer la contraseña es inválido o venció." });

    var comercio = await context.Comercios.FindAsync(tokenValido.ComercioId);
    if (comercio is null)
        return Results.BadRequest(new { mensaje = "El link para restablecer la contraseña es inválido o venció." });

    comercio.PasswordHash = HashPassword(req.NuevaPassword!);
    tokenValido.Usado = true;
    await context.SaveChangesAsync();

    return Results.Ok(new { mensaje = "Contraseña actualizada. Ya podés ingresar con tu nueva contraseña." });
}).RequireRateLimiting("login");

app.MapPost("/api/auth/super-admin/login", (IConfiguration config, SuperAdminLoginRequest req) =>
{
    var email = config["SuperAdmin:Email"];
    var passwordHash = config["SuperAdmin:PasswordHash"];
    if (email is null || passwordHash is null || req.Email != email || !VerifyPassword(req.Password, passwordHash))
        return Results.Unauthorized();

    return Results.Ok(new SuperAdminLoginResponse(GenerarToken("SuperAdmin")));
}).RequireRateLimiting("login");

// ==========================================
// ENDPOINTS PARA COMERCIOS
// ==========================================
app.MapGet("/api/comercios", async (AppDbContext context) =>
{
    var comercios = await context.Comercios
        .Select(c => new ComercioDto(c.Id, c.Nombre, c.AliasUrl, c.TipoPlantilla, c.TelefonoNotificaciones,
            c.DatosBancarios, c.Email, c.Activo, c.PlanActual, c.FechaProximoPago, c.MontoMensualAcordado, c.CicloFacturacion))
        .ToListAsync();
    return Results.Ok(comercios);
}).RequireAuthorization("SuperAdmin");

app.MapGet("/api/comercios/{id:int}", async (AppDbContext context, int id) =>
{
    var comercio = await context.Comercios
        .Where(c => c.Id == id)
        .Select(c => new ComercioDto(c.Id, c.Nombre, c.AliasUrl, c.TipoPlantilla, c.TelefonoNotificaciones,
            c.DatosBancarios, c.Email, c.Activo, c.PlanActual, c.FechaProximoPago, c.MontoMensualAcordado, c.CicloFacturacion))
        .FirstOrDefaultAsync();
    return comercio is null ? Results.NotFound() : Results.Ok(comercio);
}).RequireAuthorization("SuperAdmin");

app.MapPatch("/api/comercios/{id:int}/estado", async (AppDbContext context, int id, ActualizarEstadoRequest req) =>
{
    var comercio = await context.Comercios.FindAsync(id);
    if (comercio is null) return Results.NotFound();

    if (comercio.Activo && !req.Activo) comercio.FechaBaja = AhoraArgentina();
    if (req.Activo) comercio.FechaBaja = null;
    comercio.Activo = req.Activo;
    if (req.Activo) comercio.FechaActivacion ??= AhoraArgentina();
    await context.SaveChangesAsync();

    return Results.Ok(new ComercioDto(comercio.Id, comercio.Nombre, comercio.AliasUrl, comercio.TipoPlantilla,
        comercio.TelefonoNotificaciones, comercio.DatosBancarios, comercio.Email, comercio.Activo, comercio.PlanActual, comercio.FechaProximoPago, comercio.MontoMensualAcordado, comercio.CicloFacturacion));
}).RequireAuthorization("SuperAdmin");

app.MapPatch("/api/comercios/{id:int}/plan", async (AppDbContext context, int id, ActualizarPlanRequest req) =>
{
    if (!PlanesValidos.Contains(req.PlanActual))
        return Results.BadRequest(new { mensaje = "Plan inválido. Tiene que ser Gratuito, Basico o Premium." });

    var comercio = await context.Comercios.FindAsync(id);
    if (comercio is null) return Results.NotFound();

    comercio.PlanActual = req.PlanActual;
    comercio.FechaProximoPago = CalcularProximoPago(comercio.CicloFacturacion);
    await context.SaveChangesAsync();

    return Results.Ok(new ComercioDto(comercio.Id, comercio.Nombre, comercio.AliasUrl, comercio.TipoPlantilla,
        comercio.TelefonoNotificaciones, comercio.DatosBancarios, comercio.Email, comercio.Activo, comercio.PlanActual, comercio.FechaProximoPago, comercio.MontoMensualAcordado, comercio.CicloFacturacion));
}).RequireAuthorization("SuperAdmin");

app.MapPatch("/api/comercios/{id:int}/monto-acordado", async (AppDbContext context, int id, ActualizarMontoAcordadoRequest req) =>
{
    var comercio = await context.Comercios.FindAsync(id);
    if (comercio is null) return Results.NotFound();

    comercio.MontoMensualAcordado = req.MontoMensualAcordado;
    await context.SaveChangesAsync();

    return Results.Ok(new ComercioDto(comercio.Id, comercio.Nombre, comercio.AliasUrl, comercio.TipoPlantilla,
        comercio.TelefonoNotificaciones, comercio.DatosBancarios, comercio.Email, comercio.Activo, comercio.PlanActual, comercio.FechaProximoPago, comercio.MontoMensualAcordado, comercio.CicloFacturacion));
}).RequireAuthorization("SuperAdmin");

app.MapPatch("/api/comercios/{id:int}/ciclo-facturacion", async (AppDbContext context, int id, ActualizarCicloFacturacionRequest req) =>
{
    if (req.CicloFacturacion != "Mensual" && req.CicloFacturacion != "Anual")
        return Results.BadRequest(new { mensaje = "El ciclo de facturación tiene que ser Mensual o Anual." });

    var comercio = await context.Comercios.FindAsync(id);
    if (comercio is null) return Results.NotFound();

    comercio.CicloFacturacion = req.CicloFacturacion;
    // El ciclo cambió, así que la fecha de próximo pago (que se cuenta en meses o años según
    // el ciclo) se recalcula para no quedar desalineada con el nuevo ciclo elegido.
    comercio.FechaProximoPago = CalcularProximoPago(comercio.CicloFacturacion);
    await context.SaveChangesAsync();

    return Results.Ok(new ComercioDto(comercio.Id, comercio.Nombre, comercio.AliasUrl, comercio.TipoPlantilla,
        comercio.TelefonoNotificaciones, comercio.DatosBancarios, comercio.Email, comercio.Activo, comercio.PlanActual, comercio.FechaProximoPago, comercio.MontoMensualAcordado, comercio.CicloFacturacion));
}).RequireAuthorization("SuperAdmin");

// El Super Admin marca un pago como recibido (transferencia coordinada a mano por WhatsApp):
// empuja la fecha de próximo pago un ciclo entero desde hoy y reactiva el comercio si estaba
// pausado por falta de pago.
app.MapPatch("/api/comercios/{id:int}/renovar", async (AppDbContext context, int id) =>
{
    var comercio = await context.Comercios.FindAsync(id);
    if (comercio is null) return Results.NotFound();

    // Queda en el historial de pagos, con el monto que corresponde a su plan y ciclo.
    context.Pagos.Add(new Pago
    {
        ComercioId = comercio.Id,
        Monto = await MontoPlanComercio(context, comercio, comercio.PlanActual, comercio.CicloFacturacion),
        Plan = comercio.PlanActual,
        Ciclo = comercio.CicloFacturacion,
        Medio = "Transferencia",
        Estado = "Aprobado",
        FechaAprobacion = DateTime.UtcNow
    });

    comercio.FechaProximoPago = CalcularProximoPago(comercio.CicloFacturacion);
    comercio.Activo = true;
    comercio.FechaActivacion ??= AhoraArgentina();
    await context.SaveChangesAsync();

    return Results.Ok(new ComercioDto(comercio.Id, comercio.Nombre, comercio.AliasUrl, comercio.TipoPlantilla,
        comercio.TelefonoNotificaciones, comercio.DatosBancarios, comercio.Email, comercio.Activo, comercio.PlanActual, comercio.FechaProximoPago, comercio.MontoMensualAcordado, comercio.CicloFacturacion));
}).RequireAuthorization("SuperAdmin");

// El Super Admin activa o quita el extra "Cobros automáticos con Mercado Pago" (se coordina
// y se cobra aparte del plan). Al quitarlo, las señas vuelven a ser solo por transferencia.
app.MapPatch("/api/comercios/{id:int}/addon-cobros", async (AppDbContext context, int id, ActualizarAddonCobrosRequest req) =>
{
    var comercio = await context.Comercios.FindAsync(id);
    if (comercio is null) return Results.NotFound();

    comercio.AddonCobrosOnline = req.Activo;
    if (!req.Activo)
    {
        comercio.SeñaPorMercadoPago = false;
        comercio.SeñaPorTransferencia = true;
    }
    await context.SaveChangesAsync();
    return Results.Ok(new { addonCobrosOnline = comercio.AddonCobrosOnline });
}).RequireAuthorization("SuperAdmin");

// Borrado definitivo de un comercio (ej. quedó pausado y nunca más pagó). Solo se permite
// si ya está pausado — de última salvaguarda contra un borrado accidental de un comercio
// activo; para eso primero hay que pausarlo. No hay FKs reales en la base (por diseño, cada
// tabla solo guarda el ComercioId suelto), así que hay que borrar a mano todo lo que cuelga
// de este comercio para no dejar filas huérfanas.
app.MapDelete("/api/comercios/{id:int}", async (AppDbContext context, int id) =>
{
    var comercio = await context.Comercios.FindAsync(id);
    if (comercio is null) return Results.NotFound();
    if (comercio.Activo)
        return Results.Conflict(new { mensaje = "Solo se puede eliminar un comercio que ya esté pausado." });

    var comprobantesDelComercio = await context.Turnos
        .Where(t => t.ComercioId == id && t.ComprobanteArchivo != null)
        .Select(t => t.ComprobanteArchivo!)
        .ToListAsync();

    var fotosDeProfesionales = await context.Profesionales
        .Where(p => p.ComercioId == id && p.FotoUrl != null)
        .Select(p => p.FotoUrl!)
        .ToListAsync();

    context.Turnos.RemoveRange(context.Turnos.Where(t => t.ComercioId == id));
    context.Servicios.RemoveRange(context.Servicios.Where(s => s.ComercioId == id));
    context.Horarios.RemoveRange(context.Horarios.Where(h => h.ComercioId == id));
    context.Profesionales.RemoveRange(context.Profesionales.Where(p => p.ComercioId == id));
    context.Sucursales.RemoveRange(context.Sucursales.Where(s => s.ComercioId == id));
    context.WhatsAppConfigs.RemoveRange(context.WhatsAppConfigs.Where(w => w.ComercioId == id));
    context.PasswordResetTokens.RemoveRange(context.PasswordResetTokens.Where(t => t.ComercioId == id));
    context.Pagos.RemoveRange(context.Pagos.Where(p => p.ComercioId == id));
    context.Comercios.Remove(comercio);

    await context.SaveChangesAsync();

    // Recién con la base ya actualizada se borran los archivos (si fallara el borrado de la
    // base, los comprobantes seguirían haciendo falta).
    foreach (var nombreArchivo in comprobantesDelComercio)
    {
        var ruta = Path.Combine(rutaComprobantes, Path.GetFileName(nombreArchivo));
        if (File.Exists(ruta)) File.Delete(ruta);
    }
    foreach (var fotoUrl in fotosDeProfesionales) BorrarFotoProfesional(fotoUrl);

    return Results.NoContent();
}).RequireAuthorization("SuperAdmin");

app.MapGet("/api/admin/metricas", async (AppDbContext context) =>
{
    var totalComercios = await context.Comercios.CountAsync();
    var comerciosActivos = await context.Comercios.CountAsync(c => c.Activo);

    var ahora = AhoraArgentina();
    var inicioMes = new DateTime(ahora.Year, ahora.Month, 1);
    var inicioMesSiguiente = inicioMes.AddMonths(1);
    var turnosDelMes = await context.Turnos.CountAsync(t =>
        t.EstadoReserva != 3 // cualquier estado excepto Cancelado
        && t.FechaHoraInicio >= inicioMes && t.FechaHoraInicio < inicioMesSiguiente);

    return Results.Ok(new MetricasDto(totalComercios, comerciosActivos, totalComercios - comerciosActivos, turnosDelMes));
}).RequireAuthorization("SuperAdmin");

// Dashboard de estadísticas del Super Admin: mismo espíritu que /api/admin/metricas pero con
// el desglose completo que necesita la sección nueva del panel (por estado, por plan,
// ingreso mensual estimado y tendencia de altas).
app.MapGet("/api/admin/dashboard", async (AppDbContext context) =>
{
    var comercios = await context.Comercios.ToListAsync();

    var activos = comercios.Count(c => c.Activo);
    // "Pendiente de activación" = nunca se activó (FechaActivacion nula); si ya se activó
    // alguna vez y ahora está pausado, es un comercio inactivo común, no un alta pendiente.
    var pendientes = comercios.Count(c => !c.Activo && c.FechaActivacion is null);
    var inactivos = comercios.Count(c => !c.Activo) - pendientes;

    var porPlan = comercios
        .GroupBy(c => c.PlanActual)
        .Select(g => new PlanCantidadDto(g.Key, g.Count()))
        .OrderByDescending(x => x.Cantidad)
        .ToList();

    // MontoMensualAcordado ya representa el equivalente mensual sin importar el ciclo de
    // facturación (así se carga en el panel), así que se suma directo, sin prorratear.
    var ingresoMensualEstimado = comercios.Where(c => c.Activo).Sum(c => c.MontoMensualAcordado ?? 0);

    var ahora = AhoraArgentina();
    var inicioMes = new DateTime(ahora.Year, ahora.Month, 1);
    var turnosDelMes = await context.Turnos.CountAsync(t =>
        t.EstadoReserva != 3 && t.FechaHoraInicio >= inicioMes && t.FechaHoraInicio < inicioMes.AddMonths(1));

    // Altas de los últimos 6 meses (incluye el actual), un balde por mes en orden cronológico.
    var altasPorMes = new List<AltaMesDto>();
    for (var i = 5; i >= 0; i--)
    {
        var mesInicio = inicioMes.AddMonths(-i);
        var mesFin = mesInicio.AddMonths(1);
        var cantidad = comercios.Count(c => c.FechaAlta >= mesInicio && c.FechaAlta < mesFin);
        altasPorMes.Add(new AltaMesDto(mesInicio.Year, mesInicio.Month, cantidad));
    }

    return Results.Ok(new DashboardDto(
        comercios.Count, activos, inactivos, pendientes,
        porPlan, ingresoMensualEstimado, turnosDelMes, altasPorMes));
}).RequireAuthorization("SuperAdmin");

// Datos extra por comercio para la tabla del Super Admin (orden, filtros y alertas): fecha de
// alta, si nunca se activó (pendiente) y turnos del mes actual y del anterior. Va aparte de
// GET /api/comercios para no tocar ComercioDto, que también devuelven las acciones (PATCH).
// "Turno" = cualquier estado excepto Cancelado, igual que turnosDelMes del dashboard.
app.MapGet("/api/admin/comercios/estadisticas", async (AppDbContext context) =>
{
    var ahora = AhoraArgentina();
    var inicioMes = new DateTime(ahora.Year, ahora.Month, 1);
    var inicioMesAnterior = inicioMes.AddMonths(-1);
    var inicioMesSiguiente = inicioMes.AddMonths(1);

    var turnosPorComercio = await context.Turnos
        .Where(t => t.EstadoReserva != 3 && t.FechaHoraInicio >= inicioMesAnterior && t.FechaHoraInicio < inicioMesSiguiente)
        .GroupBy(t => t.ComercioId)
        .Select(g => new
        {
            ComercioId = g.Key,
            DelMes = g.Count(t => t.FechaHoraInicio >= inicioMes),
            DelMesAnterior = g.Count(t => t.FechaHoraInicio < inicioMes)
        })
        .ToDictionaryAsync(x => x.ComercioId);

    var comercios = await context.Comercios
        .Select(c => new { c.Id, c.FechaAlta, c.FechaActivacion, c.Activo, c.UltimoAcceso })
        .ToListAsync();

    var resultado = comercios.Select(c =>
    {
        turnosPorComercio.TryGetValue(c.Id, out var t);
        return new ComercioEstadisticaDto(
            c.Id, c.FechaAlta, !c.Activo && c.FechaActivacion is null,
            t?.DelMes ?? 0, t?.DelMesAnterior ?? 0,
            c.UltimoAcceso is null ? null : DateTime.SpecifyKind(c.UltimoAcceso.Value, DateTimeKind.Utc));
    }).ToList();

    return Results.Ok(resultado);
}).RequireAuthorization("SuperAdmin");

// Resumen del Super Admin (Etapa B): MRR con historial, turnos y facturación de los
// locales del mes vs. el anterior, turnos por día de los últimos 30 días, altas y bajas por
// mes y comercios por rubro. Los conteos que dependen de comercios (activos, pagando,
// alertas, salud) se calculan en el frontend con /api/comercios y /estadisticas.
app.MapGet("/api/admin/resumen", async (AppDbContext context) =>
{
    var ahora = AhoraArgentina();
    var hoy = DateOnly.FromDateTime(ahora);
    var inicioMes = new DateTime(ahora.Year, ahora.Month, 1);
    var inicioMesAnterior = inicioMes.AddMonths(-1);

    var comercios = await context.Comercios
        .Select(c => new { c.Activo, c.MontoMensualAcordado, c.FechaAlta, c.FechaBaja, c.TipoPlantilla })
        .ToListAsync();

    // MRR: mismo criterio que el ingreso mensual estimado del dashboard. Se guarda la foto
    // del mes en curso para poder comparar el mes que viene.
    var mrrActual = comercios.Where(c => c.Activo).Sum(c => c.MontoMensualAcordado ?? 0);
    var snapshot = await context.MrrSnapshots.FirstOrDefaultAsync(m => m.Anio == ahora.Year && m.Mes == ahora.Month);
    if (snapshot is null)
    {
        context.MrrSnapshots.Add(new MrrSnapshot { Anio = ahora.Year, Mes = ahora.Month, Monto = mrrActual });
    }
    else
    {
        snapshot.Monto = mrrActual;
        snapshot.FechaActualizacion = DateTime.UtcNow;
    }
    try
    {
        await context.SaveChangesAsync();
    }
    catch (DbUpdateException)
    {
        // Dos consultas simultáneas pueden intentar crear la foto del mes a la vez; el índice
        // único deja pasar una sola. La foto es de mejor esfuerzo: el resumen sigue igual.
        context.ChangeTracker.Clear();
    }

    var snapshots = await context.MrrSnapshots
        .OrderByDescending(m => m.Anio).ThenByDescending(m => m.Mes)
        .Take(6)
        .ToListAsync();
    var mrrMesAnterior = snapshots.FirstOrDefault(m => m.Anio == inicioMesAnterior.Year && m.Mes == inicioMesAnterior.Month)?.Monto;
    var mrrHistorico = snapshots.OrderBy(m => m.Anio).ThenBy(m => m.Mes).Select(m => new MrrMesDto(m.Anio, m.Mes, m.Monto)).ToList();

    // Turnos (sin cancelados) del mes y del anterior, toda la plataforma.
    var turnosDelMes = await context.Turnos.CountAsync(t => t.EstadoReserva != 3 && t.FechaHoraInicio >= inicioMes && t.FechaHoraInicio < inicioMes.AddMonths(1));
    var turnosMesAnterior = await context.Turnos.CountAsync(t => t.EstadoReserva != 3 && t.FechaHoraInicio >= inicioMesAnterior && t.FechaHoraInicio < inicioMes);

    // Facturación de los locales: misma lógica que Ganancias (turnos confirmados, monto
    // cobrado), solo los ya realizados (inicio anterior a ahora).
    var facturacionMes = await context.Turnos
        .Where(t => t.EstadoReserva == 2 && t.FechaHoraInicio >= inicioMes && t.FechaHoraInicio <= ahora)
        .SumAsync(t => t.MontoCobrado ?? 0);
    var facturacionMesAnterior = await context.Turnos
        .Where(t => t.EstadoReserva == 2 && t.FechaHoraInicio >= inicioMesAnterior && t.FechaHoraInicio < inicioMes)
        .SumAsync(t => t.MontoCobrado ?? 0);

    // Turnos por día de los últimos 30 días (incluye hoy), toda la plataforma.
    var desde30 = hoy.AddDays(-29);
    var inicio30 = desde30.ToDateTime(TimeOnly.MinValue);
    var fin30 = hoy.ToDateTime(TimeOnly.MaxValue);
    var fechasTurnos = await context.Turnos
        .Where(t => t.EstadoReserva != 3 && t.FechaHoraInicio >= inicio30 && t.FechaHoraInicio <= fin30)
        .Select(t => t.FechaHoraInicio)
        .ToListAsync();
    var porFecha = fechasTurnos.GroupBy(f => DateOnly.FromDateTime(f)).ToDictionary(g => g.Key, g => g.Count());
    var turnosPorDia = new List<TurnosDiaDto>();
    for (var d = desde30; d <= hoy; d = d.AddDays(1))
        turnosPorDia.Add(new TurnosDiaDto(d, porFecha.GetValueOrDefault(d)));

    // Altas y bajas de los últimos 6 meses. Baja = comercio hoy pausado cuya última pausa
    // cayó en ese mes (los eliminados ya no están en la base).
    var altasYBajas = new List<AltasBajasMesDto>();
    for (var i = 5; i >= 0; i--)
    {
        var mesInicio = inicioMes.AddMonths(-i);
        var mesFin = mesInicio.AddMonths(1);
        altasYBajas.Add(new AltasBajasMesDto(
            mesInicio.Year, mesInicio.Month,
            comercios.Count(c => c.FechaAlta >= mesInicio && c.FechaAlta < mesFin),
            comercios.Count(c => !c.Activo && c.FechaBaja >= mesInicio && c.FechaBaja < mesFin)));
    }

    var porRubro = comercios
        .GroupBy(c => string.IsNullOrWhiteSpace(c.TipoPlantilla) ? "Sin rubro" : c.TipoPlantilla)
        .Select(g => new RubroCantidadDto(g.Key, g.Count()))
        .OrderByDescending(x => x.Cantidad)
        .ToList();

    return Results.Ok(new SuperAdminResumenDto(
        mrrActual, mrrMesAnterior, mrrHistorico,
        turnosDelMes, turnosMesAnterior,
        facturacionMes, facturacionMesAnterior,
        turnosPorDia, altasYBajas, porRubro));
}).RequireAuthorization("SuperAdmin");

// Ficha de detalle de un comercio puntual para el Super Admin (distinto de /historial y
// /ganancias, que son AdminCliente-only y solo dejan ver el propio comercio logueado).
app.MapGet("/api/admin/comercios/{id:int}/detalle", async (AppDbContext context, int id) =>
{
    var comercio = await context.Comercios.FindAsync(id);
    if (comercio is null) return Results.NotFound();

    var sucursales = await context.Sucursales.Where(s => s.ComercioId == id).OrderBy(s => s.Nombre).ToListAsync();
    var profesionales = await context.Profesionales.Where(p => p.ComercioId == id).OrderBy(p => p.Nombre).ToListAsync();

    var ahora = AhoraArgentina();
    var inicioMes = new DateTime(ahora.Year, ahora.Month, 1);

    var turnosHistoricosTotal = await context.Turnos.CountAsync(t => t.ComercioId == id && t.EstadoReserva != 3);
    var turnosDelMes = await context.Turnos.CountAsync(t =>
        t.ComercioId == id && t.EstadoReserva != 3 && t.FechaHoraInicio >= inicioMes && t.FechaHoraInicio < inicioMes.AddMonths(1));

    // La facturación histórica es parte de Ganancias, que es exclusivo del plan Premium —
    // mismo criterio que la pestaña Ganancias del panel del propio comercio.
    decimal? facturacionHistorica = null;
    if (comercio.PlanActual == "Premium")
    {
        facturacionHistorica = await context.Turnos
            .Where(t => t.ComercioId == id && t.EstadoReserva == 2)
            .SumAsync(t => (decimal?)t.MontoCobrado) ?? 0;
    }

    // Turnos por semana del último mes (4 baldes de 7 días terminando hoy), para el gráfico.
    var turnosPorSemana = new List<TurnosPorSemanaDto>();
    var finBalde = ahora.Date.AddDays(1); // hasta el final del día de hoy
    for (var i = 3; i >= 0; i--)
    {
        var semanaFin = finBalde.AddDays(-7 * i);
        var semanaInicio = semanaFin.AddDays(-7);
        var cantidad = await context.Turnos.CountAsync(t =>
            t.ComercioId == id && t.EstadoReserva != 3 && t.FechaHoraInicio >= semanaInicio && t.FechaHoraInicio < semanaFin);
        turnosPorSemana.Add(new TurnosPorSemanaDto(DateOnly.FromDateTime(semanaInicio), DateOnly.FromDateTime(semanaFin.AddDays(-1)), cantidad));
    }

    // --- Datos del panel lateral del Super Admin (Etapa C) ---
    var cantidadServicios = await context.Servicios.CountAsync(s => s.ComercioId == id);

    // Lo que facturó el local en el mes: misma lógica que Ganancias (confirmados, monto
    // cobrado, ya realizados), pero para cualquier plan: es una métrica para el dueño de la
    // plataforma, no la pestaña Ganancias del comercio.
    var facturacionDelMes = await context.Turnos
        .Where(t => t.ComercioId == id && t.EstadoReserva == 2 && t.FechaHoraInicio >= inicioMes && t.FechaHoraInicio <= ahora)
        .SumAsync(t => t.MontoCobrado ?? 0);

    // Turnos (sin cancelados) por día de los últimos 30 días, incluido hoy.
    var hoy = DateOnly.FromDateTime(ahora);
    var desde30 = hoy.AddDays(-29);
    var inicio30 = desde30.ToDateTime(TimeOnly.MinValue);
    var fin30 = hoy.ToDateTime(TimeOnly.MaxValue);
    var fechas30 = await context.Turnos
        .Where(t => t.ComercioId == id && t.EstadoReserva != 3 && t.FechaHoraInicio >= inicio30 && t.FechaHoraInicio <= fin30)
        .Select(t => t.FechaHoraInicio)
        .ToListAsync();
    var porFecha30 = fechas30.GroupBy(f => DateOnly.FromDateTime(f)).ToDictionary(g => g.Key, g => g.Count());
    var turnosPorDia = new List<TurnosDiaDto>();
    for (var d = desde30; d <= hoy; d = d.AddDays(1))
        turnosPorDia.Add(new TurnosDiaDto(d, porFecha30.GetValueOrDefault(d)));

    return Results.Ok(new ComercioDetalleDto(
        comercio.Id, comercio.Nombre, comercio.AliasUrl, comercio.TipoPlantilla, comercio.PlanActual, comercio.CicloFacturacion,
        comercio.MontoMensualAcordado, comercio.FechaAlta, comercio.FechaProximoPago, comercio.Activo,
        profesionales.Count, sucursales.Count,
        turnosHistoricosTotal, turnosDelMes, facturacionHistorica,
        sucursales, profesionales, turnosPorSemana,
        comercio.Email, comercio.TelefonoNotificaciones,
        string.IsNullOrWhiteSpace(comercio.TelefonoNotificaciones) ? null : FormatearNumeroWhatsApp(comercio.TelefonoNotificaciones),
        comercio.UltimoAcceso is null ? null : DateTime.SpecifyKind(comercio.UltimoAcceso.Value, DateTimeKind.Utc),
        comercio.FechaActivacion, comercio.FechaBaja,
        cantidadServicios, facturacionDelMes, turnosPorDia, TopeTurnosMensualesGratuito,
        comercio.AddonCobrosOnline, comercio.MercadoPagoAccessToken is not null));
}).RequireAuthorization("SuperAdmin");

app.MapGet("/api/comercios/alias/{alias}", async (AppDbContext context, string alias) =>
{
    var comercio = await context.Comercios.FirstOrDefaultAsync(c => c.AliasUrl == alias);
    if (comercio is null) return Results.NotFound();
    if (!comercio.Activo) return ResultadoComercioInactivo();

    // El bot de WhatsApp es exclusivo de Premium; si el comercio bajó de plan pero había
    // dejado el toggle activado, igual no cuenta como activo (mismo criterio que los
    // endpoints de whatsapp-config, que lo apagan implícitamente fuera de Premium).
    var whatsAppActivo = false;
    if (comercio.PlanActual == "Premium")
    {
        var whatsAppConfig = await context.WhatsAppConfigs.FirstOrDefaultAsync(w => w.ComercioId == comercio.Id);
        whatsAppActivo = whatsAppConfig?.Activado ?? false;
    }

    return Results.Ok(new ComercioPublicoDto(
        comercio.Id, comercio.Nombre, comercio.AliasUrl, comercio.TipoPlantilla, comercio.TelefonoNotificaciones, comercio.LogoUrl, whatsAppActivo,
        comercio.DatosBancarios, CobraSeniaPorMercadoPago(comercio), comercio.SeñaPorTransferencia || !CobraSeniaPorMercadoPago(comercio),
        CobraSeniaPorMercadoPago(comercio) && comercio.CobroMercadoPagoTotal));
});

// El cambio de plan ya no es autogestionado: el dueño lo pide por WhatsApp desde "Mi plan"
// y lo aplica el Super Admin (PATCH /plan y /ciclo-facturacion) una vez coordinado el pago.
// Antes existía PATCH /mi-plan, que lo cambiaba al instante sin pagar (y de paso corría el
// vencimiento un ciclo entero cada vez que se guardaba).

// El dueño edita los datos de su propio negocio (no el alias del link público: cambiarlo
// rompería los links que ya compartió).
app.MapPatch("/api/comercios/{comercioId:int}/perfil", async (AppDbContext context, int comercioId, PerfilRequest req, ClaimsPrincipal user) =>
{
    if (ComercioIdDelToken(user) != comercioId) return Results.Forbid();

    if (string.IsNullOrWhiteSpace(req.Nombre))
        return Results.BadRequest(new { mensaje = "El nombre del negocio no puede estar vacío." });

    var comercio = await context.Comercios.FindAsync(comercioId);
    if (comercio is null) return Results.NotFound();

    comercio.Nombre = req.Nombre.Trim();
    comercio.TelefonoNotificaciones = req.TelefonoNotificaciones.Trim();
    comercio.DatosBancarios = req.DatosBancarios.Trim();
    await context.SaveChangesAsync();

    return Results.Ok(new PerfilDto(comercio.Nombre, comercio.TelefonoNotificaciones, comercio.DatosBancarios, comercio.LogoUrl));
}).RequireAuthorization("AdminCliente");

// Cambiar contraseña estando logueado (distinto del flujo de "olvidé mi contraseña").
app.MapPost("/api/comercios/{comercioId:int}/cambiar-password", async (AppDbContext context, int comercioId, CambiarPasswordRequest req, ClaimsPrincipal user) =>
{
    if (ComercioIdDelToken(user) != comercioId) return Results.Forbid();

    var comercio = await context.Comercios.FindAsync(comercioId);
    if (comercio is null) return Results.NotFound();

    if (!VerifyPassword(req.PasswordActual, comercio.PasswordHash))
        return Results.BadRequest(new { mensaje = "La contraseña actual no es correcta." });

    if (req.PasswordNueva.Length < LargoMinimoPassword)
        return Results.BadRequest(new { mensaje = $"La contraseña nueva tiene que tener al menos {LargoMinimoPassword} caracteres." });

    comercio.PasswordHash = HashPassword(req.PasswordNueva);
    await context.SaveChangesAsync();

    return Results.Ok(new { mensaje = "Contraseña actualizada." });
}).RequireAuthorization("AdminCliente").RequireRateLimiting("login");

// Logo del comercio (opcional). Si no carga uno, la página pública sigue mostrando iniciales.
app.MapPost("/api/comercios/{comercioId:int}/logo", async (AppDbContext context, IWebHostEnvironment env, int comercioId, IFormFile archivo, ClaimsPrincipal user) =>
{
    if (ComercioIdDelToken(user) != comercioId) return Results.Forbid();

    var comercio = await context.Comercios.FindAsync(comercioId);
    if (comercio is null) return Results.NotFound();

    var extensionesPermitidas = new[] { ".png", ".jpg", ".jpeg", ".webp" };
    var extension = Path.GetExtension(archivo.FileName).ToLowerInvariant();
    if (!extensionesPermitidas.Contains(extension))
        return Results.BadRequest(new { mensaje = "El logo tiene que ser una imagen (PNG, JPG o WEBP)." });

    const long tamañoMaximo = 2 * 1024 * 1024; // 2MB
    if (archivo.Length > tamañoMaximo)
        return Results.BadRequest(new { mensaje = "El logo no puede pesar más de 2MB." });

    var carpeta = rutaLogos;
    Directory.CreateDirectory(carpeta);

    // Si ya tenía un logo con otra extensión, lo borramos para no dejar basura.
    foreach (var ext in extensionesPermitidas)
    {
        var previo = Path.Combine(carpeta, $"{comercioId}{ext}");
        if (File.Exists(previo)) File.Delete(previo);
    }

    var rutaArchivo = Path.Combine(carpeta, $"{comercioId}{extension}");
    await using (var stream = File.Create(rutaArchivo))
    {
        await archivo.CopyToAsync(stream);
    }

    comercio.LogoUrl = $"/uploads/logos/{comercioId}{extension}";
    await context.SaveChangesAsync();

    return Results.Ok(new { logoUrl = comercio.LogoUrl });
})
// Los endpoints que reciben IFormFile piden por default el middleware de antiforgery (pensado
// para forms con cookies). Esta API es 100% JWT bearer sin cookies, así que no aplica — sin
// esto, cualquier subida de archivo tira 500 porque el middleware nunca se registró.
.DisableAntiforgery()
.RequireAuthorization("AdminCliente");

// ==========================================
// ENDPOINTS PARA SERVICIOS
// ==========================================
app.MapPost("/api/servicios", async (AppDbContext context, Servicio servicio, ClaimsPrincipal user) =>
{
    if (ComercioIdDelToken(user) != servicio.ComercioId) return Results.Forbid();

    if (ErrorServicio(servicio.Nombre, servicio.DuracionMinutos, servicio.Precio, servicio.MontoSeña) is { } error)
        return Results.BadRequest(new { mensaje = error });

    servicio.Nombre = servicio.Nombre.Trim();
    context.Servicios.Add(servicio);
    await context.SaveChangesAsync();
    return Results.Created($"/api/servicios/{servicio.Id}", servicio);
}).RequireAuthorization("AdminCliente");

// comercioId es obligatorio: sin él, esto listaba los servicios de todos los comercios.
app.MapGet("/api/servicios", async (AppDbContext context, int comercioId) =>
{
    var servicios = await context.Servicios.Where(s => s.Activo && s.ComercioId == comercioId).ToListAsync();
    return Results.Ok(servicios);
});

app.MapPut("/api/servicios/{id:int}", async (AppDbContext context, int id, EditarServicioRequest req, ClaimsPrincipal user) =>
{
    var servicio = await context.Servicios.FindAsync(id);
    if (servicio is null) return Results.NotFound();
    if (ComercioIdDelToken(user) != servicio.ComercioId) return Results.Forbid();

    if (ErrorServicio(req.Nombre, req.DuracionMinutos, req.Precio, req.MontoSeña) is { } error)
        return Results.BadRequest(new { mensaje = error });

    servicio.Nombre = req.Nombre.Trim();
    servicio.DuracionMinutos = req.DuracionMinutos;
    servicio.Precio = req.Precio;
    servicio.MontoSeña = req.MontoSeña;
    await context.SaveChangesAsync();

    return Results.Ok(servicio);
}).RequireAuthorization("AdminCliente");

app.MapDelete("/api/servicios/{id:int}", async (AppDbContext context, int id, ClaimsPrincipal user) =>
{
    var servicio = await context.Servicios.FindAsync(id);
    if (servicio is null) return Results.NotFound();
    if (ComercioIdDelToken(user) != servicio.ComercioId) return Results.Forbid();

    servicio.Activo = false;
    await context.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization("AdminCliente");

// ==========================================
// ENDPOINTS PARA PROFESIONALES (Básico: 1-2, Premium: ilimitados)
// ==========================================
app.MapPost("/api/comercios/{comercioId:int}/profesionales", async (AppDbContext context, int comercioId, ProfesionalRequest req, ClaimsPrincipal user) =>
{
    if (ComercioIdDelToken(user) != comercioId) return Results.Forbid();

    var comercio = await context.Comercios.FindAsync(comercioId);
    if (comercio is null) return Results.NotFound();

    var tope = TopeProfesionales(comercio.PlanActual);
    var cantidadActual = await context.Profesionales.CountAsync(p => p.ComercioId == comercioId);
    if (cantidadActual >= tope)
        return Results.Conflict(new { mensaje = $"Tu plan {comercio.PlanActual} permite hasta {tope} profesional(es). Actualizá de plan para agregar más." });

    if (string.IsNullOrWhiteSpace(req.Nombre))
        return Results.BadRequest(new { mensaje = "El nombre no puede estar vacío." });

    var especialidad = NormalizarEspecialidad(req.Especialidad);
    if (especialidad is { Length: > 30 })
        return Results.BadRequest(new { mensaje = "La especialidad puede tener hasta 30 caracteres." });

    if (req.SucursalId is not null && !await context.Sucursales.AnyAsync(s => s.Id == req.SucursalId && s.ComercioId == comercioId))
        return Results.NotFound(new { mensaje = "La sucursal no pertenece a este comercio." });

    var profesional = new Profesional { ComercioId = comercioId, Nombre = req.Nombre.Trim(), SucursalId = req.SucursalId, Especialidad = especialidad };
    context.Profesionales.Add(profesional);
    await context.SaveChangesAsync();
    return Results.Created($"/api/profesionales/{profesional.Id}", profesional);
}).RequireAuthorization("AdminCliente");

// Público: la página de reserva necesita listarlos para que el cliente elija profesional.
// Si el comercio tiene sucursales, se puede filtrar para mostrar solo los de la sucursal elegida.
app.MapGet("/api/comercios/{comercioId:int}/profesionales", async (AppDbContext context, int comercioId, int? sucursalId) =>
{
    var query = context.Profesionales.Where(p => p.ComercioId == comercioId);
    if (sucursalId is not null) query = query.Where(p => p.SucursalId == sucursalId);

    var profesionales = await query.OrderBy(p => p.Nombre).ToListAsync();
    return Results.Ok(profesionales);
});

app.MapPut("/api/profesionales/{id:int}", async (AppDbContext context, int id, ProfesionalRequest req, ClaimsPrincipal user) =>
{
    if (string.IsNullOrWhiteSpace(req.Nombre))
        return Results.BadRequest(new { mensaje = "El nombre no puede estar vacío." });

    var especialidad = NormalizarEspecialidad(req.Especialidad);
    if (especialidad is { Length: > 30 })
        return Results.BadRequest(new { mensaje = "La especialidad puede tener hasta 30 caracteres." });

    var profesional = await context.Profesionales.FindAsync(id);
    if (profesional is null) return Results.NotFound();
    if (ComercioIdDelToken(user) != profesional.ComercioId) return Results.Forbid();

    if (req.SucursalId is not null && !await context.Sucursales.AnyAsync(s => s.Id == req.SucursalId && s.ComercioId == profesional.ComercioId))
        return Results.NotFound(new { mensaje = "La sucursal no pertenece a este comercio." });

    profesional.Nombre = req.Nombre.Trim();
    profesional.SucursalId = req.SucursalId;
    profesional.Especialidad = especialidad;
    await context.SaveChangesAsync();

    return Results.Ok(profesional);
}).RequireAuthorization("AdminCliente");

app.MapDelete("/api/profesionales/{id:int}", async (AppDbContext context, int id, ClaimsPrincipal user) =>
{
    var profesional = await context.Profesionales.FindAsync(id);
    if (profesional is null) return Results.NotFound();
    if (ComercioIdDelToken(user) != profesional.ComercioId) return Results.Forbid();

    // Los horarios del profesional quedan huérfanos y sin usar; los turnos ya reservados
    // se conservan (con ProfesionalId apuntando a un id inexistente) para no perder el historial.
    var horariosDelProfesional = await context.Horarios.Where(h => h.ProfesionalId == id).ToListAsync();
    context.Horarios.RemoveRange(horariosDelProfesional);

    context.Profesionales.Remove(profesional);
    await context.SaveChangesAsync();
    BorrarFotoProfesional(profesional.FotoUrl);
    return Results.NoContent();
}).RequireAuthorization("AdminCliente");

// Foto de perfil del profesional (opcional). Si no tiene, se muestran sus iniciales.
app.MapPost("/api/profesionales/{id:int}/foto", async (AppDbContext context, int id, IFormFile archivo, ClaimsPrincipal user) =>
{
    var profesional = await context.Profesionales.FindAsync(id);
    if (profesional is null) return Results.NotFound();
    if (ComercioIdDelToken(user) != profesional.ComercioId) return Results.Forbid();

    const long tamañoMaximo = 2 * 1024 * 1024; // 2MB
    if (archivo.Length == 0 || archivo.Length > tamañoMaximo)
        return Results.BadRequest(new { mensaje = "La foto no puede pesar más de 2MB." });

    // Se valida por el contenido, no por la extensión que manda el navegador: este archivo
    // queda servido públicamente.
    byte[] contenido;
    using (var memoria = new MemoryStream())
    {
        await archivo.CopyToAsync(memoria);
        contenido = memoria.ToArray();
    }
    var extension = ExtensionDeImagen(contenido);
    if (extension is null)
        return Results.BadRequest(new { mensaje = "La foto tiene que ser una imagen (PNG, JPG o WEBP)." });

    var nombreArchivo = $"{id}-{Guid.NewGuid():N}{extension}";
    await File.WriteAllBytesAsync(Path.Combine(rutaFotosProfesionales, nombreArchivo), contenido);

    var fotoAnterior = profesional.FotoUrl;
    profesional.FotoUrl = $"/uploads/profesionales/{nombreArchivo}";
    await context.SaveChangesAsync();
    BorrarFotoProfesional(fotoAnterior);

    return Results.Ok(new { fotoUrl = profesional.FotoUrl });
})
// Igual que el logo: sin esto, la subida de archivos tira 500 (la API no usa cookies).
.DisableAntiforgery()
.RequireAuthorization("AdminCliente");

app.MapDelete("/api/profesionales/{id:int}/foto", async (AppDbContext context, int id, ClaimsPrincipal user) =>
{
    var profesional = await context.Profesionales.FindAsync(id);
    if (profesional is null) return Results.NotFound();
    if (ComercioIdDelToken(user) != profesional.ComercioId) return Results.Forbid();

    var fotoAnterior = profesional.FotoUrl;
    profesional.FotoUrl = null;
    await context.SaveChangesAsync();
    BorrarFotoProfesional(fotoAnterior);

    return Results.NoContent();
}).RequireAuthorization("AdminCliente");

// ==========================================
// ENDPOINTS PARA SUCURSALES (disponible en todos los planes)
// ==========================================
app.MapPost("/api/comercios/{comercioId:int}/sucursales", async (AppDbContext context, int comercioId, SucursalRequest req, ClaimsPrincipal user) =>
{
    if (ComercioIdDelToken(user) != comercioId) return Results.Forbid();
    if (string.IsNullOrWhiteSpace(req.Nombre))
        return Results.BadRequest(new { mensaje = "El nombre no puede estar vacío." });

    var comercio = await context.Comercios.FindAsync(comercioId);
    if (comercio is null) return Results.NotFound();

    // El tope aplica para altas nuevas; si un comercio ya tenía más sucursales cargadas de
    // antes de esta regla (no debería, pero por las dudas), esas no se tocan ni se borran.
    var tope = TopeSucursales(comercio.PlanActual);
    var cantidadActual = await context.Sucursales.CountAsync(s => s.ComercioId == comercioId);
    if (cantidadActual >= tope)
        return Results.Conflict(new { mensaje = $"Tu plan {comercio.PlanActual} permite hasta {tope} sucursal(es). Actualizá a Premium para agregar más." });

    var sucursal = new Sucursal
    {
        ComercioId = comercioId,
        Nombre = req.Nombre.Trim(),
        Direccion = req.Direccion?.Trim() ?? string.Empty,
        Telefono = req.Telefono,
        Activa = req.Activa
    };
    context.Sucursales.Add(sucursal);
    await context.SaveChangesAsync();
    return Results.Created($"/api/sucursales/{sucursal.Id}", sucursal);
}).RequireAuthorization("AdminCliente");

// Público: la página de reserva necesita listarlas para que el cliente elija sucursal
// cuando el comercio tiene más de una activa.
app.MapGet("/api/comercios/{comercioId:int}/sucursales", async (AppDbContext context, int comercioId) =>
{
    var sucursales = await context.Sucursales
        .Where(s => s.ComercioId == comercioId)
        .OrderBy(s => s.Nombre)
        .ToListAsync();
    return Results.Ok(sucursales);
});

app.MapPut("/api/sucursales/{id:int}", async (AppDbContext context, int id, SucursalRequest req, ClaimsPrincipal user) =>
{
    if (string.IsNullOrWhiteSpace(req.Nombre))
        return Results.BadRequest(new { mensaje = "El nombre no puede estar vacío." });

    var sucursal = await context.Sucursales.FindAsync(id);
    if (sucursal is null) return Results.NotFound();
    if (ComercioIdDelToken(user) != sucursal.ComercioId) return Results.Forbid();

    sucursal.Nombre = req.Nombre.Trim();
    sucursal.Direccion = req.Direccion?.Trim() ?? string.Empty;
    sucursal.Telefono = req.Telefono;
    sucursal.Activa = req.Activa;
    await context.SaveChangesAsync();

    return Results.Ok(sucursal);
}).RequireAuthorization("AdminCliente");

app.MapDelete("/api/sucursales/{id:int}", async (AppDbContext context, int id, ClaimsPrincipal user) =>
{
    var sucursal = await context.Sucursales.FindAsync(id);
    if (sucursal is null) return Results.NotFound();
    if (ComercioIdDelToken(user) != sucursal.ComercioId) return Results.Forbid();

    // Los profesionales de esta sucursal quedan sin sucursal asignada (no se borran ni se
    // tocan sus turnos/horarios), como si el comercio recién estuviera armando sus sucursales.
    var profesionalesDeLaSucursal = await context.Profesionales.Where(p => p.SucursalId == id).ToListAsync();
    foreach (var p in profesionalesDeLaSucursal) p.SucursalId = null;

    context.Sucursales.Remove(sucursal);
    await context.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization("AdminCliente");

// ==========================================
// ENDPOINTS PARA HORARIOS (disponibilidad semanal)
// ==========================================
app.MapGet("/api/comercios/{comercioId:int}/horarios", async (AppDbContext context, int comercioId, int? profesionalId, ClaimsPrincipal user) =>
{
    if (ComercioIdDelToken(user) != comercioId) return Results.Forbid();

    var horarios = await context.Horarios
        .Where(h => h.ComercioId == comercioId && h.ProfesionalId == profesionalId)
        .OrderBy(h => h.DiaSemana).ThenBy(h => h.HoraInicio)
        .ToListAsync();
    return Results.Ok(horarios);
}).RequireAuthorization("AdminCliente");

app.MapPost("/api/comercios/{comercioId:int}/horarios", async (AppDbContext context, int comercioId, HorarioRequest req, ClaimsPrincipal user) =>
{
    if (ComercioIdDelToken(user) != comercioId) return Results.Forbid();

    if (ErrorHorario(req) is { } error)
        return Results.BadRequest(new { mensaje = error });

    if (req.ProfesionalId is not null && !await context.Profesionales.AnyAsync(p => p.Id == req.ProfesionalId && p.ComercioId == comercioId))
        return Results.NotFound(new { mensaje = "El profesional no pertenece a este comercio." });

    var horario = new Horario
    {
        ComercioId = comercioId,
        ProfesionalId = req.ProfesionalId,
        DiaSemana = req.DiaSemana,
        HoraInicio = req.HoraInicio,
        HoraFin = req.HoraFin
    };
    context.Horarios.Add(horario);
    await context.SaveChangesAsync();
    return Results.Created($"/api/horarios/{horario.Id}", horario);
}).RequireAuthorization("AdminCliente");

app.MapPut("/api/horarios/{id:int}", async (AppDbContext context, int id, HorarioRequest req, ClaimsPrincipal user) =>
{
    var horario = await context.Horarios.FindAsync(id);
    if (horario is null) return Results.NotFound();
    if (ComercioIdDelToken(user) != horario.ComercioId) return Results.Forbid();

    if (ErrorHorario(req) is { } error)
        return Results.BadRequest(new { mensaje = error });

    if (req.ProfesionalId is not null && !await context.Profesionales.AnyAsync(p => p.Id == req.ProfesionalId && p.ComercioId == horario.ComercioId))
        return Results.NotFound(new { mensaje = "El profesional no pertenece a este comercio." });

    horario.DiaSemana = req.DiaSemana;
    horario.HoraInicio = req.HoraInicio;
    horario.HoraFin = req.HoraFin;
    horario.ProfesionalId = req.ProfesionalId;
    await context.SaveChangesAsync();

    return Results.Ok(horario);
}).RequireAuthorization("AdminCliente");

app.MapDelete("/api/horarios/{id:int}", async (AppDbContext context, int id, ClaimsPrincipal user) =>
{
    var horario = await context.Horarios.FindAsync(id);
    if (horario is null) return Results.NotFound();
    if (ComercioIdDelToken(user) != horario.ComercioId) return Results.Forbid();

    context.Horarios.Remove(horario);
    await context.SaveChangesAsync();
    return Results.NoContent();
}).RequireAuthorization("AdminCliente");

// ==========================================
// DISPONIBILIDAD (la grilla verde/gris que ve el cliente)
// ==========================================
app.MapGet("/api/comercios/{comercioId:int}/disponibilidad", async (AppDbContext context, int comercioId, int servicioId, DateOnly fecha, int? profesionalId, int? sucursalId) =>
{
    var comercioActivo = await context.Comercios.Where(c => c.Id == comercioId).Select(c => (bool?)c.Activo).FirstOrDefaultAsync();
    if (comercioActivo is null) return Results.NotFound();
    if (comercioActivo == false) return ResultadoComercioInactivo();

    var servicio = await context.Servicios.FindAsync(servicioId);
    if (servicio is null || servicio.ComercioId != comercioId)
        return Results.NotFound(new { mensaje = "El servicio no pertenece a este comercio." });

    if (profesionalId is not null)
    {
        // Horario cuelga de Profesional, no de Sucursal directamente: si vienen los dos
        // parámetros, lo que hay que validar es que el profesional elegido efectivamente
        // pertenezca a esa sucursal (evita mezclar datos de sucursales distintas).
        var profesional = await context.Profesionales.FirstOrDefaultAsync(p => p.Id == profesionalId && p.ComercioId == comercioId);
        if (profesional is null)
            return Results.NotFound(new { mensaje = "El profesional no pertenece a este comercio." });
        if (sucursalId is not null && profesional.SucursalId != sucursalId)
            return Results.NotFound(new { mensaje = "El profesional no pertenece a esa sucursal." });
    }

    var diaSemana = (int)fecha.DayOfWeek;
    var bloques = await ObtenerBloquesHorario(context, comercioId, profesionalId, diaSemana);

    if (bloques.Count == 0)
        return Results.Ok(Array.Empty<SlotDisponibilidad>());

    var inicioDia = fecha.ToDateTime(TimeOnly.MinValue);
    var finDia = fecha.ToDateTime(TimeOnly.MaxValue);

    var turnosDelDia = await context.Turnos
        .Where(t => t.ComercioId == comercioId && t.ProfesionalId == profesionalId && t.FechaHoraInicio < finDia && t.FechaHoraFin > inicioDia)
        .ToListAsync();

    var ocupados = turnosDelDia.Where(OcupaHorario).ToList();
    var duracion = TimeSpan.FromMinutes(servicio.DuracionMinutos);
    // Un servicio viejo con duración 0 (de antes de validarla) dejaría el loop de abajo sin avanzar nunca.
    if (duracion <= TimeSpan.Zero)
        return Results.Ok(Array.Empty<SlotDisponibilidad>());

    // Los horarios que ya empezaron se muestran, pero no se pueden reservar.
    var ahora = AhoraArgentina();

    var slots = new List<SlotDisponibilidad>();
    foreach (var bloque in bloques)
    {
        var cursor = fecha.ToDateTime(TimeOnly.FromTimeSpan(bloque.HoraInicio));
        var finBloque = fecha.ToDateTime(TimeOnly.FromTimeSpan(bloque.HoraFin));

        while (cursor + duracion <= finBloque)
        {
            var finSlot = cursor + duracion;
            var chocaConOcupado = ocupados.Any(t => t.FechaHoraInicio < finSlot && cursor < t.FechaHoraFin);

            slots.Add(new SlotDisponibilidad(cursor, finSlot, !chocaConOcupado && cursor > ahora));
            cursor = finSlot;
        }
    }

    return Results.Ok(slots);
});

// ==========================================
// ENDPOINTS PARA TURNOS
// ==========================================
app.MapPost("/api/turnos", async (AppDbContext context, IConfiguration config, ILogger<Program> logger, CrearTurnoRequest req) =>
{
    // El email es obligatorio (ahí le llega la confirmación al cliente), igual que en el
    // formulario de la página pública.
    if (string.IsNullOrWhiteSpace(req.ClienteEmail) || !EmailValido(req.ClienteEmail.Trim()))
        return Results.BadRequest(new { mensaje = "Ingresá un email válido: ahí te llega la confirmación del turno." });

    if (string.IsNullOrWhiteSpace(req.ClienteNombre))
        return Results.BadRequest(new { mensaje = "Ingresá tu nombre." });
    var nombreCliente = req.ClienteNombre.Trim();
    if (nombreCliente.Length > LargoMaximoNombreCliente)
        return Results.BadRequest(new { mensaje = $"El nombre puede tener hasta {LargoMaximoNombreCliente} caracteres." });

    // La página pública no ofrece horarios que ya empezaron; esto frena a quien arme el pedido a mano.
    if (req.FechaHoraInicio <= AhoraArgentina())
        return Results.Conflict(new { mensaje = "Ese horario ya pasó. Elegí otro." });

    var servicio = await context.Servicios.FindAsync(req.ServicioId);
    if (servicio is null || servicio.ComercioId != req.ComercioId)
        return Results.NotFound(new { mensaje = "El servicio no pertenece a este comercio." });

    var comercio = await context.Comercios.FindAsync(req.ComercioId);
    if (comercio is null) return Results.NotFound();
    if (!comercio.Activo) return ResultadoComercioInactivo();

    // Se paga al reservar si el servicio pide seña, o si el comercio cobra el servicio completo
    // por Mercado Pago. Por Mercado Pago (si el comercio lo ofrece) o por transferencia de la
    // seña, y en ese caso el comprobante es obligatorio.
    var montoMercadoPago = MontoMercadoPagoServicio(comercio, servicio);
    var pideSenia = servicio.MontoSeña is > 0 || montoMercadoPago is not null;
    var seniaPorMercadoPago = false;
    (byte[] Contenido, string Extension)? comprobante = null;
    if (pideSenia)
    {
        var mercadoPagoDisponible = montoMercadoPago is not null;
        if (req.MedioSenia == "MercadoPago")
        {
            if (!mercadoPagoDisponible)
                return Results.BadRequest(new { mensaje = "Este local no cobra la seña con Mercado Pago. Pagala por transferencia." });
            seniaPorMercadoPago = true;
        }
        else
        {
            // Sin seña no hay nada para transferir: ese turno se paga solo por Mercado Pago.
            if (servicio.MontoSeña is not > 0)
                return Results.BadRequest(new { mensaje = "Este local cobra el turno con Mercado Pago al reservar." });
            if (mercadoPagoDisponible && !comercio.SeñaPorTransferencia)
                return Results.BadRequest(new { mensaje = "Este local cobra la seña solo con Mercado Pago." });
            comprobante = LeerComprobante(req.ComprobanteBase64);
            if (comprobante is null)
                return Results.BadRequest(new { mensaje = "Subí la captura del comprobante de la seña (PNG, JPG o WEBP, hasta 5MB)." });
        }
    }

    if (req.ProfesionalId is not null && !await context.Profesionales.AnyAsync(p => p.Id == req.ProfesionalId && p.ComercioId == req.ComercioId))
        return Results.NotFound(new { mensaje = "El profesional no pertenece a este comercio." });

    var finTurno = req.FechaHoraInicio + TimeSpan.FromMinutes(servicio.DuracionMinutos);

    var diaSemana = (int)req.FechaHoraInicio.DayOfWeek;
    var bloques = await ObtenerBloquesHorario(context, req.ComercioId, req.ProfesionalId, diaSemana);
    if (!EstaDentroDeAlgunHorario(bloques, req.FechaHoraInicio, finTurno))
        return Results.Conflict(new { mensaje = "Ese horario ya no está disponible. Elegí otro." });

    // Desde acá hasta guardar, la agenda queda bloqueada: si dos personas reservan el mismo
    // horario en el mismo segundo, la segunda espera y ve el turno de la primera al chequear.
    await using var transaccion = await context.Database.BeginTransactionAsync();
    await BloquearAgenda(context, req.ComercioId, req.ProfesionalId);

    if (comercio.PlanActual == "Gratuito")
    {
        var inicioMes = new DateTime(req.FechaHoraInicio.Year, req.FechaHoraInicio.Month, 1);
        var inicioMesSiguiente = inicioMes.AddMonths(1);
        var turnosDelMes = await context.Turnos.CountAsync(t =>
            t.ComercioId == req.ComercioId
            && t.EstadoReserva != 3 // cualquier estado excepto Cancelado
            && t.FechaHoraInicio >= inicioMes && t.FechaHoraInicio < inicioMesSiguiente);

        if (turnosDelMes >= TopeTurnosMensualesGratuito)
            return Results.Conflict(new { mensaje = $"Este comercio alcanzó el límite de {TopeTurnosMensualesGratuito} turnos de ese mes en el plan Gratuito." });
    }

    var turnosQueChocan = await context.Turnos
        .Where(t => t.ComercioId == req.ComercioId
                 && t.ProfesionalId == req.ProfesionalId
                 && t.FechaHoraInicio < finTurno
                 && t.FechaHoraFin > req.FechaHoraInicio)
        .ToListAsync();

    if (turnosQueChocan.Any(OcupaHorario))
        return Results.Conflict(new { mensaje = "Ese horario ya no está disponible. Elegí otro." });

    var turno = new Turno
    {
        ComercioId = req.ComercioId,
        ServicioId = req.ServicioId,
        ProfesionalId = req.ProfesionalId,
        FechaHoraInicio = req.FechaHoraInicio,
        FechaHoraFin = finTurno,
        ClienteNombre = nombreCliente,
        ClienteWhatsApp = req.ClienteWhatsApp,
        ClienteEmail = req.ClienteEmail.Trim(),
        // Las reservas de la página pública entran confirmadas: ya no hay pre-reserva que el
        // comercio tenga que confirmar en 2 horas. Si el servicio pide seña por transferencia,
        // el turno queda reservado igual, con la seña "a verificar" hasta que el comercio la
        // revise. Con Mercado Pago queda como pre-reserva hasta que se apruebe el pago.
        EstadoReserva = seniaPorMercadoPago ? 1 : 2,
        SeñaVerificada = pideSenia ? false : null,
        SeñaMedio = !pideSenia ? null : seniaPorMercadoPago ? "MercadoPago" : "Transferencia",
        MontoMercadoPago = seniaPorMercadoPago ? montoMercadoPago : null,
        FechaCreacion = DateTime.UtcNow,
        MontoCobrado = servicio.Precio,
        Origen = OrigenPaginaPublica
    };

    string? rutaComprobanteGuardado = null;
    if (comprobante is { } archivo)
    {
        turno.ComprobanteArchivo = $"{Guid.NewGuid():N}{archivo.Extension}";
        rutaComprobanteGuardado = Path.Combine(rutaComprobantes, turno.ComprobanteArchivo);
        await File.WriteAllBytesAsync(rutaComprobanteGuardado, archivo.Contenido);
    }

    context.Turnos.Add(turno);
    try
    {
        await context.SaveChangesAsync();
        await transaccion.CommitAsync();
    }
    catch
    {
        // Si el turno no se pudo guardar, no dejamos el comprobante huérfano en disco.
        if (rutaComprobanteGuardado is not null && File.Exists(rutaComprobanteGuardado)) File.Delete(rutaComprobanteGuardado);
        throw;
    }

    if (seniaPorMercadoPago)
    {
        // Link de pago de la seña, con la cuenta del comercio. Los avisos de turno reservado
        // salen recién cuando Mercado Pago confirma el pago (ProcesarPagoSenia). La vuelta del
        // checkout lleva el token del turno (el mismo del link de cancelación del mail) para
        // que la página pública pueda consultar cómo quedó.
        var accessToken = await AccessTokenMercadoPagoComercio(context, config, logger, comercio);
        var urlVuelta = $"{UrlFrontend(config)}/{comercio.AliasUrl}?pagoTurno={turno.TokenCancelacion}";
        var preferencia = accessToken is null ? null : await mercadoPago.CrearPreferencia(accessToken, new PreferenciaMp(
            Items: [new ItemPreferenciaMp($"{(montoMercadoPago >= servicio.Precio ? "Turno" : "Seña")}: {servicio.Nombre} - {comercio.Nombre}", 1, montoMercadoPago!.Value)],
            ExternalReference: $"turno-{turno.Id}",
            NotificationUrl: $"{UrlApi(config)}/api/mercadopago/webhook/senias?comercioId={comercio.Id}",
            BackUrls: new BackUrlsMp(urlVuelta, urlVuelta, urlVuelta),
            Payer: new PagadorMp(turno.ClienteEmail),
            PaymentMethods: SinPagosEnEfectivo(),
            Expires: true,
            ExpirationDateTo: FechaMercadoPago(AhoraArgentina().AddMinutes(MinutosParaPagarSeniaMercadoPago))));

        if (preferencia is null)
        {
            // Sin link de pago, la pre-reserva no le sirve a nadie: se libera el horario.
            turno.EstadoReserva = 3;
            await context.SaveChangesAsync();
            return Results.Json(new { mensaje = "No pudimos generar el pago con Mercado Pago. Probá de nuevo en un rato." }, statusCode: StatusCodes.Status502BadGateway);
        }

        return Results.Created($"/api/turnos/{turno.Id}", new { turno.Id, UrlPago = preferencia.InitPoint });
    }

    await NotificarTurnoReservado(context, config, logger, turno, comercio, servicio);

    return Results.Created($"/api/turnos/{turno.Id}", turno);
}).RequireRateLimiting("turnos");

// Carga manual de un turno por parte del dueño del comercio (ej. un cliente que pidió
// el turno de forma presencial). Queda directamente Confirmado: el dueño ya acordó
// con el cliente, no pasa por el flujo de pre-reserva de 2hs.
app.MapPost("/api/comercios/{comercioId:int}/turnos", async (AppDbContext context, int comercioId, AdminCrearTurnoRequest req, ClaimsPrincipal user) =>
{
    if (ComercioIdDelToken(user) != comercioId) return Results.Forbid();

    if (string.IsNullOrWhiteSpace(req.ClienteNombre))
        return Results.BadRequest(new { mensaje = "Ingresá el nombre del cliente." });

    var comercio = await context.Comercios.FindAsync(comercioId);
    if (comercio is null) return Results.NotFound();

    var servicio = await context.Servicios.FindAsync(req.ServicioId);
    if (servicio is null || servicio.ComercioId != comercioId)
        return Results.NotFound(new { mensaje = "El servicio no pertenece a este comercio." });

    if (req.ProfesionalId is not null && !await context.Profesionales.AnyAsync(p => p.Id == req.ProfesionalId && p.ComercioId == comercioId))
        return Results.NotFound(new { mensaje = "El profesional no pertenece a este comercio." });

    var finTurno = req.FechaHoraInicio + TimeSpan.FromMinutes(servicio.DuracionMinutos);

    // Mismo bloqueo que la reserva pública: así una carga manual y una reserva online del
    // mismo horario no pueden pasar las dos el chequeo de choque a la vez.
    await using var transaccion = await context.Database.BeginTransactionAsync();
    await BloquearAgenda(context, comercioId, req.ProfesionalId);

    var turnosQueChocan = await context.Turnos
        .Where(t => t.ComercioId == comercioId
                 && t.ProfesionalId == req.ProfesionalId
                 && t.FechaHoraInicio < finTurno
                 && t.FechaHoraFin > req.FechaHoraInicio)
        .ToListAsync();

    if (turnosQueChocan.Any(OcupaHorario))
        return Results.Conflict(new { mensaje = "Ya tenés un turno cargado en ese horario." });

    if (comercio.PlanActual == "Gratuito")
    {
        var inicioMes = new DateTime(req.FechaHoraInicio.Year, req.FechaHoraInicio.Month, 1);
        var inicioMesSiguiente = inicioMes.AddMonths(1);
        var turnosDelMes = await context.Turnos.CountAsync(t =>
            t.ComercioId == comercioId
            && t.EstadoReserva != 3
            && t.FechaHoraInicio >= inicioMes && t.FechaHoraInicio < inicioMesSiguiente);

        if (turnosDelMes >= TopeTurnosMensualesGratuito)
            return Results.Conflict(new { mensaje = $"Este comercio alcanzó el límite de {TopeTurnosMensualesGratuito} turnos de ese mes en el plan Gratuito." });
    }

    var turno = new Turno
    {
        ComercioId = comercioId,
        ServicioId = servicio.Id,
        ProfesionalId = req.ProfesionalId,
        FechaHoraInicio = req.FechaHoraInicio,
        FechaHoraFin = finTurno,
        ClienteNombre = req.ClienteNombre.Trim(),
        ClienteWhatsApp = req.ClienteWhatsApp?.Trim() ?? string.Empty,
        ClienteEmail = req.ClienteEmail?.Trim() ?? string.Empty,
        EstadoReserva = 2,
        FechaCreacion = DateTime.UtcNow,
        MontoCobrado = servicio.Precio,
        Origen = OrigenPanel
    };

    context.Turnos.Add(turno);
    await context.SaveChangesAsync();
    await transaccion.CommitAsync();

    return Results.Created($"/api/turnos/{turno.Id}", turno);
}).RequireAuthorization("AdminCliente");

// ==========================================
// HISTORIAL (cortes realizados + control de ingresos)
// ==========================================
// Un turno "realizado" es un Confirmado cuya fecha ya pasó: no hace falta un estado
// nuevo ni un job en segundo plano, se calcula al consultar.
app.MapGet("/api/comercios/{comercioId:int}/historial", async (AppDbContext context, int comercioId, int? anio, int? mes, ClaimsPrincipal user) =>
{
    if (ComercioIdDelToken(user) != comercioId) return Results.Forbid();

    var ahora = AhoraArgentina();
    var inicioHoy = ahora.Date;
    var inicioSemana = inicioHoy.AddDays(-(int)inicioHoy.DayOfWeek);
    var inicioMesActual = new DateTime(ahora.Year, ahora.Month, 1);

    var realizados = context.Turnos.Where(t => t.ComercioId == comercioId && t.EstadoReserva == 2 && t.FechaHoraInicio <= ahora);

    var totalHoy = await realizados.Where(t => t.FechaHoraInicio >= inicioHoy).SumAsync(t => (decimal?)t.MontoCobrado) ?? 0;
    var totalSemana = await realizados.Where(t => t.FechaHoraInicio >= inicioSemana).SumAsync(t => (decimal?)t.MontoCobrado) ?? 0;
    var totalMes = await realizados.Where(t => t.FechaHoraInicio >= inicioMesActual).SumAsync(t => (decimal?)t.MontoCobrado) ?? 0;

    var anioFiltro = anio ?? ahora.Year;
    var mesFiltro = mes ?? ahora.Month;
    var inicioFiltro = new DateTime(anioFiltro, mesFiltro, 1);
    var finFiltro = inicioFiltro.AddMonths(1);

    var items = await (
        from t in realizados
        where t.FechaHoraInicio >= inicioFiltro && t.FechaHoraInicio < finFiltro
        join s in context.Servicios on t.ServicioId equals s.Id into servicioJoin
        from s in servicioJoin.DefaultIfEmpty()
        orderby t.FechaHoraInicio descending
        select new HistorialItemDto(t.Id, t.FechaHoraInicio, t.ClienteNombre, s != null ? s.Nombre : "—", t.MontoCobrado ?? 0)
    ).ToListAsync();

    return Results.Ok(new HistorialDto(items, totalHoy, totalSemana, totalMes));
}).RequireAuthorization("AdminCliente");

// ==========================================
// GANANCIAS (exclusivo plan Premium)
// ==========================================
app.MapGet("/api/comercios/{comercioId:int}/ganancias", async (AppDbContext context, int comercioId, DateOnly? desde, DateOnly? hasta, ClaimsPrincipal user) =>
{
    if (ComercioIdDelToken(user) != comercioId) return Results.Forbid();

    var comercio = await context.Comercios.FindAsync(comercioId);
    if (comercio is null) return Results.NotFound();

    if (comercio.PlanActual != "Premium")
        return Results.Json(new { mensaje = "El panel de ganancias es exclusivo del plan Premium." }, statusCode: StatusCodes.Status403Forbidden);

    var hoy = DateOnly.FromDateTime(AhoraArgentina());
    var fechaDesde = desde ?? new DateOnly(hoy.Year, hoy.Month, 1);
    var fechaHasta = hasta ?? hoy;

    var inicio = fechaDesde.ToDateTime(TimeOnly.MinValue);
    var fin = fechaHasta.ToDateTime(TimeOnly.MaxValue);

    var turnosDelPeriodo = await context.Turnos
        .Where(t => t.ComercioId == comercioId && t.EstadoReserva == 2 && t.FechaHoraInicio >= inicio && t.FechaHoraInicio <= fin)
        .ToListAsync();

    // Resumen del dashboard (KPIs de arriba de la pestaña Ganancias): son fijos —hoy, últimos
    // 7 días, este mes, total histórico— sin importar qué "desde/hasta" se esté mirando en el
    // resto de la pantalla. "Últimos 7 días" es una ventana corrediza (hoy y los 6 anteriores),
    // no la semana calendario, para que coincida con lo que se ve en el gráfico de barras.
    var inicioHoy = hoy.ToDateTime(TimeOnly.MinValue);
    var finHoy = hoy.ToDateTime(TimeOnly.MaxValue);
    var inicioUltimos7Dias = hoy.AddDays(-6).ToDateTime(TimeOnly.MinValue);

    var turnosConfirmadosTodos = await context.Turnos
        .Where(t => t.ComercioId == comercioId && t.EstadoReserva == 2)
        .Select(t => new { t.FechaHoraInicio, t.MontoCobrado, t.ClienteNombre, t.ClienteWhatsApp })
        .ToListAsync();

    var facturadoHoy = turnosConfirmadosTodos.Where(t => t.FechaHoraInicio >= inicioHoy && t.FechaHoraInicio <= finHoy).Sum(t => t.MontoCobrado ?? 0);
    var facturadoUltimos7Dias = turnosConfirmadosTodos.Where(t => t.FechaHoraInicio >= inicioUltimos7Dias && t.FechaHoraInicio <= finHoy).Sum(t => t.MontoCobrado ?? 0);
    var facturadoEsteMes = turnosConfirmadosTodos.Where(t => t.FechaHoraInicio >= new DateOnly(hoy.Year, hoy.Month, 1).ToDateTime(TimeOnly.MinValue) && t.FechaHoraInicio <= finHoy).Sum(t => t.MontoCobrado ?? 0);
    var cortesTotales = turnosConfirmadosTodos.Count;

    var facturacionPorDia = new List<FacturacionDiaDto>();
    for (var i = 6; i >= 0; i--)
    {
        var dia = hoy.AddDays(-i);
        var inicioDia = dia.ToDateTime(TimeOnly.MinValue);
        var finDia = dia.ToDateTime(TimeOnly.MaxValue);
        var totalDia = turnosConfirmadosTodos.Where(t => t.FechaHoraInicio >= inicioDia && t.FechaHoraInicio <= finDia).Sum(t => t.MontoCobrado ?? 0);
        facturacionPorDia.Add(new FacturacionDiaDto(dia, totalDia));
    }

    var profesionales = await context.Profesionales
        .Where(p => p.ComercioId == comercioId)
        .ToListAsync();
    var nombresPorId = profesionales.ToDictionary(p => p.Id, p => p.Nombre);
    var sucursalPorProfesionalId = profesionales.ToDictionary(p => p.Id, p => p.SucursalId);
    var nombresSucursalPorId = await context.Sucursales
        .Where(s => s.ComercioId == comercioId)
        .ToDictionaryAsync(s => s.Id, s => s.Nombre);

    var servicios = await context.Servicios
        .Where(s => s.ComercioId == comercioId)
        .ToDictionaryAsync(s => s.Id, s => s.Nombre);

    var servicioMasPedido = turnosDelPeriodo
        .GroupBy(t => t.ServicioId)
        .Select(g => new ServicioPedidoDto(
            g.Key,
            g.Key is not null && servicios.TryGetValue(g.Key.Value, out var nombreServicio) ? nombreServicio : "Sin servicio",
            g.Count()))
        .OrderByDescending(x => x.Cantidad)
        .Take(5)
        .ToList();

    var horariosOcupados = turnosDelPeriodo
        .GroupBy(t => t.FechaHoraInicio.Hour)
        .Select(g => new HorarioOcupadoDto(g.Key, g.Count()))
        .OrderBy(x => x.Hora)
        .ToList();

    // Clientes frecuentes y "para reactivar" miran TODO el historial confirmado, no el
    // rango desde/hasta elegido arriba: son sobre la relación con el cliente, no sobre
    // la facturación de un período puntual. Se identifica al cliente por WhatsApp (más
    // estable que el nombre, que puede repetirse o tipearse distinto entre visitas); si
    // no cargó WhatsApp, se cae al nombre como alternativa.
    string ClavePorCliente(string clienteWhatsApp, string clienteNombre) =>
        string.IsNullOrWhiteSpace(clienteWhatsApp) ? clienteNombre : clienteWhatsApp;

    var clientesFrecuentes = turnosConfirmadosTodos
        .Where(t => !string.IsNullOrWhiteSpace(t.ClienteNombre))
        .GroupBy(t => ClavePorCliente(t.ClienteWhatsApp, t.ClienteNombre))
        .Select(g => new ClienteFrecuenteDto(g.OrderByDescending(t => t.FechaHoraInicio).First().ClienteNombre, g.Count(), g.Max(t => t.FechaHoraInicio)))
        .OrderByDescending(x => x.CantidadTurnos)
        .Take(5)
        .ToList();

    // "Para reactivar": clientes con al menos un turno confirmado cuya última visita fue
    // hace 30 días o más. Ordenados por los que hace más tiempo que no vuelven primero.
    var umbralReactivar = AhoraArgentina().AddDays(-30);
    var paraReactivar = turnosConfirmadosTodos
        .Where(t => !string.IsNullOrWhiteSpace(t.ClienteNombre))
        .GroupBy(t => ClavePorCliente(t.ClienteWhatsApp, t.ClienteNombre))
        .Select(g => new { Nombre = g.OrderByDescending(t => t.FechaHoraInicio).First().ClienteNombre, UltimaVisita = g.Max(t => t.FechaHoraInicio), CantidadTurnos = g.Count() })
        .Where(x => x.UltimaVisita < umbralReactivar)
        .OrderBy(x => x.UltimaVisita)
        .Take(5)
        .Select(x => new ClienteReactivarDto(x.Nombre, x.CantidadTurnos, DateOnly.FromDateTime(x.UltimaVisita)))
        .ToList();

    var porProfesional = turnosDelPeriodo
        .GroupBy(t => t.ProfesionalId)
        .Select(g => new GananciaPorProfesionalDto(
            g.Key,
            g.Key is not null && nombresPorId.TryGetValue(g.Key.Value, out var nombre) ? nombre : "Sin profesional asignado",
            g.Count(),
            g.Sum(t => t.MontoCobrado ?? 0)))
        .OrderByDescending(x => x.Ingresos)
        .ToList();

    // La sucursal de un turno sale de la sucursal del profesional que lo atendió (Turno no
    // guarda SucursalId directo). Turnos sin profesional, o con un profesional sin sucursal
    // asignada, se agrupan igual bajo "Sin sucursal asignada" en vez de perderse del total.
    var porSucursal = turnosDelPeriodo
        .GroupBy(t => t.ProfesionalId is not null && sucursalPorProfesionalId.TryGetValue(t.ProfesionalId.Value, out var suc) ? suc : null)
        .Select(g => new GananciaPorSucursalDto(
            g.Key,
            g.Key is not null && nombresSucursalPorId.TryGetValue(g.Key.Value, out var nombreSucursal) ? nombreSucursal : "Sin sucursal asignada",
            g.Count(),
            g.Sum(t => t.MontoCobrado ?? 0)))
        .OrderByDescending(x => x.Ingresos)
        .ToList();

    return Results.Ok(new GananciasDto(
        porProfesional, porSucursal, turnosDelPeriodo.Count, turnosDelPeriodo.Sum(t => t.MontoCobrado ?? 0),
        facturadoHoy, facturadoUltimos7Dias, facturadoEsteMes, cortesTotales, facturacionPorDia,
        servicioMasPedido, horariosOcupados, clientesFrecuentes, paraReactivar));
}).RequireAuthorization("AdminCliente");

// ==========================================
// RESUMEN (pestaña Inicio del panel, todos los planes)
// Todo agregado en una sola llamada para el rango desde/hasta (por defecto, los últimos
// 30 días contando hoy). "Turno reservado" = el mismo criterio que usa la grilla pública
// para ocupar un horario: confirmado, o pre-reserva todavía vigente (OcupaHorario).
// ==========================================
app.MapGet("/api/comercios/{comercioId:int}/resumen", async (AppDbContext context, int comercioId, DateOnly? desde, DateOnly? hasta, ClaimsPrincipal user) =>
{
    if (ComercioIdDelToken(user) != comercioId) return Results.Forbid();

    var comercio = await context.Comercios.FindAsync(comercioId);
    if (comercio is null) return Results.NotFound();

    var hoy = DateOnly.FromDateTime(AhoraArgentina());
    var fechaHasta = hasta ?? hoy;
    var fechaDesde = desde ?? fechaHasta.AddDays(-29);
    if (fechaDesde > fechaHasta) return Results.BadRequest(new { mensaje = "La fecha \"desde\" no puede ser posterior a \"hasta\"." });
    if (fechaHasta.DayNumber - fechaDesde.DayNumber > 366) return Results.BadRequest(new { mensaje = "El rango no puede superar un año." });

    var cantidadDias = fechaHasta.DayNumber - fechaDesde.DayNumber + 1;
    // Período anterior del mismo largo, para comparar los KPIs ("vs. los N días anteriores").
    var fechaDesdeAnterior = fechaDesde.AddDays(-cantidadDias);

    var inicioRango = fechaDesdeAnterior.ToDateTime(TimeOnly.MinValue);
    var finRango = fechaHasta.ToDateTime(TimeOnly.MaxValue);

    var turnosCrudos = await context.Turnos
        .Where(t => t.ComercioId == comercioId && t.EstadoReserva != 3 && t.FechaHoraInicio >= inicioRango && t.FechaHoraInicio <= finRango)
        .ToListAsync();
    var turnosReservados = turnosCrudos.Where(OcupaHorario).ToList();

    var inicioPeriodo = fechaDesde.ToDateTime(TimeOnly.MinValue);
    var turnos = turnosReservados.Where(t => t.FechaHoraInicio >= inicioPeriodo).ToList();
    var turnosAnteriores = turnosReservados.Where(t => t.FechaHoraInicio < inicioPeriodo).ToList();

    var servicios = await context.Servicios.Where(s => s.ComercioId == comercioId).ToDictionaryAsync(s => s.Id);
    var profesionales = await context.Profesionales.Where(p => p.ComercioId == comercioId).OrderBy(p => p.Nombre).ToListAsync();
    var horarios = await context.Horarios.Where(h => h.ComercioId == comercioId).ToListAsync();

    // Monto del turno: lo cobrado al reservar (MontoCobrado) y, si no quedó guardado, el
    // precio actual del servicio. Es una estimación: incluye turnos que todavía no pasaron.
    decimal MontoTurno(Turno t) =>
        t.MontoCobrado ?? (t.ServicioId is not null && servicios.TryGetValue(t.ServicioId.Value, out var s) ? s.Precio : 0m);

    static int MinutosTurno(Turno t) => Math.Max(0, (int)(t.FechaHoraFin - t.FechaHoraInicio).TotalMinutes);

    // Minutos de atención disponibles en el período para una "agenda" (un profesional con
    // sus propios horarios, o el horario general con ProfesionalId nulo): se suma, día por
    // día, el largo de los bloques horarios de ese día de la semana.
    int MinutosDisponibles(int? profesionalId)
    {
        var minutosPorDiaSemana = new int[7];
        foreach (var h in horarios.Where(h => h.ProfesionalId == profesionalId))
            minutosPorDiaSemana[h.DiaSemana] += Math.Max(0, (int)(h.HoraFin - h.HoraInicio).TotalMinutes);

        var total = 0;
        for (var d = fechaDesde; d <= fechaHasta; d = d.AddDays(1))
            total += minutosPorDiaSemana[(int)d.DayOfWeek];
        return total;
    }

    // La ocupación total junta todas las agendas que pueden recibir turnos: el horario
    // general (turnos sin profesional) más el de cada profesional.
    var minutosDisponiblesTotal = MinutosDisponibles(null) + profesionales.Sum(p => MinutosDisponibles(p.Id));
    var minutosReservadosTotal = turnos.Sum(MinutosTurno);

    var ocupacionPorProfesional = profesionales
        .Select(p =>
        {
            var reservados = turnos.Where(t => t.ProfesionalId == p.Id).Sum(MinutosTurno);
            return new OcupacionProfesionalDto(p.Id, p.Nombre, reservados, MinutosDisponibles(p.Id));
        })
        .ToList();

    var turnosPorDia = new List<TurnosDiaDto>();
    var cantidadPorFecha = turnos.GroupBy(t => DateOnly.FromDateTime(t.FechaHoraInicio)).ToDictionary(g => g.Key, g => g.Count());
    for (var d = fechaDesde; d <= fechaHasta; d = d.AddDays(1))
        turnosPorDia.Add(new TurnosDiaDto(d, cantidadPorFecha.GetValueOrDefault(d)));

    var horariosMasPedidos = turnos
        .GroupBy(t => new { DiaSemana = (int)t.FechaHoraInicio.DayOfWeek, Hora = t.FechaHoraInicio.Hour })
        .Select(g => new CeldaHeatmapDto(g.Key.DiaSemana, g.Key.Hora, g.Count()))
        .OrderBy(c => c.DiaSemana).ThenBy(c => c.Hora)
        .ToList();

    var serviciosMasReservados = turnos
        .GroupBy(t => t.ServicioId)
        .Select(g => new ServicioResumenDto(
            g.Key,
            g.Key is not null && servicios.TryGetValue(g.Key.Value, out var s) ? s.Nombre : "Sin servicio",
            g.Count(),
            g.Sum(MontoTurno)))
        .OrderByDescending(x => x.Cantidad)
        .Take(5)
        .ToList();

    // "¿A qué hora reservan tus clientes?": hora (de Argentina) en la que se CREÓ el turno.
    // Solo reservas online: la carga presencial del panel no pide email y la página pública
    // sí, así que se toman los turnos con email. FechaCreacion se guarda en UTC.
    var reservasOnline = turnos.Where(t => !string.IsNullOrWhiteSpace(t.ClienteEmail)).ToList();
    var reservasPorHora = new int[24];
    var reservasFueraDeHorario = 0;
    foreach (var t in reservasOnline)
    {
        var creacionLocal = t.FechaCreacion.AddHours(-3);
        reservasPorHora[creacionLocal.Hour]++;

        // "Fuera de horario" = en ese momento el negocio no estaba atendiendo según ningún
        // horario cargado (general o de algún profesional) para ese día de la semana.
        var hora = creacionLocal.TimeOfDay;
        var abierto = horarios.Any(h => h.DiaSemana == (int)creacionLocal.DayOfWeek && hora >= h.HoraInicio && hora < h.HoraFin);
        if (!abierto) reservasFueraDeHorario++;
    }

    return Results.Ok(new ResumenDto(
        fechaDesde, fechaHasta,
        turnos.Count, turnosAnteriores.Count,
        turnos.Sum(MontoTurno), turnosAnteriores.Sum(MontoTurno),
        minutosReservadosTotal, minutosDisponiblesTotal,
        turnosPorDia, horariosMasPedidos, serviciosMasReservados,
        reservasPorHora.Select((cantidad, hora) => new ReservasHoraDto(hora, cantidad)).ToList(),
        reservasOnline.Count, reservasFueraDeHorario,
        ocupacionPorProfesional));
}).RequireAuthorization("AdminCliente");

// ==========================================
// WHATSAPP (placeholder — exclusivo plan Premium)
// Todavía no conecta a la Cloud API real de Meta: solo guarda si el comercio
// activó o no el bot. La integración real se hace aparte con las credenciales.
//
// Diseño ya decidido para cuando se implemente (ver el comentario largo en el punto de
// enganche, en POST /api/turnos): un solo mensaje de WhatsApp por turno —la confirmación
// de la reserva, con los datos de la seña solo si el servicio tiene una configurada—, no
// un recordatorio más una confirmación separados. Es así a propósito por costo: Meta
// cobra por mensaje, y con dos mensajes por turno el margen de Premium se comía rápido en
// comercios con mucho movimiento.
// ==========================================
app.MapGet("/api/comercios/{comercioId:int}/whatsapp-config", async (AppDbContext context, int comercioId, ClaimsPrincipal user) =>
{
    if (ComercioIdDelToken(user) != comercioId) return Results.Forbid();

    var comercio = await context.Comercios.FindAsync(comercioId);
    if (comercio is null) return Results.NotFound();

    if (comercio.PlanActual != "Premium")
        return Results.Json(new { mensaje = "El bot de WhatsApp es exclusivo del plan Premium." }, statusCode: StatusCodes.Status403Forbidden);

    var config = await context.WhatsAppConfigs.FirstOrDefaultAsync(w => w.ComercioId == comercioId);
    return Results.Ok(new WhatsAppConfigDto(config?.Activado ?? false));
}).RequireAuthorization("AdminCliente");

app.MapPost("/api/comercios/{comercioId:int}/whatsapp-config", async (AppDbContext context, int comercioId, WhatsAppConfigDto req, ClaimsPrincipal user) =>
{
    if (ComercioIdDelToken(user) != comercioId) return Results.Forbid();

    var comercio = await context.Comercios.FindAsync(comercioId);
    if (comercio is null) return Results.NotFound();

    if (comercio.PlanActual != "Premium")
        return Results.Json(new { mensaje = "El bot de WhatsApp es exclusivo del plan Premium." }, statusCode: StatusCodes.Status403Forbidden);

    var config = await context.WhatsAppConfigs.FirstOrDefaultAsync(w => w.ComercioId == comercioId);
    if (config is null)
    {
        config = new WhatsAppConfig { ComercioId = comercioId, Activado = req.Activado };
        context.WhatsAppConfigs.Add(config);
    }
    else
    {
        config.Activado = req.Activado;
    }
    await context.SaveChangesAsync();

    return Results.Ok(new WhatsAppConfigDto(config.Activado));
}).RequireAuthorization("AdminCliente");

// incluirVencidos: todos los turnos (también cancelados y pre-reservas vencidas).
// incluirPasados: además de los activos, los confirmados cuya fecha ya pasó, para que el
// calendario del panel muestre los días anteriores.
app.MapGet("/api/comercios/{comercioId:int}/turnos", async (AppDbContext context, int comercioId, bool incluirVencidos, bool? incluirPasados, ClaimsPrincipal user) =>
{
    if (ComercioIdDelToken(user) != comercioId) return Results.Forbid();

    var turnos = await context.Turnos
        .Where(t => t.ComercioId == comercioId)
        .OrderBy(t => t.FechaHoraInicio)
        .ToListAsync();

    if (!incluirVencidos)
    {
        // "Activos" = pre-reservas todavía vigentes o confirmados cuya fecha no pasó.
        // Un confirmado con fecha pasada ya se considera "realizado" y vive en el Historial;
        // no tiene sentido que además siga apareciendo acá.
        var ahora = AhoraArgentina();
        turnos = turnos.Where(t =>
            (t.EstadoReserva == 1 && EsPreReservaVigente(t)) ||
            (t.EstadoReserva == 2 && (incluirPasados == true || t.FechaHoraInicio > ahora))
        ).ToList();
    }

    return Results.Ok(turnos);
}).RequireAuthorization("AdminCliente");

app.MapGet("/api/turnos", async (AppDbContext context) =>
{
    var turnos = await context.Turnos.ToListAsync();
    return Results.Ok(turnos);
}).RequireAuthorization("SuperAdmin");

app.MapPatch("/api/turnos/{id:int}/confirmar", async (AppDbContext context, int id, ClaimsPrincipal user) =>
{
    var turno = await context.Turnos.FindAsync(id);
    if (turno is null) return Results.NotFound();
    if (ComercioIdDelToken(user) != turno.ComercioId) return Results.Forbid();

    turno.EstadoReserva = 2; // Confirmado
    await context.SaveChangesAsync();
    return Results.Ok(turno);
}).RequireAuthorization("AdminCliente");

app.MapPatch("/api/turnos/{id:int}/cancelar", async (AppDbContext context, IConfiguration config, ILogger<Program> logger, int id, ClaimsPrincipal user) =>
{
    var turno = await context.Turnos.FindAsync(id);
    if (turno is null) return Results.NotFound();
    if (ComercioIdDelToken(user) != turno.ComercioId) return Results.Forbid();

    var yaEstabaCancelado = turno.EstadoReserva == 3;
    turno.EstadoReserva = 3; // Cancelado
    await context.SaveChangesAsync();

    // Aviso al cliente, si dejó email (la carga manual no lo pide). No se avisa si el turno
    // ya estaba cancelado ni si ya pasó (ahí el comercio solo está ordenando su agenda).
    if (!yaEstabaCancelado && turno.FechaHoraInicio > AhoraArgentina() && EmailValido(turno.ClienteEmail))
    {
        var comercio = await context.Comercios.FindAsync(turno.ComercioId);
        var servicio = turno.ServicioId is not null ? await context.Servicios.FindAsync(turno.ServicioId) : null;
        var nombreComercio = comercio?.Nombre ?? "El negocio";
        var linkReservar = $"{config["Frontend:BaseUrl"] ?? "http://localhost:4200"}/{comercio?.AliasUrl}";
        var contacto = string.IsNullOrWhiteSpace(comercio?.TelefonoNotificaciones)
            ? ""
            : $"Si tenés dudas, podés escribirle al {comercio.TelefonoNotificaciones}.\n\n";

        EnviarEmailEnSegundoPlano(config, logger, turno.ClienteEmail, $"Tu turno en {nombreComercio} fue cancelado",
            $"Hola {turno.ClienteNombre},\n\n" +
            $"{nombreComercio} canceló tu turno{(servicio is null ? "" : $" para \"{servicio.Nombre}\"")} del {FormatoFechaTurno(turno.FechaHoraInicio)}.\n\n" +
            contacto +
            $"Si querés, podés reservar otro horario acá: {linkReservar}\n\n" +
            "Reserva2",
            "aviso de cancelación al cliente",
            links: [linkReservar]);
    }

    return Results.Ok(turno);
}).RequireAuthorization("AdminCliente");

// Comprobante de la seña: solo el comercio dueño del turno. No es un archivo estático público.
app.MapGet("/api/turnos/{id:int}/comprobante", async (AppDbContext context, int id, ClaimsPrincipal user) =>
{
    var turno = await context.Turnos.FindAsync(id);
    if (turno is null) return Results.NotFound();
    if (ComercioIdDelToken(user) != turno.ComercioId) return Results.Forbid();
    if (turno.ComprobanteArchivo is null) return Results.NotFound(new { mensaje = "Este turno no tiene comprobante." });

    var ruta = Path.Combine(rutaComprobantes, Path.GetFileName(turno.ComprobanteArchivo));
    if (!File.Exists(ruta)) return Results.NotFound(new { mensaje = "No encontramos el archivo del comprobante." });

    var tipo = Path.GetExtension(ruta).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg"
    };
    return Results.File(ruta, tipo);
}).RequireAuthorization("AdminCliente");

app.MapPatch("/api/turnos/{id:int}/sena-verificada", async (AppDbContext context, int id, ClaimsPrincipal user) =>
{
    var turno = await context.Turnos.FindAsync(id);
    if (turno is null) return Results.NotFound();
    if (ComercioIdDelToken(user) != turno.ComercioId) return Results.Forbid();
    if (turno.SeñaVerificada is null) return Results.BadRequest(new { mensaje = "Este turno no tiene seña." });

    turno.SeñaVerificada = true;
    await context.SaveChangesAsync();
    return Results.Ok(turno);
}).RequireAuthorization("AdminCliente");

// ==========================================
// CANCELACIÓN PÚBLICA (el cliente final cancela su turno sin necesitar cuenta,
// usando el link único que le llega por mail)
// ==========================================
app.MapGet("/api/turnos/por-token/{token}", async (AppDbContext context, string token) =>
{
    var turno = await context.Turnos.FirstOrDefaultAsync(t => t.TokenCancelacion == token);
    if (turno is null) return Results.NotFound();

    var comercio = await context.Comercios.FindAsync(turno.ComercioId);
    var servicio = turno.ServicioId is not null ? await context.Servicios.FindAsync(turno.ServicioId) : null;

    return Results.Ok(new TurnoPorTokenDto(
        turno.Id, comercio?.Nombre ?? "—", servicio?.Nombre ?? "—",
        turno.FechaHoraInicio, turno.EstadoReserva,
        PuedeCancelarOnline(turno), HorasMinimasParaCancelar, comercio?.TelefonoNotificaciones));
});

app.MapPost("/api/turnos/por-token/{token}/cancelar", async (AppDbContext context, IConfiguration config, ILogger<Program> logger, string token) =>
{
    var turno = await context.Turnos.FirstOrDefaultAsync(t => t.TokenCancelacion == token);
    if (turno is null) return Results.NotFound();

    if (turno.EstadoReserva == 3)
        return Results.BadRequest(new { mensaje = "Ese turno ya estaba cancelado." });

    var comercio = await context.Comercios.FindAsync(turno.ComercioId);

    if (!PuedeCancelarOnline(turno))
    {
        var telefono = comercio?.TelefonoNotificaciones;
        return Results.Conflict(new
        {
            mensaje = "Ya no se puede cancelar online, comunicate con el comercio" +
                (string.IsNullOrWhiteSpace(telefono) ? "." : $" al {telefono}."),
            telefonoComercio = telefono
        });
    }

    turno.EstadoReserva = 3;
    await context.SaveChangesAsync();

    // Aviso al comercio: el horario le quedó libre y conviene que lo sepa sin entrar al panel.
    if (comercio is not null && EmailValido(comercio.Email))
    {
        var servicio = turno.ServicioId is not null ? await context.Servicios.FindAsync(turno.ServicioId) : null;
        var linkPanel = $"{config["Frontend:BaseUrl"] ?? "http://localhost:4200"}/panel";

        EnviarEmailEnSegundoPlano(config, logger, comercio.Email, $"Turno cancelado: {turno.ClienteNombre} - {FormatoFechaTurno(turno.FechaHoraInicio)}",
            $"{turno.ClienteNombre} canceló su turno desde el link del mail de confirmación.\n\n" +
            $"Servicio: {servicio?.Nombre ?? "-"}\n" +
            $"Fecha y hora: {FormatoFechaTurno(turno.FechaHoraInicio)}\n" +
            $"WhatsApp: {(string.IsNullOrWhiteSpace(turno.ClienteWhatsApp) ? "-" : turno.ClienteWhatsApp)}\n\n" +
            "El horario volvió a quedar libre para otra reserva.\n\n" +
            $"Ver tus turnos: {linkPanel}",
            "aviso de cancelación al comercio",
            links: [linkPanel]);
    }

    return Results.Ok(new { mensaje = "Tu turno fue cancelado." });
}).RequireRateLimiting("turnos");

// ¡ESTA ES LA LÍNEA MÁGICA!
// Arranca la aplicación. Todo lo que defina reglas o tipos va DEBAJO de esto.
// ==========================================
// MERCADO PAGO: CUENTA DEL COMERCIO (para cobrar señas)
// ==========================================
app.MapGet("/api/comercios/{comercioId:int}/mercadopago", async (AppDbContext context, IConfiguration config, int comercioId, ClaimsPrincipal user) =>
{
    if (ComercioIdDelToken(user) != comercioId) return Results.Forbid();
    var comercio = await context.Comercios.FindAsync(comercioId);
    if (comercio is null) return Results.NotFound();

    return Results.Ok(new MercadoPagoConfigDto(OAuthMercadoPagoConfigurado(config), comercio.MercadoPagoAccessToken is not null,
        comercio.MercadoPagoUserId, comercio.SeñaPorMercadoPago, comercio.SeñaPorTransferencia, comercio.CobroMercadoPagoTotal,
        comercio.AddonCobrosOnline, PrecioAddonCobrosMensual));
}).RequireAuthorization("AdminCliente");

// Link para que el dueño autorice a Reserva2 a cobrar en su nombre. "state" va firmado y
// vence en 15 minutos: así la vuelta de Mercado Pago sabe a qué comercio corresponde y nadie
// puede asociar su cuenta a un comercio ajeno armando el link a mano.
app.MapPost("/api/comercios/{comercioId:int}/mercadopago/conectar", async (AppDbContext context, IConfiguration config, int comercioId, ClaimsPrincipal user) =>
{
    if (ComercioIdDelToken(user) != comercioId) return Results.Forbid();
    if (!OAuthMercadoPagoConfigurado(config))
        return Results.BadRequest(new { mensaje = "Mercado Pago todavía no está disponible. Escribinos y lo activamos." });
    var comercioQueConecta = await context.Comercios.FindAsync(comercioId);
    if (comercioQueConecta is null) return Results.NotFound();
    if (!comercioQueConecta.AddonCobrosOnline)
        return Results.BadRequest(new { mensaje = "Los cobros automáticos son un extra que no está incluido en tu plan. Escribinos para sumarlo." });

    var state = protectorEstadoOAuthMp.Protect(comercioId.ToString(), TimeSpan.FromMinutes(15));
    return Results.Ok(new { url = MercadoPagoClient.UrlAutorizacion(config["MercadoPago:ClientId"]!, RedirectUriOAuthMp(config), state) });
}).RequireAuthorization("AdminCliente");

// Mercado Pago redirige acá el navegador del dueño después de autorizar (o rechazar). Se
// canjea el código por los tokens y se lo devuelve al panel con el resultado.
app.MapGet("/api/mercadopago/oauth/callback", async (AppDbContext context, IConfiguration config, ILogger<Program> logger, string? code, string? state) =>
{
    var vuelta = $"{UrlFrontend(config)}/panel?tab=perfil&mercadopago=";
    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state) || !OAuthMercadoPagoConfigurado(config))
        return Results.Redirect(vuelta + "error");

    int comercioId;
    try
    {
        comercioId = int.Parse(protectorEstadoOAuthMp.Unprotect(state));
    }
    catch (System.Security.Cryptography.CryptographicException)
    {
        return Results.Redirect(vuelta + "vencido");
    }

    var comercio = await context.Comercios.FindAsync(comercioId);
    if (comercio is null) return Results.Redirect(vuelta + "error");

    var token = await mercadoPago.CanjearCodigo(config["MercadoPago:ClientId"]!, config["MercadoPago:ClientSecret"]!, code, RedirectUriOAuthMp(config));
    if (token is null) return Results.Redirect(vuelta + "error");

    GuardarTokensMercadoPago(comercio, token);
    // Recién conectada, la cuenta ya queda ofrecida para las señas (se puede apagar).
    comercio.SeñaPorMercadoPago = true;
    await context.SaveChangesAsync();
    logger.LogInformation("El comercio {ComercioId} conectó su cuenta de Mercado Pago {UserId}.", comercioId, token.UserId);

    return Results.Redirect(vuelta + "conectado");
}).RequireRateLimiting("login");

app.MapPatch("/api/comercios/{comercioId:int}/mercadopago", async (AppDbContext context, IConfiguration config, int comercioId, MercadoPagoPreferenciasRequest req, ClaimsPrincipal user) =>
{
    if (ComercioIdDelToken(user) != comercioId) return Results.Forbid();
    var comercio = await context.Comercios.FindAsync(comercioId);
    if (comercio is null) return Results.NotFound();

    if (req.SeñaPorMercadoPago && !comercio.AddonCobrosOnline)
        return Results.BadRequest(new { mensaje = "Los cobros automáticos son un extra que no está incluido en tu plan. Escribinos para sumarlo." });
    if (req.SeñaPorMercadoPago && comercio.MercadoPagoAccessToken is null)
        return Results.BadRequest(new { mensaje = "Primero conectá tu cuenta de Mercado Pago." });
    if (!req.SeñaPorMercadoPago && !req.SeñaPorTransferencia)
        return Results.BadRequest(new { mensaje = "Dejá al menos una forma de pagar la seña." });

    comercio.SeñaPorMercadoPago = req.SeñaPorMercadoPago;
    comercio.SeñaPorTransferencia = req.SeñaPorTransferencia;
    comercio.CobroMercadoPagoTotal = req.CobroTotal;
    await context.SaveChangesAsync();

    return Results.Ok(new MercadoPagoConfigDto(OAuthMercadoPagoConfigurado(config), comercio.MercadoPagoAccessToken is not null,
        comercio.MercadoPagoUserId, comercio.SeñaPorMercadoPago, comercio.SeñaPorTransferencia, comercio.CobroMercadoPagoTotal,
        comercio.AddonCobrosOnline, PrecioAddonCobrosMensual));
}).RequireAuthorization("AdminCliente");

// Desconectar: se borran los tokens y las señas vuelven a ser solo por transferencia. (El
// permiso del lado de Mercado Pago lo puede revocar el dueño desde su cuenta.)
app.MapDelete("/api/comercios/{comercioId:int}/mercadopago", async (AppDbContext context, IConfiguration config, int comercioId, ClaimsPrincipal user) =>
{
    if (ComercioIdDelToken(user) != comercioId) return Results.Forbid();
    var comercio = await context.Comercios.FindAsync(comercioId);
    if (comercio is null) return Results.NotFound();

    comercio.MercadoPagoUserId = null;
    comercio.MercadoPagoAccessToken = null;
    comercio.MercadoPagoRefreshToken = null;
    comercio.MercadoPagoTokenVence = null;
    comercio.SeñaPorMercadoPago = false;
    comercio.SeñaPorTransferencia = true;
    await context.SaveChangesAsync();

    return Results.Ok(new MercadoPagoConfigDto(OAuthMercadoPagoConfigurado(config), false, null, false, true, false,
        comercio.AddonCobrosOnline, PrecioAddonCobrosMensual));
}).RequireAuthorization("AdminCliente");

// ==========================================
// MERCADO PAGO: SEÑAS
// ==========================================
// Notificación de Mercado Pago sobre un pago de seña (notification_url de la preferencia,
// con el comercio en el query). Lo que trae no se cree: solo se usa el id para ir a buscar
// el pago con el token del comercio. Un error de Mercado Pago devuelve 500 para que reintente.
app.MapPost("/api/mercadopago/webhook/senias", async (HttpRequest request, AppDbContext context, IConfiguration config, ILogger<Program> logger, int comercioId) =>
{
    var pagoId = await IdPagoDeNotificacionMp(request);
    if (pagoId is null) return Results.Ok();

    var comercio = await context.Comercios.FindAsync(comercioId);
    if (comercio is null) return Results.Ok();

    try
    {
        await ProcesarPagoSenia(context, config, logger, comercio, pagoId.Value);
        return Results.Ok();
    }
    catch (HttpRequestException ex)
    {
        logger.LogError(ex, "No se pudo consultar el pago {PagoId} de Mercado Pago (comercio {ComercioId}).", pagoId, comercioId);
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
});

// La página pública lo llama cuando el cliente vuelve del checkout (con el token del turno
// y, si Mercado Pago lo pasó, el id del pago), para mostrarle cómo quedó su reserva sin
// depender de que el webhook haya llegado.
app.MapPost("/api/mercadopago/senias/{token}/verificar", async (AppDbContext context, IConfiguration config, ILogger<Program> logger, string token, VerificarPagoRequest req) =>
{
    var turno = await context.Turnos.FirstOrDefaultAsync(t => t.TokenCancelacion == token && t.SeñaMedio == "MercadoPago");
    if (turno is null) return Results.NotFound();

    var estado = turno.MercadoPagoPagoId is not null
        ? (turno.EstadoReserva == 2 ? "confirmado" : "horario_ocupado")
        : "sin_pago";

    if (turno.MercadoPagoPagoId is null && req.PagoId is { } pagoId)
    {
        var comercio = await context.Comercios.FindAsync(turno.ComercioId);
        if (comercio is not null)
        {
            try
            {
                var resultado = await ProcesarPagoSenia(context, config, logger, comercio, pagoId);
                if (resultado != "desconocido") estado = resultado;
            }
            catch (HttpRequestException ex)
            {
                logger.LogError(ex, "No se pudo consultar el pago {PagoId} de Mercado Pago al volver del checkout.", pagoId);
                estado = "pendiente";
            }
        }
    }

    // Si el cliente canceló la reserva mientras tanto (o la abandonó y no pagó) se le dice.
    if (estado == "sin_pago" && turno.EstadoReserva == 3) estado = "cancelado";
    return Results.Ok(new { estado });
}).RequireRateLimiting("turnos");

// El cliente volvió del checkout sin pagar: se libera el horario en el momento en vez de
// esperar a que venza la pre-reserva, así puede volver a elegirlo. Si después igual llega
// el pago, ProcesarPagoSenia recupera el turno si el horario sigue libre.
app.MapPost("/api/mercadopago/senias/{token}/abandonar", async (AppDbContext context, string token) =>
{
    var turno = await context.Turnos.FirstOrDefaultAsync(t => t.TokenCancelacion == token && t.SeñaMedio == "MercadoPago");
    if (turno is null) return Results.NotFound();

    if (turno.EstadoReserva == 1 && turno.MercadoPagoPagoId is null)
    {
        turno.EstadoReserva = 3;
        await context.SaveChangesAsync();
    }
    return Results.NoContent();
}).RequireRateLimiting("turnos");

// ==========================================
// MERCADO PAGO: PAGO DEL PLAN (el comercio le paga a Reserva2)
// ==========================================
app.MapGet("/api/comercios/{comercioId:int}/pago-plan", async (AppDbContext context, IConfiguration config, int comercioId, string plan, string ciclo, ClaimsPrincipal user) =>
{
    if (ComercioIdDelToken(user) != comercioId) return Results.Forbid();
    if (!PlanesValidos.Contains(plan) || ciclo is not ("Mensual" or "Anual"))
        return Results.BadRequest(new { mensaje = "Plan o ciclo inválido." });
    var comercio = await context.Comercios.FindAsync(comercioId);
    if (comercio is null) return Results.NotFound();

    var monto = await MontoPlanComercio(context, comercio, plan, ciclo);
    return Results.Ok(new { monto, disponible = !string.IsNullOrWhiteSpace(config["MercadoPago:AccessToken"]) && monto > 0 });
}).RequireAuthorization("AdminCliente");

// Arranca el pago de un plan (renovación del actual o cambio a otro) con Checkout Pro de la
// cuenta de Reserva2. El monto lo calcula el backend; el plan se aplica recién cuando Mercado
// Pago aprueba el pago.
app.MapPost("/api/comercios/{comercioId:int}/pago-plan", async (AppDbContext context, IConfiguration config, int comercioId, PagarPlanRequest req, ClaimsPrincipal user) =>
{
    if (ComercioIdDelToken(user) != comercioId) return Results.Forbid();
    if (!PlanesValidos.Contains(req.Plan) || req.Ciclo is not ("Mensual" or "Anual"))
        return Results.BadRequest(new { mensaje = "Plan o ciclo inválido." });

    var accessToken = config["MercadoPago:AccessToken"];
    if (string.IsNullOrWhiteSpace(accessToken))
        return Results.BadRequest(new { mensaje = "El pago con Mercado Pago todavía no está disponible. Escribinos por WhatsApp." });

    var comercio = await context.Comercios.FindAsync(comercioId);
    if (comercio is null) return Results.NotFound();

    var monto = await MontoPlanComercio(context, comercio, req.Plan, req.Ciclo);
    if (monto <= 0)
        return Results.BadRequest(new { mensaje = "Ese plan no tiene costo. Si querés pasarte al Gratuito, escribinos por WhatsApp." });

    var pago = new Pago
    {
        ComercioId = comercio.Id,
        Monto = monto,
        Plan = req.Plan,
        Ciclo = req.Ciclo,
        Medio = "MercadoPago",
        Estado = "Pendiente"
    };
    context.Pagos.Add(pago);
    await context.SaveChangesAsync();

    var urlVuelta = $"{UrlFrontend(config)}/panel?tab=plan&pagoPlan={pago.Id}";
    var nombrePlan = req.Plan == "Basico" ? "Básico" : req.Plan;
    var preferencia = await mercadoPago.CrearPreferencia(accessToken, new PreferenciaMp(
        Items: [new ItemPreferenciaMp($"Reserva2 - Plan {nombrePlan} {req.Ciclo.ToLowerInvariant()} - {comercio.Nombre}", 1, monto)],
        ExternalReference: $"pago-{pago.Id}",
        NotificationUrl: $"{UrlApi(config)}/api/mercadopago/webhook/planes",
        BackUrls: new BackUrlsMp(urlVuelta, urlVuelta, urlVuelta),
        Payer: EmailValido(comercio.Email) ? new PagadorMp(comercio.Email) : null,
        PaymentMethods: SinPagosEnEfectivo(),
        StatementDescriptor: "RESERVA2"));

    if (preferencia is null)
    {
        context.Pagos.Remove(pago);
        await context.SaveChangesAsync();
        return Results.Json(new { mensaje = "No pudimos generar el pago con Mercado Pago. Probá de nuevo en un rato." }, statusCode: StatusCodes.Status502BadGateway);
    }

    return Results.Ok(new { urlPago = preferencia.InitPoint, monto });
}).RequireAuthorization("AdminCliente");

app.MapPost("/api/mercadopago/webhook/planes", async (HttpRequest request, AppDbContext context, IConfiguration config, ILogger<Program> logger) =>
{
    var pagoId = await IdPagoDeNotificacionMp(request);
    if (pagoId is null) return Results.Ok();

    try
    {
        await ProcesarPagoPlan(context, config, logger, pagoId.Value);
        return Results.Ok();
    }
    catch (HttpRequestException ex)
    {
        logger.LogError(ex, "No se pudo consultar el pago de plan {PagoId} en Mercado Pago.", pagoId);
        return Results.StatusCode(StatusCodes.Status500InternalServerError);
    }
});

// El panel lo llama al volver del checkout: procesa el pago si el webhook todavía no llegó y
// devuelve cómo quedó el plan, para actualizar la sesión sin tener que volver a loguearse.
app.MapPost("/api/comercios/{comercioId:int}/pago-plan/{id:int}/verificar", async (AppDbContext context, IConfiguration config, ILogger<Program> logger, int comercioId, int id, VerificarPagoRequest req, ClaimsPrincipal user) =>
{
    if (ComercioIdDelToken(user) != comercioId) return Results.Forbid();
    var pago = await context.Pagos.FirstOrDefaultAsync(p => p.Id == id && p.ComercioId == comercioId);
    if (pago is null) return Results.NotFound();

    if (pago.Estado == "Pendiente" && req.PagoId is { } pagoMpId)
    {
        try
        {
            var procesado = await ProcesarPagoPlan(context, config, logger, pagoMpId);
            // Un id de pago que no corresponde a este Pago no lo toca.
            if (procesado?.Id == pago.Id) pago = procesado;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "No se pudo consultar el pago de plan {PagoId} en Mercado Pago al volver del checkout.", pagoMpId);
        }
    }

    var comercio = await context.Comercios.FindAsync(comercioId);
    return Results.Ok(new EstadoPagoPlanDto(pago.Estado, comercio!.PlanActual, comercio.CicloFacturacion, comercio.FechaProximoPago, comercio.Activo));
}).RequireAuthorization("AdminCliente");

// Historial de pagos del plan de un comercio, para la ficha del Super Admin.
app.MapGet("/api/admin/comercios/{id:int}/pagos", async (AppDbContext context, int id) =>
{
    var pagos = await context.Pagos
        .Where(p => p.ComercioId == id && p.Estado != "Pendiente")
        .OrderByDescending(p => p.Fecha)
        .Select(p => new PagoDto(p.Id, p.Fecha, p.Monto, p.Plan, p.Ciclo, p.Medio, p.Estado, p.MercadoPagoPagoId))
        .ToListAsync();
    return Results.Ok(pagos);
}).RequireAuthorization("SuperAdmin");

app.Run();

// ==========================================
// DTOs (Data Transfer Objects)
// (Acá es donde tienen que ir los 'record' y 'class' para que C# 9+ no tire error CS8803)
// ==========================================
record ComercioPublicoDto(int Id, string Nombre, string AliasUrl, string TipoPlantilla, string TelefonoNotificaciones, string? LogoUrl, bool WhatsAppActivo, string DatosBancarios,
    bool SeñaMercadoPago, bool SeñaTransferencia, bool CobroMercadoPagoTotal);
record ComercioDto(int Id, string Nombre, string AliasUrl, string TipoPlantilla, string TelefonoNotificaciones,
    string DatosBancarios, string Email, bool Activo, string PlanActual, DateTime? FechaProximoPago, decimal? MontoMensualAcordado, string CicloFacturacion);
record ActualizarEstadoRequest(bool Activo);
record ActualizarPlanRequest(string PlanActual);
record ActualizarMontoAcordadoRequest(decimal? MontoMensualAcordado);
record ActualizarCicloFacturacionRequest(string CicloFacturacion);
record MetricasDto(int TotalComercios, int ComerciosActivos, int ComerciosInactivos, int TurnosDelMes);
record PlanCantidadDto(string Plan, int Cantidad);
record AltaMesDto(int Anio, int Mes, int Cantidad);
record DashboardDto(
    int TotalComercios, int ComerciosActivos, int ComerciosInactivos, int ComerciosPendientes,
    List<PlanCantidadDto> PorPlan, decimal IngresoMensualEstimado, int TurnosDelMes, List<AltaMesDto> AltasPorMes);
record TurnosPorSemanaDto(DateOnly Desde, DateOnly Hasta, int Cantidad);
record ComercioEstadisticaDto(int Id, DateTime FechaAlta, bool Pendiente, int TurnosDelMes, int TurnosMesAnterior, DateTime? UltimoAcceso);
record MrrMesDto(int Anio, int Mes, decimal Monto);
record AltasBajasMesDto(int Anio, int Mes, int Altas, int Bajas);
record RubroCantidadDto(string Rubro, int Cantidad);
record SuperAdminResumenDto(
    decimal MrrActual, decimal? MrrMesAnterior, List<MrrMesDto> MrrHistorico,
    int TurnosDelMes, int TurnosMesAnterior,
    decimal FacturacionLocalesMes, decimal FacturacionLocalesMesAnterior,
    List<TurnosDiaDto> TurnosPorDia, List<AltasBajasMesDto> AltasYBajasPorMes, List<RubroCantidadDto> PorRubro);
record ComercioDetalleDto(
    int Id, string Nombre, string AliasUrl, string TipoPlantilla, string PlanActual, string CicloFacturacion,
    decimal? MontoMensualAcordado, DateTime FechaAlta, DateTime? FechaProximoPago, bool Activo,
    int CantidadProfesionales, int CantidadSucursales,
    int TurnosHistoricosTotal, int TurnosDelMes, decimal? FacturacionHistorica,
    List<Sucursal> Sucursales, List<Profesional> Profesionales, List<TurnosPorSemanaDto> TurnosPorSemana,
    string Email, string TelefonoNotificaciones, string? WhatsAppNumero, DateTime? UltimoAcceso,
    DateTime? FechaActivacion, DateTime? FechaBaja,
    int CantidadServicios, decimal FacturacionDelMes, List<TurnosDiaDto> TurnosPorDia, int TopeTurnosGratuito,
    bool AddonCobrosOnline, bool MercadoPagoConectado);
record RegistroRequest(string Nombre, string AliasUrl, string TipoPlantilla, string TelefonoNotificaciones, string DatosBancarios, string Email, string Password, string? PlanActual = null, string? CicloFacturacion = null, int? CantidadProfesionales = null, int? CantidadSucursales = null);
record PerfilRequest(string Nombre, string TelefonoNotificaciones, string DatosBancarios);
record PerfilDto(string Nombre, string TelefonoNotificaciones, string DatosBancarios, string? LogoUrl);
record EditarServicioRequest(string Nombre, int DuracionMinutos, decimal Precio, decimal? MontoSeña);
record CambiarPasswordRequest(string PasswordActual, string PasswordNueva);
record TurnoPorTokenDto(int Id, string NombreComercio, string NombreServicio, DateTime FechaHoraInicio, int EstadoReserva,
    bool PuedeCancelar, int HorasMinimasParaCancelar, string? TelefonoComercio);
record LoginRequest(string Email, string Password);
record ForgotPasswordRequest(string Email);
record ResetPasswordRequest(string Token, string NuevaPassword);
record LoginResponse(int ComercioId, string Nombre, string AliasUrl, string Token, string PlanActual, string CicloFacturacion,
    string TelefonoNotificaciones, string DatosBancarios, string? LogoUrl, DateTime? FechaProximoPago, bool Activo);
record SuperAdminLoginRequest(string Email, string Password);
record SuperAdminLoginResponse(string Token);
record ProfesionalRequest(string Nombre, int? SucursalId = null, string? Especialidad = null);
record SucursalRequest(string Nombre, string Direccion, string? Telefono, bool Activa = true);
record HorarioRequest(int DiaSemana, TimeSpan HoraInicio, TimeSpan HoraFin, int? ProfesionalId = null);
record SlotDisponibilidad(DateTime Inicio, DateTime Fin, bool Disponible);
// ComprobanteBase64: captura del comprobante de la seña (PNG, JPG o WEBP, hasta 5MB), en
// base64 con o sin el prefijo "data:image/...;base64,". Obligatorio solo si el servicio
// tiene seña; se ignora si no la tiene.
record CrearTurnoRequest(int ComercioId, int ServicioId, DateTime FechaHoraInicio, string ClienteNombre, string ClienteWhatsApp, string ClienteEmail, int? ProfesionalId = null, string? ComprobanteBase64 = null, string? MedioSenia = null);
record AdminCrearTurnoRequest(int ServicioId, DateTime FechaHoraInicio, string ClienteNombre, string? ClienteWhatsApp, string? ClienteEmail, int? ProfesionalId = null);
record HistorialItemDto(int Id, DateTime FechaHoraInicio, string ClienteNombre, string ServicioNombre, decimal Monto);
record HistorialDto(List<HistorialItemDto> Items, decimal TotalHoy, decimal TotalSemana, decimal TotalMes);
record GananciaPorProfesionalDto(int? ProfesionalId, string NombreProfesional, int CantidadTurnos, decimal Ingresos);
record GananciaPorSucursalDto(int? SucursalId, string NombreSucursal, int CantidadTurnos, decimal Ingresos);
record FacturacionDiaDto(DateOnly Fecha, decimal Total);
record ServicioPedidoDto(int? ServicioId, string NombreServicio, int Cantidad);
record HorarioOcupadoDto(int Hora, int Cantidad);
record ClienteFrecuenteDto(string Nombre, int CantidadTurnos, DateTime UltimaVisita);
record ClienteReactivarDto(string Nombre, int CantidadTurnos, DateOnly UltimaVisita);
record GananciasDto(
    List<GananciaPorProfesionalDto> PorProfesional, List<GananciaPorSucursalDto> PorSucursal, int CantidadTotal, decimal IngresosTotal,
    decimal FacturadoHoy, decimal FacturadoUltimos7Dias, decimal FacturadoEsteMes, int CortesTotales, List<FacturacionDiaDto> FacturacionPorDia,
    List<ServicioPedidoDto> ServicioMasPedido, List<HorarioOcupadoDto> HorariosOcupados,
    List<ClienteFrecuenteDto> ClientesFrecuentes, List<ClienteReactivarDto> ParaReactivar);
record TurnosDiaDto(DateOnly Fecha, int Cantidad);
record CeldaHeatmapDto(int DiaSemana, int Hora, int Cantidad);
record ServicioResumenDto(int? ServicioId, string NombreServicio, int Cantidad, decimal Ingresos);
record ReservasHoraDto(int Hora, int Cantidad);
record OcupacionProfesionalDto(int ProfesionalId, string Nombre, int MinutosReservados, int MinutosDisponibles);
record ResumenDto(
    DateOnly Desde, DateOnly Hasta,
    int Turnos, int TurnosPeriodoAnterior,
    decimal IngresosEstimados, decimal IngresosPeriodoAnterior,
    int MinutosReservados, int MinutosDisponibles,
    List<TurnosDiaDto> TurnosPorDia, List<CeldaHeatmapDto> HorariosMasPedidos, List<ServicioResumenDto> ServiciosMasReservados,
    List<ReservasHoraDto> ReservasPorHoraDeCreacion, int ReservasOnline, int ReservasFueraDeHorario,
    List<OcupacionProfesionalDto> OcupacionPorProfesional);
record WhatsAppConfigDto(bool Activado);
record MercadoPagoConfigDto(bool Disponible, bool Conectado, long? CuentaId, bool SeñaPorMercadoPago, bool SeñaPorTransferencia, bool CobroTotal,
    bool AddonActivo = false, decimal PrecioAddon = 0);
record ActualizarAddonCobrosRequest(bool Activo);
record MercadoPagoPreferenciasRequest(bool SeñaPorMercadoPago, bool SeñaPorTransferencia, bool CobroTotal = false);
record VerificarPagoRequest(long? PagoId);
record PagarPlanRequest(string Plan, string Ciclo);
record EstadoPagoPlanDto(string Estado, string PlanActual, string CicloFacturacion, DateTime? FechaProximoPago, bool Activo);
record PagoDto(int Id, DateTime Fecha, decimal Monto, string Plan, string Ciclo, string Medio, string Estado, long? MercadoPagoPagoId);
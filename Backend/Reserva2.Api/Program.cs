using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Reserva2.Api.Data;
using Reserva2.Api.Models;
using Reserva2.Api.Utils;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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

var app = builder.Build();

// Configuración para poder probar la API visualmente
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseStaticFiles(); // Sirve los logos subidos desde wwwroot/uploads/logos
app.UseCors("AllowAngular");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// ==========================================
// CONSTANTES DE NEGOCIO
// ==========================================
const int HorasLimiteParaConfirmar = 2;
const int TopeTurnosMensualesGratuito = 60;
const int DuracionTokenHoras = 24;

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
    ["Premium"] = (11000m, 8500m)
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
    t.EstadoReserva == 1 && t.FechaCreacion > DateTime.UtcNow.AddHours(-HorasLimiteParaConfirmar);

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
// .ics de un turno) — Brevo lo soporta nativo en el mismo POST, en base64.
async Task EnviarEmail(IConfiguration config, ILogger logger, string destinatario, string asunto, string cuerpo, (string NombreArchivo, string ContenidoBase64)? adjunto = null)
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
        // Los cuerpos de acá se arman como texto plano con "\n"; Brevo espera HTML, así
        // que se convierten los saltos de línea para que no quede todo el mail pegado.
        ["htmlContent"] = cuerpo.Replace("\n", "<br>")
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
                        parameters = parametros.Select(p => new { type = "text", text = p }).ToArray()
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
    if (await context.Comercios.AnyAsync(c => c.Email == req.Email))
        return Results.Conflict(new { mensaje = "Ya existe una cuenta con ese email." });

    if (await context.Comercios.AnyAsync(c => c.AliasUrl == req.AliasUrl))
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
        Nombre = req.Nombre,
        AliasUrl = req.AliasUrl,
        TipoPlantilla = req.TipoPlantilla,
        TelefonoNotificaciones = req.TelefonoNotificaciones,
        DatosBancarios = req.DatosBancarios,
        Email = req.Email,
        PasswordHash = HashPassword(req.Password),
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
                "Si no lo pediste vos, podés ignorar este mensaje.");
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
    var tokenValido = await context.PasswordResetTokens.FirstOrDefaultAsync(t => t.Token == req.Token);
    if (tokenValido is null || tokenValido.Usado || tokenValido.FechaExpiracion < DateTime.UtcNow)
        return Results.BadRequest(new { mensaje = "El link para restablecer la contraseña es inválido o venció." });

    var comercio = await context.Comercios.FindAsync(tokenValido.ComercioId);
    if (comercio is null)
        return Results.BadRequest(new { mensaje = "El link para restablecer la contraseña es inválido o venció." });

    comercio.PasswordHash = HashPassword(req.NuevaPassword);
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

    comercio.FechaProximoPago = CalcularProximoPago(comercio.CicloFacturacion);
    comercio.Activo = true;
    comercio.FechaActivacion ??= AhoraArgentina();
    await context.SaveChangesAsync();

    return Results.Ok(new ComercioDto(comercio.Id, comercio.Nombre, comercio.AliasUrl, comercio.TipoPlantilla,
        comercio.TelefonoNotificaciones, comercio.DatosBancarios, comercio.Email, comercio.Activo, comercio.PlanActual, comercio.FechaProximoPago, comercio.MontoMensualAcordado, comercio.CicloFacturacion));
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

    context.Turnos.RemoveRange(context.Turnos.Where(t => t.ComercioId == id));
    context.Servicios.RemoveRange(context.Servicios.Where(s => s.ComercioId == id));
    context.Horarios.RemoveRange(context.Horarios.Where(h => h.ComercioId == id));
    context.Profesionales.RemoveRange(context.Profesionales.Where(p => p.ComercioId == id));
    context.Sucursales.RemoveRange(context.Sucursales.Where(s => s.ComercioId == id));
    context.WhatsAppConfigs.RemoveRange(context.WhatsAppConfigs.Where(w => w.ComercioId == id));
    context.PasswordResetTokens.RemoveRange(context.PasswordResetTokens.Where(t => t.ComercioId == id));
    context.Comercios.Remove(comercio);

    await context.SaveChangesAsync();
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

    return Results.Ok(new ComercioDetalleDto(
        comercio.Id, comercio.Nombre, comercio.AliasUrl, comercio.TipoPlantilla, comercio.PlanActual, comercio.CicloFacturacion,
        comercio.MontoMensualAcordado, comercio.FechaAlta, comercio.FechaProximoPago, comercio.Activo,
        profesionales.Count, sucursales.Count,
        turnosHistoricosTotal, turnosDelMes, facturacionHistorica,
        sucursales, profesionales, turnosPorSemana));
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
        comercio.Id, comercio.Nombre, comercio.AliasUrl, comercio.TipoPlantilla, comercio.TelefonoNotificaciones, comercio.LogoUrl, whatsAppActivo));
});

// Autogestión de plan: el propio dueño del comercio cambia su plan y ciclo de facturación
// desde el panel (sin proceso de pago automático todavía; el cobro se sigue gestionando a mano).
app.MapPatch("/api/comercios/{comercioId:int}/mi-plan", async (AppDbContext context, int comercioId, MiPlanRequest req, ClaimsPrincipal user) =>
{
    if (ComercioIdDelToken(user) != comercioId) return Results.Forbid();

    if (!PlanesValidos.Contains(req.PlanActual))
        return Results.BadRequest(new { mensaje = "Plan inválido. Tiene que ser Gratuito, Basico o Premium." });

    if (req.CicloFacturacion != "Mensual" && req.CicloFacturacion != "Anual")
        return Results.BadRequest(new { mensaje = "El ciclo de facturación tiene que ser Mensual o Anual." });

    var comercio = await context.Comercios.FindAsync(comercioId);
    if (comercio is null) return Results.NotFound();

    comercio.PlanActual = req.PlanActual;
    comercio.CicloFacturacion = req.CicloFacturacion;
    comercio.FechaProximoPago = CalcularProximoPago(comercio.CicloFacturacion);
    await context.SaveChangesAsync();

    return Results.Ok(new MiPlanDto(comercio.PlanActual, comercio.CicloFacturacion, comercio.FechaProximoPago));
}).RequireAuthorization("AdminCliente");

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

    if (req.PasswordNueva.Length < 6)
        return Results.BadRequest(new { mensaje = "La contraseña nueva tiene que tener al menos 6 caracteres." });

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

    var carpeta = Path.Combine(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"), "uploads", "logos");
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

    context.Servicios.Add(servicio);
    await context.SaveChangesAsync();
    return Results.Created($"/api/servicios/{servicio.Id}", servicio);
}).RequireAuthorization("AdminCliente");

app.MapGet("/api/servicios", async (AppDbContext context, int? comercioId) =>
{
    var query = context.Servicios.Where(s => s.Activo);
    if (comercioId is not null)
        query = query.Where(s => s.ComercioId == comercioId);

    return Results.Ok(await query.ToListAsync());
});

app.MapPut("/api/servicios/{id:int}", async (AppDbContext context, int id, EditarServicioRequest req, ClaimsPrincipal user) =>
{
    var servicio = await context.Servicios.FindAsync(id);
    if (servicio is null) return Results.NotFound();
    if (ComercioIdDelToken(user) != servicio.ComercioId) return Results.Forbid();

    if (string.IsNullOrWhiteSpace(req.Nombre))
        return Results.BadRequest(new { mensaje = "El nombre del servicio no puede estar vacío." });
    if (req.DuracionMinutos <= 0)
        return Results.BadRequest(new { mensaje = "La duración tiene que ser mayor a 0." });

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

    var profesional = new Profesional { ComercioId = comercioId, Nombre = req.Nombre, SucursalId = req.SucursalId };
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

    var profesional = await context.Profesionales.FindAsync(id);
    if (profesional is null) return Results.NotFound();
    if (ComercioIdDelToken(user) != profesional.ComercioId) return Results.Forbid();

    profesional.Nombre = req.Nombre.Trim();
    profesional.SucursalId = req.SucursalId;
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

    var slots = new List<SlotDisponibilidad>();
    foreach (var bloque in bloques)
    {
        var cursor = fecha.ToDateTime(TimeOnly.FromTimeSpan(bloque.HoraInicio));
        var finBloque = fecha.ToDateTime(TimeOnly.FromTimeSpan(bloque.HoraFin));

        while (cursor + duracion <= finBloque)
        {
            var finSlot = cursor + duracion;
            var chocaConOcupado = ocupados.Any(t => t.FechaHoraInicio < finSlot && cursor < t.FechaHoraFin);

            slots.Add(new SlotDisponibilidad(cursor, finSlot, !chocaConOcupado));
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
    var servicio = await context.Servicios.FindAsync(req.ServicioId);
    if (servicio is null || servicio.ComercioId != req.ComercioId)
        return Results.NotFound(new { mensaje = "El servicio no pertenece a este comercio." });

    var comercio = await context.Comercios.FindAsync(req.ComercioId);
    if (comercio is null) return Results.NotFound();
    if (!comercio.Activo) return ResultadoComercioInactivo();

    if (req.ProfesionalId is not null && !await context.Profesionales.AnyAsync(p => p.Id == req.ProfesionalId && p.ComercioId == req.ComercioId))
        return Results.NotFound(new { mensaje = "El profesional no pertenece a este comercio." });

    var finTurno = req.FechaHoraInicio + TimeSpan.FromMinutes(servicio.DuracionMinutos);

    var diaSemana = (int)req.FechaHoraInicio.DayOfWeek;
    var bloques = await ObtenerBloquesHorario(context, req.ComercioId, req.ProfesionalId, diaSemana);
    if (!EstaDentroDeAlgunHorario(bloques, req.FechaHoraInicio, finTurno))
        return Results.Conflict(new { mensaje = "Ese horario ya no está disponible. Elegí otro." });

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
        ClienteNombre = req.ClienteNombre,
        ClienteWhatsApp = req.ClienteWhatsApp,
        ClienteEmail = req.ClienteEmail,
        EstadoReserva = 1,
        FechaCreacion = DateTime.UtcNow,
        MontoCobrado = servicio.Precio
    };

    context.Turnos.Add(turno);
    await context.SaveChangesAsync();

    if (!string.IsNullOrWhiteSpace(req.ClienteEmail))
    {
        // El turno YA está guardado en este punto — lo que pase con el mail de acá en más
        // no puede hacer fallar la respuesta al cliente. Se dispara sin esperarlo (fire-and-
        // forget) para que ni siquiera lo demore: si el envío tarda o el SMTP no responde
        // (con el timeout ya puesto en EnviarEmail, como mucho 8s), el cliente ya tiene su
        // confirmación hace rato. Se capturan los valores que hacen falta antes de arrancar
        // la tarea de fondo porque "req"/"comercio"/"servicio" pueden no seguir vivos igual
        // de simple una vez que termina este request.
        var clienteEmail = req.ClienteEmail;
        var clienteNombre = req.ClienteNombre;
        var fechaHoraInicio = req.FechaHoraInicio;
        var fechaHoraFin = finTurno;
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

                await EnviarEmail(config, logger, clienteEmail, $"Turno pre-reservado en {nombreComercio}",
                    $"Hola {clienteNombre},\n\n" +
                    $"Tu turno para \"{nombreServicio}\" en {nombreComercio} quedó pre-reservado para el {fechaTexto}.\n\n" +
                    "En breve te van a escribir por WhatsApp para coordinar la seña. Tenés 2 horas para confirmar antes de que el horario se libere.\n\n" +
                    $"¿No podés ir? Cancelalo acá: {linkCancelacion}\n" +
                    "(¿Necesitás cancelar? Usá el link que te llega por mail apenas confirmás la reserva.)\n\n" +
                    $"¿Querés agregarlo a tu calendario? {linkGoogleCalendar}\n" +
                    "(También te dejamos un archivo adjunto que sirve para cualquier calendario, no solo Google.)\n\n" +
                    "Gracias por reservar con Reserva2.",
                    ("turno.ics", icsBase64));
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
        var whatsAppConfig = await context.WhatsAppConfigs.FirstOrDefaultAsync(w => w.ComercioId == req.ComercioId);
        if (whatsAppConfig?.Activado == true)
        {
            // Mismo criterio que en el mail: se capturan los valores antes de arrancar la
            // tarea de fondo, y el envío es fire-and-forget con su propio try/catch — un
            // error de la API de WhatsApp (ej. plantilla todavía no aprobada) no puede
            // demorar ni hacer fallar la respuesta al cliente, que ya tiene su turno guardado.
            var clienteWhatsApp = req.ClienteWhatsApp;
            var clienteNombre = req.ClienteNombre;
            var fechaHoraInicio = req.FechaHoraInicio;
            var nombreComercio = comercio.Nombre;
            var datosBancarios = comercio.DatosBancarios;
            var montoSenia = servicio.MontoSeña;

            _ = Task.Run(async () =>
            {
                try
                {
                    var fechaTexto = fechaHoraInicio.ToString("dddd d 'de' MMMM", new System.Globalization.CultureInfo("es-AR"));
                    var horaTexto = fechaHoraInicio.ToString("HH:mm");

                    if (montoSenia is not null)
                    {
                        await EnviarPlantillaWhatsApp(config, logger, clienteWhatsApp, "confirmacion_turno_sena", new[]
                        {
                            clienteNombre,
                            nombreComercio,
                            fechaTexto,
                            horaTexto,
                            montoSenia.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
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
        MontoCobrado = servicio.Precio
    };

    context.Turnos.Add(turno);
    await context.SaveChangesAsync();

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

app.MapGet("/api/comercios/{comercioId:int}/turnos", async (AppDbContext context, int comercioId, bool incluirVencidos, ClaimsPrincipal user) =>
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
            (t.EstadoReserva == 2 && t.FechaHoraInicio > ahora)
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

app.MapPatch("/api/turnos/{id:int}/cancelar", async (AppDbContext context, int id, ClaimsPrincipal user) =>
{
    var turno = await context.Turnos.FindAsync(id);
    if (turno is null) return Results.NotFound();
    if (ComercioIdDelToken(user) != turno.ComercioId) return Results.Forbid();

    turno.EstadoReserva = 3; // Cancelado
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
        turno.FechaHoraInicio, turno.EstadoReserva));
});

app.MapPost("/api/turnos/por-token/{token}/cancelar", async (AppDbContext context, string token) =>
{
    var turno = await context.Turnos.FirstOrDefaultAsync(t => t.TokenCancelacion == token);
    if (turno is null) return Results.NotFound();

    if (turno.EstadoReserva == 3)
        return Results.BadRequest(new { mensaje = "Ese turno ya estaba cancelado." });

    turno.EstadoReserva = 3;
    await context.SaveChangesAsync();

    return Results.Ok(new { mensaje = "Tu turno fue cancelado." });
}).RequireRateLimiting("turnos");

// ¡ESTA ES LA LÍNEA MÁGICA!
// Arranca la aplicación. Todo lo que defina reglas o tipos va DEBAJO de esto.
app.Run();

// ==========================================
// DTOs (Data Transfer Objects)
// (Acá es donde tienen que ir los 'record' y 'class' para que C# 9+ no tire error CS8803)
// ==========================================
record ComercioPublicoDto(int Id, string Nombre, string AliasUrl, string TipoPlantilla, string TelefonoNotificaciones, string? LogoUrl, bool WhatsAppActivo);
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
record ComercioDetalleDto(
    int Id, string Nombre, string AliasUrl, string TipoPlantilla, string PlanActual, string CicloFacturacion,
    decimal? MontoMensualAcordado, DateTime FechaAlta, DateTime? FechaProximoPago, bool Activo,
    int CantidadProfesionales, int CantidadSucursales,
    int TurnosHistoricosTotal, int TurnosDelMes, decimal? FacturacionHistorica,
    List<Sucursal> Sucursales, List<Profesional> Profesionales, List<TurnosPorSemanaDto> TurnosPorSemana);
record RegistroRequest(string Nombre, string AliasUrl, string TipoPlantilla, string TelefonoNotificaciones, string DatosBancarios, string Email, string Password, string? PlanActual = null, string? CicloFacturacion = null, int? CantidadProfesionales = null, int? CantidadSucursales = null);
record MiPlanRequest(string PlanActual, string CicloFacturacion);
record MiPlanDto(string PlanActual, string CicloFacturacion, DateTime? FechaProximoPago);
record PerfilRequest(string Nombre, string TelefonoNotificaciones, string DatosBancarios);
record PerfilDto(string Nombre, string TelefonoNotificaciones, string DatosBancarios, string? LogoUrl);
record EditarServicioRequest(string Nombre, int DuracionMinutos, decimal Precio, decimal? MontoSeña);
record CambiarPasswordRequest(string PasswordActual, string PasswordNueva);
record TurnoPorTokenDto(int Id, string NombreComercio, string NombreServicio, DateTime FechaHoraInicio, int EstadoReserva);
record LoginRequest(string Email, string Password);
record ForgotPasswordRequest(string Email);
record ResetPasswordRequest(string Token, string NuevaPassword);
record LoginResponse(int ComercioId, string Nombre, string AliasUrl, string Token, string PlanActual, string CicloFacturacion,
    string TelefonoNotificaciones, string DatosBancarios, string? LogoUrl, DateTime? FechaProximoPago, bool Activo);
record SuperAdminLoginRequest(string Email, string Password);
record SuperAdminLoginResponse(string Token);
record ProfesionalRequest(string Nombre, int? SucursalId = null);
record SucursalRequest(string Nombre, string Direccion, string? Telefono, bool Activa = true);
record HorarioRequest(int DiaSemana, TimeSpan HoraInicio, TimeSpan HoraFin, int? ProfesionalId = null);
record SlotDisponibilidad(DateTime Inicio, DateTime Fin, bool Disponible);
record CrearTurnoRequest(int ComercioId, int ServicioId, DateTime FechaHoraInicio, string ClienteNombre, string ClienteWhatsApp, string ClienteEmail, int? ProfesionalId = null);
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
record WhatsAppConfigDto(bool Activado);
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Reserva2.Api.Data;
using Reserva2.Api.Models;
using Reserva2.Api.Utils;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Mail;
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

// Turno.FechaHoraInicio/FechaHoraFin se guardan como hora local de Argentina "pelada" (sin
// offset: el cliente manda literalmente "2026-09-14T16:00:00"), no como UTC. Argentina no
// tiene horario de verano desde 2009 y su offset es siempre UTC-3, así que para comparar
// "ahora" contra esas columnas hay que restarle 3 horas a UtcNow en vez de usarlo crudo
// (si no, un despliegue en un servidor con reloj en UTC corre el día/las franjas ~3hs).
static DateTime AhoraArgentina() => DateTime.UtcNow.AddHours(-3);

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
// Si no hay Smtp:Host configurado (ej. en desarrollo sin credenciales todavía), no falla:
// simplemente no se envía el mail y queda logueado el motivo.
async Task EnviarEmail(IConfiguration config, ILogger logger, string destinatario, string asunto, string cuerpo)
{
    var host = config["Smtp:Host"];
    if (string.IsNullOrWhiteSpace(host))
    {
        logger.LogWarning("Smtp:Host no está configurado; no se envió el email a {Destinatario} ({Asunto}).", destinatario, asunto);
        return;
    }

    using var client = new SmtpClient(host, int.Parse(config["Smtp:Port"] ?? "587"))
    {
        Credentials = new NetworkCredential(config["Smtp:User"], config["Smtp:Password"]),
        EnableSsl = true
    };

    using var mensaje = new MailMessage
    {
        From = new MailAddress(config["Smtp:From"] ?? config["Smtp:User"] ?? "no-reply@reserva2.app", "Reserva2"),
        Subject = asunto,
        Body = cuerpo,
        IsBodyHtml = false
    };
    mensaje.To.Add(destinatario);

    await client.SendMailAsync(mensaje);
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
        CicloFacturacion = cicloElegido
    };

    context.Comercios.Add(comercio);
    await context.SaveChangesAsync();

    return Results.Created($"/api/comercios/{comercio.Id}",
        new LoginResponse(comercio.Id, comercio.Nombre, comercio.AliasUrl, GenerarToken("AdminCliente", comercio.Id), comercio.PlanActual, comercio.CicloFacturacion,
            comercio.TelefonoNotificaciones, comercio.DatosBancarios, comercio.LogoUrl));
}).RequireRateLimiting("login");

app.MapPost("/api/auth/login", async (AppDbContext context, LoginRequest req) =>
{
    var comercio = await context.Comercios.FirstOrDefaultAsync(c => c.Email == req.Email);
    if (comercio is null || !VerifyPassword(req.Password, comercio.PasswordHash))
        return Results.Unauthorized();

    return Results.Ok(new LoginResponse(comercio.Id, comercio.Nombre, comercio.AliasUrl, GenerarToken("AdminCliente", comercio.Id), comercio.PlanActual, comercio.CicloFacturacion,
            comercio.TelefonoNotificaciones, comercio.DatosBancarios, comercio.LogoUrl));
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
    await context.SaveChangesAsync();

    return Results.Ok(new ComercioDto(comercio.Id, comercio.Nombre, comercio.AliasUrl, comercio.TipoPlantilla,
        comercio.TelefonoNotificaciones, comercio.DatosBancarios, comercio.Email, comercio.Activo, comercio.PlanActual, comercio.FechaProximoPago, comercio.MontoMensualAcordado, comercio.CicloFacturacion));
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

app.MapGet("/api/comercios/alias/{alias}", async (AppDbContext context, string alias) =>
{
    var comercio = await context.Comercios.FirstOrDefaultAsync(c => c.AliasUrl == alias);
    if (comercio is null) return Results.NotFound();
    if (!comercio.Activo) return ResultadoComercioInactivo();

    return Results.Ok(new ComercioPublicoDto(
        comercio.Id, comercio.Nombre, comercio.AliasUrl, comercio.TipoPlantilla, comercio.TelefonoNotificaciones, comercio.LogoUrl));
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
    await context.SaveChangesAsync();

    return Results.Ok(new MiPlanDto(comercio.PlanActual, comercio.CicloFacturacion));
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
}).RequireAuthorization("AdminCliente");

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

    var profesional = new Profesional { ComercioId = comercioId, Nombre = req.Nombre };
    context.Profesionales.Add(profesional);
    await context.SaveChangesAsync();
    return Results.Created($"/api/profesionales/{profesional.Id}", profesional);
}).RequireAuthorization("AdminCliente");

// Público: la página de reserva necesita listarlos para que el cliente elija profesional.
app.MapGet("/api/comercios/{comercioId:int}/profesionales", async (AppDbContext context, int comercioId) =>
{
    var profesionales = await context.Profesionales
        .Where(p => p.ComercioId == comercioId)
        .OrderBy(p => p.Nombre)
        .ToListAsync();
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
app.MapGet("/api/comercios/{comercioId:int}/disponibilidad", async (AppDbContext context, int comercioId, int servicioId, DateOnly fecha, int? profesionalId) =>
{
    var comercioActivo = await context.Comercios.Where(c => c.Id == comercioId).Select(c => (bool?)c.Activo).FirstOrDefaultAsync();
    if (comercioActivo is null) return Results.NotFound();
    if (comercioActivo == false) return ResultadoComercioInactivo();

    var servicio = await context.Servicios.FindAsync(servicioId);
    if (servicio is null || servicio.ComercioId != comercioId)
        return Results.NotFound(new { mensaje = "El servicio no pertenece a este comercio." });

    if (profesionalId is not null && !await context.Profesionales.AnyAsync(p => p.Id == profesionalId && p.ComercioId == comercioId))
        return Results.NotFound(new { mensaje = "El profesional no pertenece a este comercio." });

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
        try
        {
            var fechaTexto = req.FechaHoraInicio.ToString("dddd d 'de' MMMM 'a las' HH:mm", new System.Globalization.CultureInfo("es-AR"));
            var baseUrlCancelacion = config["Frontend:BaseUrl"] ?? "http://localhost:4200";
            var linkCancelacion = $"{baseUrlCancelacion}/cancelar-turno?token={turno.TokenCancelacion}";
            await EnviarEmail(config, logger, req.ClienteEmail, $"Turno pre-reservado en {comercio.Nombre}",
                $"Hola {req.ClienteNombre},\n\n" +
                $"Tu turno para \"{servicio.Nombre}\" en {comercio.Nombre} quedó pre-reservado para el {fechaTexto}.\n\n" +
                "En breve te van a escribir por WhatsApp para coordinar la seña. Tenés 2 horas para confirmar antes de que el horario se libere.\n\n" +
                $"¿No podés ir? Cancelalo acá: {linkCancelacion}\n\n" +
                "Gracias por reservar con Reserva2.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo enviar el email de confirmación de turno.");
        }

        // PUNTO DE ENGANCHE DEL BOT DE WHATSAPP (Premium, todavía sin implementar).
        //
        // Diseño ya decidido, no volver a discutirlo al implementar: UN SOLO mensaje de
        // WhatsApp por turno, no un recordatorio + una confirmación separados. Ese único
        // mensaje ES la confirmación de la reserva, y solo si el servicio tiene seña
        // configurada (servicio.MontoSeña != null) incluye el alias/monto de la seña; si
        // el servicio no tiene seña, confirma el turno sin esa parte.
        //
        // Por qué un solo mensaje: Meta cobra por mensaje de WhatsApp Business enviado.
        // Con dos mensajes por turno (recordatorio + confirmación) el margen del plan
        // Premium se comía rápido en comercios con mucho movimiento diario. Con uno solo,
        // el margen aguanta cómodo incluso con varios turnos por día.
        //
        // Cuándo dispararlo: acá mismo, justo después del mail de confirmación, si
        // comercio.PlanActual == "Premium" y el WhatsAppConfig del comercio tiene
        // Activado == true. Todavía no está conectado a la Cloud API real de Meta:
        // falta cargar las credenciales (WhatsApp Business Account, número verificado,
        // token de acceso) cuando estén disponibles.
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

    var nombresPorId = await context.Profesionales
        .Where(p => p.ComercioId == comercioId)
        .ToDictionaryAsync(p => p.Id, p => p.Nombre);

    var porProfesional = turnosDelPeriodo
        .GroupBy(t => t.ProfesionalId)
        .Select(g => new GananciaPorProfesionalDto(
            g.Key,
            g.Key is not null && nombresPorId.TryGetValue(g.Key.Value, out var nombre) ? nombre : "Sin profesional asignado",
            g.Count(),
            g.Sum(t => t.MontoCobrado ?? 0)))
        .OrderByDescending(x => x.Ingresos)
        .ToList();

    return Results.Ok(new GananciasDto(porProfesional, turnosDelPeriodo.Count, turnosDelPeriodo.Sum(t => t.MontoCobrado ?? 0)));
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
record ComercioPublicoDto(int Id, string Nombre, string AliasUrl, string TipoPlantilla, string TelefonoNotificaciones, string? LogoUrl);
record ComercioDto(int Id, string Nombre, string AliasUrl, string TipoPlantilla, string TelefonoNotificaciones,
    string DatosBancarios, string Email, bool Activo, string PlanActual, DateTime? FechaProximoPago, decimal? MontoMensualAcordado, string CicloFacturacion);
record ActualizarEstadoRequest(bool Activo);
record ActualizarPlanRequest(string PlanActual);
record ActualizarMontoAcordadoRequest(decimal? MontoMensualAcordado);
record ActualizarCicloFacturacionRequest(string CicloFacturacion);
record MetricasDto(int TotalComercios, int ComerciosActivos, int ComerciosInactivos, int TurnosDelMes);
record RegistroRequest(string Nombre, string AliasUrl, string TipoPlantilla, string TelefonoNotificaciones, string DatosBancarios, string Email, string Password, string? PlanActual = null, string? CicloFacturacion = null);
record MiPlanRequest(string PlanActual, string CicloFacturacion);
record MiPlanDto(string PlanActual, string CicloFacturacion);
record PerfilRequest(string Nombre, string TelefonoNotificaciones, string DatosBancarios);
record PerfilDto(string Nombre, string TelefonoNotificaciones, string DatosBancarios, string? LogoUrl);
record EditarServicioRequest(string Nombre, int DuracionMinutos, decimal Precio, decimal? MontoSeña);
record CambiarPasswordRequest(string PasswordActual, string PasswordNueva);
record TurnoPorTokenDto(int Id, string NombreComercio, string NombreServicio, DateTime FechaHoraInicio, int EstadoReserva);
record LoginRequest(string Email, string Password);
record ForgotPasswordRequest(string Email);
record ResetPasswordRequest(string Token, string NuevaPassword);
record LoginResponse(int ComercioId, string Nombre, string AliasUrl, string Token, string PlanActual, string CicloFacturacion,
    string TelefonoNotificaciones, string DatosBancarios, string? LogoUrl);
record SuperAdminLoginRequest(string Email, string Password);
record SuperAdminLoginResponse(string Token);
record ProfesionalRequest(string Nombre);
record HorarioRequest(int DiaSemana, TimeSpan HoraInicio, TimeSpan HoraFin, int? ProfesionalId = null);
record SlotDisponibilidad(DateTime Inicio, DateTime Fin, bool Disponible);
record CrearTurnoRequest(int ComercioId, int ServicioId, DateTime FechaHoraInicio, string ClienteNombre, string ClienteWhatsApp, string ClienteEmail, int? ProfesionalId = null);
record AdminCrearTurnoRequest(int ServicioId, DateTime FechaHoraInicio, string ClienteNombre, string? ClienteWhatsApp, string? ClienteEmail, int? ProfesionalId = null);
record HistorialItemDto(int Id, DateTime FechaHoraInicio, string ClienteNombre, string ServicioNombre, decimal Monto);
record HistorialDto(List<HistorialItemDto> Items, decimal TotalHoy, decimal TotalSemana, decimal TotalMes);
record GananciaPorProfesionalDto(int? ProfesionalId, string NombreProfesional, int CantidadTurnos, decimal Ingresos);
record GananciasDto(List<GananciaPorProfesionalDto> PorProfesional, int CantidadTotal, decimal IngresosTotal);
record WhatsAppConfigDto(bool Activado);
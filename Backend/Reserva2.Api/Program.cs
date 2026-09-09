using Microsoft.EntityFrameworkCore;
using Reserva2.Api.Data;
using Reserva2.Api.Models;
using Reserva2.Api.Utils;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);

// Configuración de la base de datos SQL Server
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// System.Text.Json no serializa TimeSpan por defecto (lo usan los Horarios).
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new TimeSpanJsonConverter());
});

// === Configuración de CORS para Angular ===
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular", policy =>
    {
        policy.WithOrigins("http://localhost:4200") // El puerto que usará Angular
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
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
app.UseCors("AllowAngular");

// ==========================================
// CONSTANTES DE NEGOCIO
// ==========================================
const int HorasLimiteParaConfirmar = 2;

bool EsPreReservaVigente(Turno t) =>
    t.EstadoReserva == 1 && t.FechaCreacion > DateTime.UtcNow.AddHours(-HorasLimiteParaConfirmar);

bool OcupaHorario(Turno t) => t.EstadoReserva == 2 || EsPreReservaVigente(t);

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
// ENDPOINTS DE AUTENTICACIÓN (panel de admin)
// ==========================================
app.MapPost("/api/auth/register", async (AppDbContext context, RegistroRequest req) =>
{
    if (await context.Comercios.AnyAsync(c => c.Email == req.Email))
        return Results.Conflict(new { mensaje = "Ya existe una cuenta con ese email." });

    if (await context.Comercios.AnyAsync(c => c.AliasUrl == req.AliasUrl))
        return Results.Conflict(new { mensaje = "Ese link ya está en uso por otro negocio." });

    var comercio = new Comercio
    {
        Nombre = req.Nombre,
        AliasUrl = req.AliasUrl,
        TipoPlantilla = req.TipoPlantilla,
        TelefonoNotificaciones = req.TelefonoNotificaciones,
        DatosBancarios = req.DatosBancarios,
        Email = req.Email,
        PasswordHash = HashPassword(req.Password)
    };

    context.Comercios.Add(comercio);
    await context.SaveChangesAsync();

    return Results.Created($"/api/comercios/{comercio.Id}",
        new LoginResponse(comercio.Id, comercio.Nombre, comercio.AliasUrl));
});

app.MapPost("/api/auth/login", async (AppDbContext context, LoginRequest req) =>
{
    var comercio = await context.Comercios.FirstOrDefaultAsync(c => c.Email == req.Email);
    if (comercio is null || !VerifyPassword(req.Password, comercio.PasswordHash))
        return Results.Unauthorized();

    return Results.Ok(new LoginResponse(comercio.Id, comercio.Nombre, comercio.AliasUrl));
});

// ==========================================
// ENDPOINTS PARA COMERCIOS
// ==========================================
app.MapGet("/api/comercios", async (AppDbContext context) =>
{
    var comercios = await context.Comercios.ToListAsync();
    return Results.Ok(comercios);
});

app.MapGet("/api/comercios/{id:int}", async (AppDbContext context, int id) =>
{
    var comercio = await context.Comercios.FindAsync(id);
    return comercio is null ? Results.NotFound() : Results.Ok(comercio);
});

app.MapGet("/api/comercios/alias/{alias}", async (AppDbContext context, string alias) =>
{
    var comercio = await context.Comercios.FirstOrDefaultAsync(c => c.AliasUrl == alias);
    if (comercio is null) return Results.NotFound();

    return Results.Ok(new ComercioPublicoDto(
        comercio.Id, comercio.Nombre, comercio.AliasUrl, comercio.TipoPlantilla, comercio.TelefonoNotificaciones));
});

// ==========================================
// ENDPOINTS PARA SERVICIOS
// ==========================================
app.MapPost("/api/servicios", async (AppDbContext context, Servicio servicio) =>
{
    context.Servicios.Add(servicio);
    await context.SaveChangesAsync();
    return Results.Created($"/api/servicios/{servicio.Id}", servicio);
});

app.MapGet("/api/servicios", async (AppDbContext context, int? comercioId) =>
{
    var query = context.Servicios.Where(s => s.Activo);
    if (comercioId is not null)
        query = query.Where(s => s.ComercioId == comercioId);

    return Results.Ok(await query.ToListAsync());
});

app.MapDelete("/api/servicios/{id:int}", async (AppDbContext context, int id) =>
{
    var servicio = await context.Servicios.FindAsync(id);
    if (servicio is null) return Results.NotFound();

    servicio.Activo = false;
    await context.SaveChangesAsync();
    return Results.NoContent();
});

// ==========================================
// ENDPOINTS PARA HORARIOS (disponibilidad semanal)
// ==========================================
app.MapGet("/api/comercios/{comercioId:int}/horarios", async (AppDbContext context, int comercioId) =>
{
    var horarios = await context.Horarios
        .Where(h => h.ComercioId == comercioId)
        .OrderBy(h => h.DiaSemana).ThenBy(h => h.HoraInicio)
        .ToListAsync();
    return Results.Ok(horarios);
});

app.MapPost("/api/comercios/{comercioId:int}/horarios", async (AppDbContext context, int comercioId, HorarioRequest req) =>
{
    var horario = new Horario
    {
        ComercioId = comercioId,
        DiaSemana = req.DiaSemana,
        HoraInicio = req.HoraInicio,
        HoraFin = req.HoraFin
    };
    context.Horarios.Add(horario);
    await context.SaveChangesAsync();
    return Results.Created($"/api/horarios/{horario.Id}", horario);
});

app.MapDelete("/api/horarios/{id:int}", async (AppDbContext context, int id) =>
{
    var horario = await context.Horarios.FindAsync(id);
    if (horario is null) return Results.NotFound();

    context.Horarios.Remove(horario);
    await context.SaveChangesAsync();
    return Results.NoContent();
});

// ==========================================
// DISPONIBILIDAD (la grilla verde/gris que ve el cliente)
// ==========================================
app.MapGet("/api/comercios/{comercioId:int}/disponibilidad", async (AppDbContext context, int comercioId, int servicioId, DateOnly fecha) =>
{
    var servicio = await context.Servicios.FindAsync(servicioId);
    if (servicio is null || servicio.ComercioId != comercioId)
        return Results.NotFound(new { mensaje = "El servicio no pertenece a este comercio." });

    var diaSemana = (int)fecha.DayOfWeek;
    var bloques = await context.Horarios
        .Where(h => h.ComercioId == comercioId && h.DiaSemana == diaSemana)
        .ToListAsync();

    if (bloques.Count == 0)
        return Results.Ok(Array.Empty<SlotDisponibilidad>());

    var inicioDia = fecha.ToDateTime(TimeOnly.MinValue);
    var finDia = fecha.ToDateTime(TimeOnly.MaxValue);

    var turnosDelDia = await context.Turnos
        .Where(t => t.ComercioId == comercioId && t.FechaHoraInicio < finDia && t.FechaHoraFin > inicioDia)
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
app.MapPost("/api/turnos", async (AppDbContext context, CrearTurnoRequest req) =>
{
    var servicio = await context.Servicios.FindAsync(req.ServicioId);
    if (servicio is null || servicio.ComercioId != req.ComercioId)
        return Results.NotFound(new { mensaje = "El servicio no pertenece a este comercio." });

    var finTurno = req.FechaHoraInicio + TimeSpan.FromMinutes(servicio.DuracionMinutos);

    var turnosQueChocan = await context.Turnos
        .Where(t => t.ComercioId == req.ComercioId
                 && t.FechaHoraInicio < finTurno
                 && t.FechaHoraFin > req.FechaHoraInicio)
        .ToListAsync();

    if (turnosQueChocan.Any(OcupaHorario))
        return Results.Conflict(new { mensaje = "Ese horario ya no está disponible. Elegí otro." });

    var turno = new Turno
    {
        ComercioId = req.ComercioId,
        ServicioId = req.ServicioId,
        FechaHoraInicio = req.FechaHoraInicio,
        FechaHoraFin = finTurno,
        ClienteNombre = req.ClienteNombre,
        ClienteWhatsApp = req.ClienteWhatsApp,
        EstadoReserva = 1,
        FechaCreacion = DateTime.UtcNow
    };

    context.Turnos.Add(turno);
    await context.SaveChangesAsync();

    return Results.Created($"/api/turnos/{turno.Id}", turno);
});

app.MapGet("/api/comercios/{comercioId:int}/turnos", async (AppDbContext context, int comercioId, bool incluirVencidos) =>
{
    var turnos = await context.Turnos
        .Where(t => t.ComercioId == comercioId)
        .OrderBy(t => t.FechaHoraInicio)
        .ToListAsync();

    if (!incluirVencidos)
        turnos = turnos.Where(t => t.EstadoReserva != 1 || EsPreReservaVigente(t)).ToList();

    return Results.Ok(turnos);
});

app.MapGet("/api/turnos", async (AppDbContext context) =>
{
    var turnos = await context.Turnos.ToListAsync();
    return Results.Ok(turnos);
});

app.MapPatch("/api/turnos/{id:int}/confirmar", async (AppDbContext context, int id) =>
{
    var turno = await context.Turnos.FindAsync(id);
    if (turno is null) return Results.NotFound();

    turno.EstadoReserva = 2; // Confirmado
    await context.SaveChangesAsync();
    return Results.Ok(turno);
});

app.MapPatch("/api/turnos/{id:int}/cancelar", async (AppDbContext context, int id) =>
{
    var turno = await context.Turnos.FindAsync(id);
    if (turno is null) return Results.NotFound();

    turno.EstadoReserva = 3; // Cancelado
    await context.SaveChangesAsync();
    return Results.Ok(turno);
});

// ¡ESTA ES LA LÍNEA MÁGICA! 
// Arranca la aplicación. Todo lo que defina reglas o tipos va DEBAJO de esto.
app.Run();

// ==========================================
// DTOs (Data Transfer Objects)
// (Acá es donde tienen que ir los 'record' y 'class' para que C# 9+ no tire error CS8803)
// ==========================================
record ComercioPublicoDto(int Id, string Nombre, string AliasUrl, string TipoPlantilla, string TelefonoNotificaciones);
record RegistroRequest(string Nombre, string AliasUrl, string TipoPlantilla, string TelefonoNotificaciones, string DatosBancarios, string Email, string Password);
record LoginRequest(string Email, string Password);
record LoginResponse(int ComercioId, string Nombre, string AliasUrl);
record HorarioRequest(int DiaSemana, TimeSpan HoraInicio, TimeSpan HoraFin);
record SlotDisponibilidad(DateTime Inicio, DateTime Fin, bool Disponible);
record CrearTurnoRequest(int ComercioId, int ServicioId, DateTime FechaHoraInicio, string ClienteNombre, string ClienteWhatsApp);
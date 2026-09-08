using Microsoft.EntityFrameworkCore;
using Reserva2.Api.Data;
using Reserva2.Api.Models;

var builder = WebApplication.CreateBuilder(args);

// Configuración de la base de datos SQL Server
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// === NUEVO: Configuración de CORS para Angular ===
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
// ENDPOINTS PARA COMERCIOS
// ==========================================
app.MapPost("/api/comercios", async (AppDbContext context, Comercio comercio) =>
{
    context.Comercios.Add(comercio);
    await context.SaveChangesAsync();
    return Results.Created($"/api/comercios/{comercio.Id}", comercio);
});

app.MapGet("/api/comercios", async (AppDbContext context) =>
{
    var comercios = await context.Comercios.ToListAsync();
    return Results.Ok(comercios);
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

app.MapGet("/api/servicios", async (AppDbContext context) =>
{
    var servicios = await context.Servicios.ToListAsync();
    return Results.Ok(servicios);
});

// ==========================================
// ENDPOINTS PARA TURNOS
// ==========================================
app.MapPost("/api/turnos", async (AppDbContext context, Turno turno) =>
{
    context.Turnos.Add(turno);
    await context.SaveChangesAsync();
    return Results.Created($"/api/turnos/{turno.Id}", turno);
});

app.MapGet("/api/turnos", async (AppDbContext context) =>
{
    var turnos = await context.Turnos.ToListAsync();
    return Results.Ok(turnos);
});

app.Run();
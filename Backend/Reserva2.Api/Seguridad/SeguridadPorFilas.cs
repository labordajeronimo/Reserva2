using System.Data.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Reserva2.Api.Seguridad;

// ==========================================
// SEGURIDAD POR FILAS (Row-Level Security de SQL Server)
// ==========================================
// Segunda barrera, en la base, además del chequeo "¿este recurso es de tu comercio?" que ya
// hace cada endpoint. En los pedidos del panel de un comercio, cada conexión a SQL Server
// arranca con SESSION_CONTEXT('ComercioId') = el comercio del token, y la política
// seguridad.PoliticaComercio (migración AgregarSeguridadPorFilas) hace que esa conexión:
//   - solo VEA filas de ese comercio (las de otros directamente no existen para ella), y
//   - no pueda INSERTAR ni MOVER filas hacia otro comercio.
// Si algún endpoint se olvidara del chequeo, la base igual no le devuelve datos ajenos.
//
// Los pedidos públicos (página de reserva, links de cancelación, webhooks de Mercado Pago),
// el login y el Super Admin no fijan comercio: ven todo, como hasta ahora, porque por
// naturaleza cruzan comercios (buscar por alias, por token, listar todos).

// Comercio del pedido actual (scoped: uno por request). Null = sin restricción por fila.
public sealed class ContextoComercio
{
    public int? ComercioId { get; private set; }

    public void Fijar(int comercioId)
    {
        // Una vez fijado no se puede cambiar a otro comercio dentro del mismo pedido.
        if (ComercioId is not null && ComercioId != comercioId)
            throw new InvalidOperationException("El comercio del pedido ya estaba fijado.");
        ComercioId = comercioId;
    }
}

// Al abrir cada conexión, si el pedido es de un comercio, se lo pasa a SQL Server con
// @read_only = 1 (nada de lo que corra después en esa conexión lo puede cambiar). El pool
// de conexiones lo limpia solo (sp_reset_connection) cuando la conexión vuelve a usarse.
public sealed class InterceptorSeguridadPorFilas(ContextoComercio contexto) : DbConnectionInterceptor
{
    private const string Sql = "EXEC sp_set_session_context @key = N'ComercioId', @value = @comercioId, @read_only = 1;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (contexto.ComercioId is not { } comercioId) return;
        using var comando = CrearComando(connection, comercioId);
        comando.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (contexto.ComercioId is not { } comercioId) return;
        await using var comando = CrearComando(connection, comercioId);
        await comando.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DbCommand CrearComando(DbConnection connection, int comercioId)
    {
        var comando = connection.CreateCommand();
        comando.CommandText = Sql;
        comando.Parameters.Add(new SqlParameter("@comercioId", System.Data.SqlDbType.Int) { Value = comercioId });
        return comando;
    }
}

public static class SeguridadPorFilasExtensions
{
    // Va después de UseAuthorization: para entonces el token ya se validó y se sabe qué
    // política pide el endpoint. Solo los endpoints del panel del comercio (política
    // "AdminCliente") quedan restringidos a su comercio.
    public static IApplicationBuilder UseSeguridadPorFilas(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            var endpoint = http.GetEndpoint();
            var esDelPanel = endpoint?.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy == "AdminCliente") == true;
            if (esDelPanel && int.TryParse(http.User.FindFirst("comercioId")?.Value, out var comercioId))
                http.RequestServices.GetRequiredService<ContextoComercio>().Fijar(comercioId);
            await next();
        });
}

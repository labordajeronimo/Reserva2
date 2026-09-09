using System.Text.Json;
using System.Text.Json.Serialization;

namespace Reserva2.Api.Utils
{
    // System.Text.Json no trae soporte nativo para TimeSpan (a diferencia de
    // DateOnly/TimeOnly, que sí lo tienen desde .NET 6). Los Horarios usan
    // TimeSpan para HoraInicio/HoraFin, así que sin este converter la API
    // tira una excepción al serializar o deserializar esos campos.
    // Acepta "HH:mm" o "HH:mm:ss" desde el frontend, y siempre devuelve "HH:mm:ss".
    public class TimeSpanJsonConverter : JsonConverter<TimeSpan>
    {
        public override TimeSpan Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var valor = reader.GetString();
            return TimeSpan.TryParse(valor, out var resultado)
                ? resultado
                : throw new JsonException($"No se pudo interpretar '{valor}' como una hora (usá HH:mm).");
        }

        public override void Write(Utf8JsonWriter writer, TimeSpan value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString(@"hh\:mm\:ss"));
        }
    }
}
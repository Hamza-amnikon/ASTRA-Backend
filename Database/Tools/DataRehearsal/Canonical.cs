using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Astra.Migration;

public static class Canonical
{
    public static object? FromSource(JsonElement value, Column column)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        return column.Kind switch
        {
            "uuid" => Guid.Parse(value.GetString()!).ToString("D"),
            "text" or "inet" => value.GetString(),
            "bool" => value.GetBoolean(),
            "int2" or "int4" or "int8" => value.GetInt64(),
            "timestamptz" => Timestamp(value.GetString()!),
            "date" => DateTime.ParseExact(value.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            "time" => ParseTime(value.GetString()!),
            "bytea" => Binary(value.GetString()!),
            "jsonb" or "_text" or "_int2" => value.GetRawText(),
            _ => throw new InvalidOperationException("Unsupported source type: " + column.Kind),
        };
    }

    private static byte[] Binary(string text)
    {
        if (!text.StartsWith("\\x", StringComparison.Ordinal)) throw new InvalidOperationException("Unsupported binary encoding.");
        return Convert.FromHexString(text[2..]);
    }

    private static DateTime Timestamp(string text)
    {
        var value = DateTimeOffset.Parse(text, CultureInfo.InvariantCulture).UtcDateTime;
        if (value.Year < 1000 || value.Ticks % 10 != 0) throw new InvalidOperationException("Timestamp cannot be represented without loss.");
        return value;
    }

    private static TimeSpan ParseTime(string text)
    {
        var parts = text.Split(':');
        if (parts.Length != 3) throw new InvalidOperationException("Unsupported source time.");
        var hours = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var minutes = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var seconds = decimal.Parse(parts[2], CultureInfo.InvariantCulture);
        return TimeSpan.FromTicks(checked((long)((hours * 3600m + minutes * 60m + seconds) * TimeSpan.TicksPerSecond)));
    }

    public static byte[] RowHash(Table table, object?[] values)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            for (var i = 0; i < table.Columns.Length; i++)
            {
                var column = table.Columns[i];
                var value = values[i];
                writer.WriteStartArray();
                writer.WriteStringValue(column.Name);
                writer.WriteStringValue(column.Kind);
                if (value is null or DBNull) writer.WriteNullValue();
                else if (column.Kind is "jsonb" or "_text" or "_int2")
                {
                    using var json = JsonDocument.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!);
                    WriteJson(writer, json.RootElement);
                }
                else if (column.Kind == "bool") writer.WriteBooleanValue(Convert.ToBoolean(value, CultureInfo.InvariantCulture));
                else if (column.Kind is "int2" or "int4" or "int8")
                    writer.WriteStringValue(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                else if (column.Kind == "timestamptz")
                    writer.WriteStringValue(((DateTime)value).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture));
                else if (column.Kind == "date")
                    writer.WriteStringValue(((DateTime)value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                else if (column.Kind == "time") writer.WriteStringValue(((TimeSpan)value).Ticks.ToString(CultureInfo.InvariantCulture));
                else if (column.Kind == "bytea") writer.WriteStringValue(Convert.ToHexString((byte[])value));
                else writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        return SHA256.HashData(buffer.ToArray());
    }

    private static void WriteJson(Utf8JsonWriter writer, JsonElement value)
    {
        writer.WriteStartArray();
        writer.WriteStringValue(value.ValueKind.ToString());
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartArray();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WriteStartArray();
                    writer.WriteStringValue(property.Name);
                    WriteJson(writer, property.Value);
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteJson(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String: writer.WriteStringValue(value.GetString()); break;
            case JsonValueKind.Number: writer.WriteStringValue(Number(value.GetRawText())); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new InvalidOperationException("Unsupported JSON value.");
        }
        writer.WriteEndArray();
    }

    // Canonicalize decimals with string/BigInteger arithmetic, never double.
    private static string Number(string text)
    {
        var negative = text.StartsWith('-');
        if (negative) text = text[1..];
        var exponentParts = text.Split(new[] { 'e', 'E' });
        var exponent = exponentParts.Length > 1 ? BigInteger.Parse(exponentParts[1], CultureInfo.InvariantCulture) : BigInteger.Zero;
        var point = exponentParts[0].IndexOf('.');
        if (point >= 0) exponent -= exponentParts[0].Length - point - 1;
        var coefficient = exponentParts[0].Replace(".", "").TrimStart('0');
        if (coefficient.Length == 0) return "0";
        var trimmed = coefficient.TrimEnd('0');
        exponent += coefficient.Length - trimmed.Length;
        return (negative ? "-" : "") + trimmed + "e" + exponent.ToString(CultureInfo.InvariantCulture);
    }
}

public sealed class RowDigest
{
    private static readonly BigInteger Modulus = BigInteger.One << 256;
    private BigInteger sum;
    public long Count { get; private set; }
    public void Add(byte[] digest)
    {
        sum = (sum + new BigInteger(digest, isUnsigned: true, isBigEndian: true)) % Modulus;
        Count++;
    }
    public string Checksum
    {
        get
        {
            var bytes = sum.ToByteArray(isUnsigned: true, isBigEndian: true);
            var padded = new byte[32];
            bytes.CopyTo(padded, padded.Length - bytes.Length);
            return Convert.ToHexString(padded).ToLowerInvariant();
        }
    }
}

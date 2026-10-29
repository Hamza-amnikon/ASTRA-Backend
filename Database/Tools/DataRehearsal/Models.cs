using System.Text.Json.Serialization;

namespace Astra.Migration;

public sealed record Column(
    [property: JsonPropertyName("column_name")] string Name,
    [property: JsonPropertyName("udt_name")] string Kind,
    [property: JsonPropertyName("is_nullable")] string Nullable);
public sealed record Table(string Name, Column[] Columns, string[] PrimaryKey);
public sealed record Sequence(string Name, [property: JsonPropertyName("last_value")] string? LastValue);
public sealed record Header(int Format, int SchemaVersion, bool SourceReadOnly, Table[] Tables, Sequence[] Sequences);
public sealed record Trailer(string Sha256, Dictionary<string, long> Counts, int AttachmentCount);
public sealed record Attachment(string Key, string Sha256, long Size);

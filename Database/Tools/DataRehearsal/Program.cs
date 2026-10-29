using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Astra.Migration;
using Astra.Tools;
using MySqlConnector;
using Microsoft.Extensions.Configuration;

var persist = args.Length == 3 && args[2] == "--persist";
var apiSmoke = args.Length == 3 && args[2] == "--api-smoke";
if (args.Length is < 2 or > 3 || (args.Length == 3 && !persist && !apiSmoke))
{
    Console.Error.WriteLine("Usage: DataRehearsal <migration-root> <private-snapshot-file> [--persist|--api-smoke]");
    return 1;
}
var root = Path.GetFullPath(args[0]);
var databaseRoot = Directory.Exists(Path.Combine(root, "Database"))
    ? Path.Combine(root, "Database") : Path.Combine(root, "apps", "api", "Database");
var snapshot = Path.GetFullPath(args[1]);
var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
var options = RehearsalConnection.Load(root);
options.Pooling = false;
var database = persist ? "astra" : "astra_data_rehearsal_" + Guid.NewGuid().ToString("N");
var filesRoot = persist ? Path.Combine(root, "storage") : Path.Combine(root, "artifacts", database);
await using var connection = new MySqlConnection(options.ConnectionString);
MySqlTransaction? transaction = null;
bool created = false, filesCreated = false;
string stage = "prepare isolated rehearsal", currentTable = "";
object? report = null;
int exitCode = 0;
try
{
    using var sourceSchema = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "migration/source-schema.json")));
    using var coverage = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(databaseRoot, "Migrations/coverage.json")));
    var bounds = coverage.RootElement.GetProperty("textBounds").EnumerateArray()
        .ToDictionary(item => item.GetProperty("table").GetString()! + "." + item.GetProperty("column").GetString()!,
            item => item.GetProperty("maximumCharacters").GetInt32());
    var metadata = sourceSchema.RootElement.GetProperty("tables").EnumerateArray()
        .ToDictionary(item => item.GetProperty("name").GetString()!, item => item.Clone());
    await connection.OpenAsync();
    if (persist)
    {
        await using var schemaExists = new MySqlCommand("SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name=@db", connection);
        schemaExists.Parameters.AddWithValue("@db", database);
        if (Convert.ToInt32(await schemaExists.ExecuteScalarAsync(), CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("The persistent destination database does not exist.");
        await using var existing = new MySqlCommand("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=@db AND table_type='BASE TABLE'", connection);
        existing.Parameters.AddWithValue("@db", database);
        if (Convert.ToInt32(await existing.ExecuteScalarAsync(), CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException("The persistent destination is not empty.");
        if (Directory.Exists(filesRoot))
            throw new InvalidOperationException("The destination attachment directory already exists.");
    }
    else
        await Execute($"CREATE DATABASE {Q(database)} CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_as_cs");
    created = true;
    await connection.ChangeDatabaseAsync(database);
    stage = "create rehearsal schema";
    foreach (var sql in SqlScript.Statements(Path.Combine(databaseRoot, "Migrations/001_source_schema.sql"))) await Execute(sql);
    Directory.CreateDirectory(filesRoot);
    filesCreated = true;
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(filesRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    await Execute("SET SESSION FOREIGN_KEY_CHECKS = 0");
    transaction = await connection.BeginTransactionAsync();
    using var snapshotHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    await using var input = File.OpenRead(snapshot);
    await using var gzip = new GZipStream(input, CompressionMode.Decompress);
    using var reader = new StreamReader(gzip, new UTF8Encoding(false, true));
    Header? header = null;
    Trailer? trailer = null;
    var tables = new Dictionary<string, Table>();
    var digests = new Dictionary<string, RowDigest>();
    var expectedAttachments = new Dictionary<Guid, Attachment>();
    var copiedAttachmentIds = new HashSet<Guid>();
    while (await reader.ReadLineAsync() is { } line)
    {
        using var record = JsonDocument.Parse(line);
        var value = record.RootElement;
        var kind = value.GetProperty("kind").GetString();
        if (trailer is not null) throw new InvalidOperationException("Data appears after the snapshot trailer.");
        if (kind == "snapshot-end")
        {
            trailer = JsonSerializer.Deserialize<Trailer>(line, jsonOptions)!;
            continue;
        }
        snapshotHash.AppendData(Encoding.UTF8.GetBytes(line + "\n"));
        if (kind == "snapshot-start")
        {
            if (header is not null) throw new InvalidOperationException("Duplicate snapshot header.");
            header = JsonSerializer.Deserialize<Header>(line, jsonOptions)!;
            if (header.Format != 1 || header.SchemaVersion != 25 || !header.SourceReadOnly || header.Tables.Length != 70)
                throw new InvalidOperationException("Unexpected snapshot metadata.");
            foreach (var table in header.Tables)
            {
                Q(table.Name);
                if (!metadata.TryGetValue(table.Name, out var expected)) throw new InvalidOperationException("Unexpected source table.");
                var columns = expected.GetProperty("columns").EnumerateArray().ToArray();
                if (columns.Length != table.Columns.Length || table.PrimaryKey.Length == 0)
                    throw new InvalidOperationException("Source schema shape changed.");
                for (var i = 0; i < columns.Length; i++)
                {
                    Q(table.Columns[i].Name);
                    if (columns[i].GetProperty("column_name").GetString() != table.Columns[i].Name ||
                        columns[i].GetProperty("udt_name").GetString() != table.Columns[i].Kind)
                        throw new InvalidOperationException("Source column metadata changed.");
                }
                tables.Add(table.Name, table);
                digests.Add(table.Name, new RowDigest());
            }
        }
        else if (kind == "row")
        {
            stage = "import source row";
            currentTable = value.GetProperty("table").GetString()!;
            if (!tables.TryGetValue(currentTable, out var table)) throw new InvalidOperationException("Unknown source table.");
            var row = value.GetProperty("row");
            if (row.EnumerateObject().Count() != table.Columns.Length) throw new InvalidOperationException("Source row shape changed.");
            var values = new object?[table.Columns.Length];
            for (var i = 0; i < values.Length; i++)
            {
                var column = table.Columns[i];
                values[i] = Canonical.FromSource(row.GetProperty(column.Name), column);
                if (values[i] is string text && bounds.TryGetValue(table.Name + "." + column.Name, out var maximum) &&
                    text.EnumerateRunes().Count() > maximum)
                    throw new InvalidOperationException("Indexed source text exceeds the proposed MySQL bound; schema widening is required.");
            }
            await using var insert = new MySqlCommand("INSERT INTO " + Q(table.Name) + " (" +
                string.Join(", ", table.Columns.Select(column => Q(column.Name))) + ") VALUES (" +
                string.Join(", ", Enumerable.Range(0, values.Length).Select(i => "@p" + i)) + ")", connection, transaction);
            for (var i = 0; i < values.Length; i++) insert.Parameters.AddWithValue("p" + i, values[i] ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync();
            digests[table.Name].Add(Canonical.RowHash(table, values));
            if (table.Name == "ticket_attachments")
            {
                var id = Guid.Parse(row.GetProperty("id").GetString()!);
                expectedAttachments.Add(id, new Attachment(row.GetProperty("storage_key").GetString()!,
                    row.GetProperty("sha256").GetString()!.ToLowerInvariant(), row.GetProperty("size_bytes").GetInt64()));
            }
        }
        else if (kind == "attachment")
        {
            stage = "copy and verify ticket attachment";
            var id = Guid.Parse(value.GetProperty("id").GetString()!);
            if (!expectedAttachments.TryGetValue(id, out var expected) || !copiedAttachmentIds.Add(id))
                throw new InvalidOperationException("Unmatched or duplicate attachment bytes.");
            var bytes = Convert.FromBase64String(value.GetProperty("contentBase64").GetString()!);
            if (bytes.LongLength != expected.Size || bytes.LongLength != value.GetProperty("sizeBytes").GetInt64() ||
                value.GetProperty("storageKey").GetString() != expected.Key ||
                value.GetProperty("sha256").GetString()!.ToLowerInvariant() != expected.Sha256 ||
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != expected.Sha256)
                throw new InvalidOperationException("Source attachment metadata or digest mismatch.");
            var target = persist ? ResolveStoragePath(filesRoot, expected.Key) : Path.Combine(filesRoot, id.ToString("D") + ".bin");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.GetDirectoryName(target)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await output.WriteAsync(bytes);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await using var saved = File.OpenRead(target);
            if (Convert.ToHexString(await SHA256.HashDataAsync(saved)).ToLowerInvariant() != expected.Sha256)
                throw new InvalidOperationException("Copied attachment digest mismatch.");
        }
        else throw new InvalidOperationException("Unfinished or unknown snapshot record.");
    }
    stage = "verify snapshot integrity";
    currentTable = "";
    if (header is null || trailer is null ||
        Convert.ToHexString(snapshotHash.GetHashAndReset()).ToLowerInvariant() != trailer.Sha256 ||
        copiedAttachmentIds.Count != trailer.AttachmentCount || copiedAttachmentIds.Count != expectedAttachments.Count ||
        digests.Any(entry => !trailer.Counts.TryGetValue(entry.Key, out var count) || count != entry.Value.Count))
        throw new InvalidOperationException("Snapshot integrity or record-count mismatch.");
    var tableResults = new List<object>();
    foreach (var table in header.Tables)
    {
        stage = "compare imported table checksum";
        currentTable = table.Name;
        var imported = new RowDigest();
        await using var query = new MySqlCommand("SELECT " + string.Join(", ", table.Columns.Select(column => Q(column.Name))) +
            " FROM " + Q(table.Name), connection, transaction);
        await using (var rows = await query.ExecuteReaderAsync())
        {
            while (await rows.ReadAsync())
            {
                var values = new object?[table.Columns.Length];
                for (var i = 0; i < values.Length; i++) values[i] = rows.IsDBNull(i) ? null : rows.GetValue(i);
                imported.Add(Canonical.RowHash(table, values));
            }
        }
        var expected = digests[table.Name];
        if (imported.Count != expected.Count || imported.Checksum != expected.Checksum)
            throw new InvalidOperationException("Normalized table digest differs from the source snapshot.");
        tableResults.Add(new { table = table.Name, rows = imported.Count, normalizedSha256Sum = imported.Checksum });
    }
    stage = "verify foreign-key relationships";
    currentTable = "";
    var foreignKeys = 0;
    foreach (var table in metadata)
    {
        foreach (var constraint in table.Value.GetProperty("constraints").EnumerateArray())
        {
            if (constraint.GetProperty("type").GetString() != "f") continue;
            var match = Regex.Match(constraint.GetProperty("definition").GetString()!,
                @"^FOREIGN KEY \(([^)]+)\) REFERENCES (\w+)\(([^)]+)\)");
            if (!match.Success) throw new InvalidOperationException("Unrecognized foreign-key metadata.");
            var local = match.Groups[1].Value.Split(", ");
            var parent = match.Groups[2].Value;
            var remote = match.Groups[3].Value.Split(", ");
            if (!tables.ContainsKey(parent) || local.Length != remote.Length) throw new InvalidOperationException("Invalid reference metadata.");
            var predicate = string.Join(" AND ", local.Select(column => "child." + Q(column) + " IS NOT NULL"));
            var equality = string.Join(" AND ", local.Zip(remote).Select(pair => "child." + Q(pair.First) + " = parent." + Q(pair.Second)));
            await using var query = new MySqlCommand("SELECT COUNT(*) FROM " + Q(table.Key) + " AS child WHERE " + predicate +
                " AND NOT EXISTS (SELECT 1 FROM " + Q(parent) + " AS parent WHERE " + equality + ")", connection, transaction);
            if (Convert.ToInt64(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException("Imported foreign-key integrity failed.");
            foreignKeys++;
        }
    }
    await transaction.CommitAsync();
    await transaction.DisposeAsync();
    transaction = null;
    await Execute("SET SESSION FOREIGN_KEY_CHECKS = 1");
    stage = "preserve application sequence values";
    string[] requiredSequences = ["audit_logs_id_seq", "change_history_id_seq", "change_number_seq", "problem_number_seq",
        "ticket_assignments_id_seq", "ticket_history_id_seq", "ticket_number_seq"];
    var sequences = header.Sequences.ToDictionary(sequence => sequence.Name);
    if (requiredSequences.Any(name => !sequences.TryGetValue(name, out var sequence) || sequence.LastValue is null))
        throw new InvalidOperationException("An application numbering sequence is unavailable.");
    await Execute("CREATE TABLE astra_sequence_state (name VARCHAR(64) PRIMARY KEY, next_value BIGINT NOT NULL CHECK(next_value > 0)) ENGINE=InnoDB");
    foreach (var name in requiredSequences)
    {
        var next = checked(long.Parse(sequences[name].LastValue!, CultureInfo.InvariantCulture) + 1);
        await using var insert = new MySqlCommand("INSERT INTO astra_sequence_state(name, next_value) VALUES(@name, @next)", connection);
        insert.Parameters.AddWithValue("name", name);
        insert.Parameters.AddWithValue("next", next);
        await insert.ExecuteNonQueryAsync();
    }
    var autoIncrementTables = new Dictionary<string, string>
    {
        ["audit_logs_id_seq"] = "audit_logs", ["change_history_id_seq"] = "change_history",
        ["problem_number_seq"] = "problems", ["ticket_assignments_id_seq"] = "ticket_assignments",
        ["ticket_history_id_seq"] = "ticket_history",
    };
    foreach (var pair in autoIncrementTables)
    {
        var next = checked(long.Parse(sequences[pair.Key].LastValue!, CultureInfo.InvariantCulture) + 1);
        await Execute("ALTER TABLE " + Q(pair.Value) + " AUTO_INCREMENT = " + next.ToString(CultureInfo.InvariantCulture));
        await using var query = new MySqlCommand("SELECT AUTO_INCREMENT FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @table", connection);
        query.Parameters.AddWithValue("table", pair.Value);
        if (Convert.ToInt64(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture) != next)
            throw new InvalidOperationException("A source sequence conflicts with its surviving identifiers.");
    }
    stage = "apply captured source behaviors after data restoration";
    foreach (var sql in SqlScript.Statements(Path.Combine(databaseRoot, "Migrations/002_source_behaviors.sql"))) await Execute(sql);
    await using (var query = new MySqlCommand("SELECT COUNT(*) FROM information_schema.triggers WHERE trigger_schema = DATABASE() AND (trigger_name LIKE 'astra_%_reserve_prefix_%' OR trigger_name LIKE 'astra_%_immutable_%')", connection))
    {
        if (Convert.ToInt32(await query.ExecuteScalarAsync(), CultureInfo.InvariantCulture) != 10)
            throw new InvalidOperationException("Captured source behaviors are incomplete.");
    }
    if (apiSmoke)
    {
        stage = "replacement API write smoke test";
        await RunApiSmoke(database, options.ConnectionString);
    }
    report = new
    {
        capturedAt = DateTimeOffset.UtcNow, status = "passed", sourceSnapshot = Path.GetFileName(snapshot),
        sourceSnapshotSha256 = trailer.Sha256, sourceDataChanged = false, durableMigrationPerformed = persist,
        applicationCutover = false, sourceTables = tables.Count, sourceRecords = digests.Values.Sum(digest => digest.Count),
        ticketAttachmentsVerified = copiedAttachmentIds.Count, foreignKeysVerified = foreignKeys,
        applicationSequencesPreserved = requiredSequences.Length, oldFrameworkSequencesNotRequired = new[] { "knex_migrations_id_seq", "knex_migrations_lock_index_seq" },
        capturedSourceBehaviorTriggersApplied = 10,
        pending = new[] { "Runtime privilege parity and concurrency acceptance", "Business API migration", "Authentication and authorization tests", "Restore-tested backups", "Final write-freeze snapshot and cutover" },
        tables = tableResults,
    };
        Console.WriteLine(persist
            ? $"Private MySQL destination populated and verified: {tables.Count} source tables, {digests.Values.Sum(digest => digest.Count)} records, {copiedAttachmentIds.Count} attachments, {foreignKeys} relationships, {requiredSequences.Length} application sequences."
            : $"MySQL data rehearsal passed: {tables.Count} source tables, {digests.Values.Sum(digest => digest.Count)} records, {copiedAttachmentIds.Count} attachments, {foreignKeys} relationships, {requiredSequences.Length} application sequences.");
}
catch (Exception exception)
{
    // Never print SQL, row contents, driver messages, or source record identifiers.
    Console.Error.WriteLine(JsonSerializer.Serialize(new { status = "failed", stage, table = currentTable,
        errorCode = exception is MySqlException mysql ? mysql.Number.ToString(CultureInfo.InvariantCulture) : exception.GetType().Name }));
    exitCode = 1;
}
finally
{
    if (transaction is not null)
    {
        await transaction.RollbackAsync();
        await transaction.DisposeAsync();
        transaction = null;
    }
    if (created && connection.State == System.Data.ConnectionState.Open && (!persist || exitCode != 0))
    {
        await Execute("SET SESSION FOREIGN_KEY_CHECKS = 1");
        await Execute("DROP DATABASE " + Q(database));
    }
    if (filesCreated && (!persist || exitCode != 0)) Directory.Delete(filesRoot, recursive: true);
}
if (exitCode == 0 && report is not null)
{
    var output = Path.Combine(root, "artifacts", (persist ? "data-import-" : "data-rehearsal-") + DateTime.UtcNow.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8] + ".json");
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, jsonOptions) + "\n");
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(output, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    Console.WriteLine(persist ? "Persistent import report saved privately: " + output : "Temporary database and copied attachment files removed. Report saved: " + output);
}
return exitCode;

async Task Execute(string sql)
{
    await using var command = new MySqlCommand(sql, connection, transaction);
    await command.ExecuteNonQueryAsync();
}

static string Q(string name)
{
    if (!Regex.IsMatch(name, @"^[a-z_][a-z_0-9]*$", RegexOptions.CultureInvariant))
        throw new InvalidOperationException("Invalid metadata identifier.");
    return "`" + name + "`";
}

async Task RunApiSmoke(string database, string serverConnectionString)
{
    var calendarUnit = new SlaCalendar("", "calendar-test", 60, 240, 75, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"), [1, 2, 3, 4, 5], TimeSpan.FromHours(9), TimeSpan.FromHours(17), [new DateOnly(2026, 11, 2)]);
    var beforeHoliday = new DateTime(2026, 10, 30, 11, 0, 0, DateTimeKind.Utc);
    var afterHoliday = new DateTime(2026, 11, 3, 5, 0, 0, DateTimeKind.Utc);
    if (SlaBusinessCalendar.Add(calendarUnit, beforeHoliday, 120) != afterHoliday || SlaBusinessCalendar.Between(calendarUnit, beforeHoliday, afterHoliday) != 120)
        throw new InvalidOperationException("Business-hours calendar behavior did not match the expected holiday/weekend schedule.");
    var apiConnection = new MySqlConnectionStringBuilder(serverConnectionString) { Database = database, GuidFormat = MySqlGuidFormat.None };
    await using var connection = new MySqlConnection(apiConnection.ConnectionString);
    await connection.OpenAsync();
    await connection.ChangeDatabaseAsync(database);
    const string contextSql = "SELECT u.id,c.id,r.id,cat.id,src.code FROM users u JOIN user_roles ur ON ur.user_id=u.id AND ur.role_code='ADMIN' JOIN clients c ON c.status='ACTIVE' AND c.id<>'00000000-0000-4000-8000-000000000132' JOIN contacts r ON r.client_id=c.id AND r.active=1 JOIN categories cat ON cat.active=1 JOIN ticket_sources src ON src.active=1 AND REGEXP_LIKE(src.code,'^[A-Z0-9_]+$','c') WHERE u.active=1 AND (u.access_expires_at IS NULL OR u.access_expires_at>UTC_TIMESTAMP(6)) ORDER BY c.id,r.id,cat.id,src.sort_order LIMIT 1";
    await using var contextQuery = new MySqlCommand(contextSql, connection);
    await using var reader = await contextQuery.ExecuteReaderAsync();
    if (!await reader.ReadAsync()) throw new InvalidOperationException("No active admin and ticket options for the temporary API check.");
    var adminId = reader.GetString(0);
    var clientId = reader.GetString(1);
    var requesterId = reader.GetString(2);
    var categoryId = reader.GetString(3);
    var source = reader.GetString(4);
    await reader.DisposeAsync();

    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:MySql"] = apiConnection.ConnectionString,
    }).Build();
    var store = new MySqlStore(configuration);
    currentTable = "directory reads";
    _ = await store.ListClientDirectory(adminId, clientId, "sites", CancellationToken.None);
    if (!await store.IsAdministrator(adminId, CancellationToken.None) || (await store.GetRoles(CancellationToken.None)).Count == 0 || (await store.GetPermissions(CancellationToken.None)).Count == 0 || (await store.GetTicketSources(CancellationToken.None)).Count == 0 || (await store.ListClients(adminId, CancellationToken.None)).Count == 0 || (await store.ListClientDirectory(adminId, clientId, "contacts", CancellationToken.None)).Count == 0 || (await store.ListCategories(CancellationToken.None)).Count == 0 || (await store.ListTags(CancellationToken.None)).Count == 0 || (await store.ListUsers(CancellationToken.None)).Count == 0 || (await store.ListAuditLogs(CancellationToken.None)).Count == 0)
        throw new InvalidOperationException("A shared directory read check failed.");
    currentTable = "sla policies and calendars";
    var policies = await store.ListSlaSettings(adminId, "policies", CancellationToken.None);
    var calendars = await store.ListSlaSettings(adminId, "calendars", CancellationToken.None);
    if (policies is null || policies.Count == 0 || calendars is null || calendars.Count == 0 ||
        calendars.Any(item => item["working_days"] is not System.Text.Json.JsonElement days || days.ValueKind != System.Text.Json.JsonValueKind.Array || item["holidays"] is not System.Text.Json.JsonElement holidays || holidays.ValueKind != System.Text.Json.JsonValueKind.Array))
        throw new InvalidOperationException("The SLA settings did not load with their calendar days and holidays.");
    currentTable = "create and update sla calendar";
    var calendarName = "Temporary migration calendar " + Guid.NewGuid().ToString("N")[..8];
    var newCalendar = await store.SaveSlaCalendar(adminId, null, new SlaCalendarRequest { Name = calendarName, Timezone = "Asia/Kolkata", WorkingDays = [1, 2, 3, 4, 5], WorkStart = "09:00", WorkEnd = "17:00", Active = true, Holidays = [new SlaHolidayRequest { Date = "2026-11-02", Name = "Temporary holiday" }] }, CancellationToken.None);
    if (newCalendar is null) throw new InvalidOperationException("The SLA calendar was not created.");
    var updatedCalendar = await store.SaveSlaCalendar(adminId, newCalendar.Id, new SlaCalendarRequest { Name = calendarName + " updated", Timezone = "Asia/Kolkata", WorkingDays = [1, 2, 3, 4, 5], WorkStart = "10:00", WorkEnd = "18:00", Active = true, Holidays = [new SlaHolidayRequest { Date = "2026-11-03", Name = "Updated holiday" }] }, CancellationToken.None);
    var calendarRows = await store.ListSlaSettings(adminId, "calendars", CancellationToken.None);
    var savedCalendar = calendarRows?.SingleOrDefault(row => Convert.ToString(row["id"], CultureInfo.InvariantCulture) == newCalendar.Id);
    if (updatedCalendar is null || savedCalendar is null || Convert.ToString(savedCalendar["name"], CultureInfo.InvariantCulture) != calendarName + " updated" || savedCalendar["holidays"] is not System.Text.Json.JsonElement savedHolidays || savedHolidays.GetArrayLength() != 1 || savedHolidays[0].GetProperty("date").GetString() != "2026-11-03")
        throw new InvalidOperationException("The SLA calendar update or holiday replacement failed.");
    currentTable = "update sla policy";
    var policyToUpdate = policies.FirstOrDefault(policy => Convert.ToBoolean(policy["active"], CultureInfo.InvariantCulture)) ?? policies[0];
    var updatedPolicy = await store.SaveSlaPolicy(adminId, Convert.ToString(policyToUpdate["priority"], CultureInfo.InvariantCulture)!, new SlaPolicyRequest { ResponseMinutes = Convert.ToInt32(policyToUpdate["response_minutes"], CultureInfo.InvariantCulture), ResolutionMinutes = Convert.ToInt32(policyToUpdate["resolution_minutes"], CultureInfo.InvariantCulture), WarningPercent = Convert.ToInt32(policyToUpdate["warning_percent"], CultureInfo.InvariantCulture), Active = true, CalendarId = Convert.ToString(policyToUpdate["calendar_id"], CultureInfo.InvariantCulture)!, ApplyToOpenTickets = true }, CancellationToken.None);
    if (updatedPolicy is null) throw new InvalidOperationException("The SLA policy was not saved.");
    currentTable = "admin resource lists";
    var adminOverview = await store.GetAdminOverview(adminId, CancellationToken.None);
    if (adminOverview is null || Convert.ToInt32(adminOverview["users"], CultureInfo.InvariantCulture) == 0 || Convert.ToInt32(adminOverview["tickets"], CultureInfo.InvariantCulture) == 0)
        throw new InvalidOperationException("The admin overview counts did not load.");
    currentTable = "access policy reads";
    var accessPolicies = await store.GetAccessPolicies(adminId, CancellationToken.None);
    var userPolicy = await store.GetUserAccessPolicy(adminId, adminId, CancellationToken.None);
    if (accessPolicies is null || userPolicy is null || System.Text.Json.JsonSerializer.SerializeToElement(accessPolicies).GetProperty("roles").GetArrayLength() != 6 || !System.Text.Json.JsonSerializer.SerializeToElement(userPolicy).GetProperty("roles").EnumerateArray().Any(role => role.GetString() == "ADMIN"))
        throw new InvalidOperationException("The access policies did not load correctly.");
    currentTable = "access policy writes";
    var roleRows = System.Text.Json.JsonSerializer.SerializeToElement(accessPolicies).GetProperty("roles");
    var technicianPolicy = roleRows.EnumerateArray().Single(role => role.GetProperty("code").GetString() == "TECHNICIAN");
    var roleRevision = technicianPolicy.GetProperty("revision").GetInt32();
    var rolePermissions = technicianPolicy.GetProperty("permissions").EnumerateArray().Select(permission => permission.GetString()!).ToArray();
    var savedRolePolicy = await store.SaveAccessPolicy(adminId, "role", "TECHNICIAN", roleRevision, rolePermissions, [], CancellationToken.None);
    if (savedRolePolicy is null || System.Text.Json.JsonSerializer.SerializeToElement(savedRolePolicy).GetProperty("revision").GetInt32() != roleRevision + 1)
        throw new InvalidOperationException("The technician role policy was not saved.");
    try { _ = await store.SaveAccessPolicy(adminId, "role", "TECHNICIAN", roleRevision, rolePermissions, [], CancellationToken.None); throw new InvalidOperationException("A stale policy revision was accepted."); }
    catch (InvalidOperationException exception) when (exception.Message.Contains("Refresh before saving", StringComparison.Ordinal)) { }
    await using var staffQuery = new MySqlCommand("SELECT ur.user_id FROM user_roles ur WHERE ur.role_code IN ('TECHNICIAN','TEAM_LEAD','MANAGER') AND NOT EXISTS(SELECT 1 FROM user_roles fixed_role WHERE fixed_role.user_id=ur.user_id AND fixed_role.role_code IN ('ADMIN','USER','INTERN')) LIMIT 1", connection);
    var staffId = Convert.ToString(await staffQuery.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    if (staffId is null) throw new InvalidOperationException("No staff user exists to check user policy saves.");
    if (staffId is not null)
    {
        var staffPolicy = await store.GetUserAccessPolicy(adminId, staffId, CancellationToken.None);
        if (staffPolicy is null) throw new InvalidOperationException("A staff user policy was unavailable.");
        var staffJson = System.Text.Json.JsonSerializer.SerializeToElement(staffPolicy);
        var staffRevision = staffJson.GetProperty("revision").GetInt32();
        var savedStaffPolicy = await store.SaveAccessPolicy(adminId, "user", staffId, staffRevision, ["TICKETS_TEAM_VIEW"], [], CancellationToken.None);
        if (savedStaffPolicy is null || System.Text.Json.JsonSerializer.SerializeToElement(savedStaffPolicy).GetProperty("revision").GetInt32() != staffRevision + 1)
            throw new InvalidOperationException("A staff user policy was not saved.");
    }
    currentTable = "notification list and preferences";
    var notificationId = Guid.NewGuid().ToString("D");
    await using (var insertNotification = new MySqlCommand("INSERT INTO notifications(id,user_id,ticket_id,type,title,body,event_key) VALUES(@id,@user,(SELECT id FROM tickets ORDER BY created_at LIMIT 1),'TICKET_ASSIGNED','Temporary test','Temporary migration check',@key)", connection))
    {
        insertNotification.Parameters.AddWithValue("@id", notificationId);
        insertNotification.Parameters.AddWithValue("@user", adminId);
        insertNotification.Parameters.AddWithValue("@key", "temporary-migration-" + notificationId);
        await insertNotification.ExecuteNonQueryAsync();
    }
    var notifications = System.Text.Json.JsonSerializer.SerializeToElement(await store.ListNotifications(adminId, 50, CancellationToken.None));
    if (notifications.GetProperty("unread").GetInt32() < 1 || !notifications.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("id").GetString() == notificationId))
        throw new InvalidOperationException("The notification list omitted the new alert.");
    await store.MarkNotificationsRead(adminId, notificationId, CancellationToken.None);
    var marked = System.Text.Json.JsonSerializer.SerializeToElement(await store.ListNotifications(adminId, 50, CancellationToken.None));
    if (marked.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("id").GetString() == notificationId).GetProperty("read_at").ValueKind == System.Text.Json.JsonValueKind.Null)
        throw new InvalidOperationException("The notification was not marked read.");
    await store.SaveNotificationPreferences(adminId, new NotificationPreferencesRequest { TicketAssigned = false }, CancellationToken.None);
    if ((await store.GetNotificationPreferences(adminId, CancellationToken.None)).TicketAssigned)
        throw new InvalidOperationException("The notification preference was not saved.");
    await store.MarkNotificationsRead(adminId, null, CancellationToken.None);
    if (System.Text.Json.JsonSerializer.SerializeToElement(await store.ListNotifications(adminId, 50, CancellationToken.None)).GetProperty("unread").GetInt32() != 0)
        throw new InvalidOperationException("Read-all did not clear the unread count.");
    currentTable = "staff directory reads";
    var departments = await store.ListDepartments(CancellationToken.None);
    var staffDirectory = await store.ListStaffDirectory(adminId, CancellationToken.None);
    if (staffDirectory is null || staffDirectory.Any(person => Convert.ToString(person["id"], CultureInfo.InvariantCulture) == adminId) || departments.Any(department => !Convert.ToBoolean(department["active"], CultureInfo.InvariantCulture)))
        throw new InvalidOperationException("The internal department or staff directory check failed.");
    currentTable = "create department and membership";
    var departmentCreated = await store.CreateDepartment(adminId, new DepartmentRequest { Name = "Temporary migration department " + Guid.NewGuid().ToString("N")[..8], Description = "Isolated check" }, CancellationToken.None);
    if (departmentCreated is null || !((await store.ListDepartments(CancellationToken.None)).Any(department => Convert.ToString(department["id"], CultureInfo.InvariantCulture) == departmentCreated.Id)))
        throw new InvalidOperationException("The department was not created.");
    var memberSaved = await store.SaveDepartmentMember(adminId, departmentCreated.Id, new DepartmentMemberRequest { UserId = adminId, Expertise = "Migration check", IsLead = true }, CancellationToken.None);
    if (memberSaved is null) throw new InvalidOperationException("The department member was not saved.");
    await using (var memberQuery = new MySqlCommand("SELECT is_lead FROM department_users WHERE department_id=@department AND user_id=@user", connection))
    {
        memberQuery.Parameters.AddWithValue("@department", departmentCreated.Id);
        memberQuery.Parameters.AddWithValue("@user", adminId);
        if (!Convert.ToBoolean(await memberQuery.ExecuteScalarAsync(), CultureInfo.InvariantCulture)) throw new InvalidOperationException("The department lead setting was not saved.");
    }
    currentTable = "knowledge and service catalogue reads";
    var knowledgeOptions = System.Text.Json.JsonSerializer.SerializeToElement(await store.GetKnowledgeOptions(adminId, CancellationToken.None));
    var serviceCatalogue = System.Text.Json.JsonSerializer.SerializeToElement(await store.GetServiceCatalogue(adminId, CancellationToken.None));
    var knowledgePage = await store.ListKnowledge(adminId, "", 1, null, CancellationToken.None);
    if (knowledgeOptions.GetProperty("clients").GetArrayLength() == 0 || knowledgeOptions.GetProperty("categories").GetArrayLength() == 0 || !serviceCatalogue.GetProperty("canManage").GetBoolean() || knowledgePage is null)
        throw new InvalidOperationException("The knowledge options, articles, or service catalogue did not load.");
    var knowledgeItems = System.Text.Json.JsonSerializer.SerializeToElement(knowledgePage).GetProperty("items");
    if (knowledgeItems.GetArrayLength() > 0 && await store.ListKnowledge(adminId, "", 1, knowledgeItems[0].GetProperty("id").GetString(), CancellationToken.None) is null)
        throw new InvalidOperationException("The knowledge article detail did not load.");
    currentTable = "create and edit knowledge content";
    var templateName = "Temporary service template " + Guid.NewGuid().ToString("N")[..8];
    var template = await store.SaveServiceTemplate(adminId, null, new ServiceTemplateRequest { Name = templateName, Description = "Temporary migration service description", Instructions = "Temporary migration service instructions", CategoryId = categoryId }, CancellationToken.None);
    if (template is null) throw new InvalidOperationException("The service template was not created.");
    var updatedTemplate = await store.SaveServiceTemplate(adminId, template.Id, new ServiceTemplateRequest { Name = templateName + " updated", Description = "Temporary migration service description", Instructions = "Temporary migration service instructions", CategoryId = categoryId }, CancellationToken.None);
    if (updatedTemplate is null || !System.Text.Json.JsonSerializer.SerializeToElement(await store.GetServiceCatalogue(adminId, CancellationToken.None)).GetProperty("items").EnumerateArray().Any(item => item.GetProperty("id").GetString() == template.Id && item.GetProperty("name").GetString() == templateName + " updated"))
        throw new InvalidOperationException("The service template update failed.");
    var articleTitle = "Temporary knowledge article " + Guid.NewGuid().ToString("N")[..8];
    var article = await store.SaveKnowledgeArticle(adminId, null, new KnowledgeArticleRequest { Title = articleTitle, Body = "Temporary migration knowledge article body.", CategoryId = categoryId, Visibility = "STAFF", Status = "PUBLISHED" }, CancellationToken.None);
    if (article is null) throw new InvalidOperationException("The knowledge article was not created.");
    var articleUpdated = await store.SaveKnowledgeArticle(adminId, article.Id, new KnowledgeArticleRequest { Title = articleTitle + " updated", Body = "Temporary migration knowledge article body.", CategoryId = categoryId, Visibility = "STAFF", Status = "PUBLISHED", Revision = 1 }, CancellationToken.None);
    var articleDetail = await store.ListKnowledge(adminId, "", 1, article.Id, CancellationToken.None);
    if (articleUpdated is null || articleDetail is null || System.Text.Json.JsonSerializer.SerializeToElement(articleDetail).GetProperty("revision").GetInt32() != 2)
        throw new InvalidOperationException("The knowledge article update failed.");
    currentTable = "problem options and list";
    var problemOptions = await store.GetProblemOptions(adminId, CancellationToken.None);
    var problemList = await store.ListProblems(adminId, "", null, null, 1, 20, CancellationToken.None);
    if (problemOptions is null || problemList is null || System.Text.Json.JsonSerializer.SerializeToElement(problemOptions).GetProperty("clients").GetArrayLength() == 0 || System.Text.Json.JsonSerializer.SerializeToElement(problemList).GetProperty("items").ValueKind != System.Text.Json.JsonValueKind.Array)
        throw new InvalidOperationException("The problem options or list did not load.");
    currentTable = "create problem";
    var problemTitle = "Temporary migration problem " + Guid.NewGuid().ToString("N")[..8];
    var problemCreated = await store.CreateProblem(adminId, new CreateProblemRequest { ClientId = clientId, Title = problemTitle, Description = "Temporary investigation for isolated API verification." }, CancellationToken.None);
    var problemSearch = await store.ListProblems(adminId, problemTitle, null, clientId, 1, 20, CancellationToken.None);
    if (problemCreated is null || problemSearch is null || !System.Text.Json.JsonSerializer.SerializeToElement(problemSearch).GetProperty("items").EnumerateArray().Any(problem => problem.GetProperty("id").GetString() == problemCreated.Id))
        throw new InvalidOperationException("The new problem did not appear in the list.");
    currentTable = "problem detail";
    var problemDetail = await store.GetProblemDetail(adminId, problemCreated.Id, CancellationToken.None);
    if (problemDetail is null || System.Text.Json.JsonSerializer.SerializeToElement(problemDetail).GetProperty("problem").GetProperty("id").GetString() != problemCreated.Id || System.Text.Json.JsonSerializer.SerializeToElement(problemDetail).GetProperty("history").GetArrayLength() == 0)
        throw new InvalidOperationException("The problem detail or history did not load.");
    currentTable = "role-filtered help topics";
    var helpTopics = await store.GetHelpTopics(adminId, CancellationToken.None);
    if (helpTopics is null || helpTopics.Length < 20 || !helpTopics.Any(topic => System.Text.Json.JsonSerializer.SerializeToElement(topic).GetProperty("id").GetString() == "audit-history"))
        throw new InvalidOperationException("The administrator help topics did not load.");
    foreach (var resource in new[] { "clients", "contacts", "sites", "assets", "categories", "tags", "sources", "departments", "users", "audit-logs" })
    {
        currentTable = $"admin resource list {resource}";
        var listing = await store.ListAdminResource(adminId, resource, "", "all", null, 1, 10, CancellationToken.None);
        if (listing is null || listing.GetType().GetProperty("items") is null)
            throw new InvalidOperationException($"The temporary {resource} admin list check failed.");
    }
    currentTable = "create contact";
    var contact = await store.CreateContact(adminId, new CreateContactRequest { ClientId=clientId, Name="Temporary API smoke contact", Email="temporary-smoke@example.invalid" }, CancellationToken.None);
    currentTable = "create category";
    var category = await store.CreateCategory(adminId, new CreateCategoryRequest { Name="Temporary API smoke category" }, CancellationToken.None);
    currentTable = "create tag";
    var tag = await store.CreateTag(adminId, new CreateTagRequest { Name="Temporary API smoke tag" }, CancellationToken.None);
    currentTable = "create ticket source";
    var sourceCreated = await store.CreateTicketSource(adminId, new TicketSourceRequest { Code="SMOKE_"+Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(), Name="Temporary API smoke source" }, CancellationToken.None);
    currentTable = "create client";
    var clientCreated = await store.CreateClient(adminId,new CreateClientRequest{Name="Temporary Migration Smoke Client"},CancellationToken.None);
    currentTable = "create site";
    var siteCreated = clientCreated is null?null:await store.CreateSite(adminId,new CreateSiteRequest{ClientId=clientCreated.Id,Name="Temporary Migration Smoke Site"},CancellationToken.None);
    currentTable = "create asset";
    var assetCreated = clientCreated is null || siteCreated is null?null:await store.CreateAsset(adminId,new CreateAssetRequest{ClientId=clientCreated.Id,SiteId=siteCreated.Id,Name="Temporary Migration Smoke Asset",AssetType="DEVICE"},CancellationToken.None);
    if (contact is null || category is null || tag is null || sourceCreated is null || clientCreated is null || siteCreated is null || assetCreated is null) throw new InvalidOperationException("A shared directory write check failed.");
    currentTable = "create ticket";
    CreatedTicket created;
    try { created = await store.CreateTicket(adminId, new CreateTicketRequest
    {
        ClientId = clientId,
        RequesterId = requesterId,
        CategoryId = categoryId,
        Subject = "Temporary migration API smoke check",
        Description = "This record exists only in a temporary rehearsal database and is removed automatically.",
        Source = source,
        Priority = "P3_MEDIUM",
        Attachments = [new CreateAttachment { FileName = "smoke.txt", ContentType = "text/plain", ContentBase64 = "c21va2U=" }],
    }, CancellationToken.None); }
    catch (ArgumentException exception)
    {
        currentTable = exception.Message switch
        {
            "Select valid ticket options." => "ticket create validation: identifiers",
            "Enter a subject and description." => "ticket create validation: text fields",
            "Select a valid ticket source." => "ticket create validation: source",
            "Select a valid priority." => "ticket create validation: priority",
            "Too many tags or attachments." => "ticket create validation: collection limits",
            "An attachment is invalid or its file type is not allowed." => "ticket create validation: attachment metadata",
            "An attachment could not be read." => "ticket create validation: attachment encoding",
            "Attachments must contain valid data between 1 byte and 5 MB." => "ticket create validation: attachment data",
            "One or more selected ticket options are no longer available." => "ticket create validation: selected options",
            "Internal tickets need an active department; other tickets cannot use one." => "ticket create validation: department selection",
            "A selected tag is unavailable." => "ticket create validation: tag selection",
            "The assigned person is not active service-desk staff for this client." => "ticket create validation: assignee",
            "The selected client or site is inactive." => "ticket create validation: client/site",
            _ => "ticket create validation: unexpected argument error",
        };
        throw;
    }
    currentTable = "link knowledge article to ticket";
    var knowledgeLink = await store.LinkKnowledgeTicket(adminId, article.Id, created.Id, CancellationToken.None);
    if (knowledgeLink is null) throw new InvalidOperationException("The knowledge article was not linked to the ticket.");
    await using (var knowledgeLinkQuery = new MySqlCommand("SELECT COUNT(*) FROM ticket_knowledge_links WHERE article_id=@article AND ticket_id=@ticket", connection))
    {
        knowledgeLinkQuery.Parameters.AddWithValue("@article", article.Id);
        knowledgeLinkQuery.Parameters.AddWithValue("@ticket", created.Id);
        if (Convert.ToInt32(await knowledgeLinkQuery.ExecuteScalarAsync(), CultureInfo.InvariantCulture) != 1) throw new InvalidOperationException("The knowledge link was not stored.");
    }
    currentTable = "problem ticket link and unlink";
    var problemLink = await store.ChangeProblemTicketLink(adminId, problemCreated.Id, created.Id, false, CancellationToken.None);
    var linkedProblem = await store.GetProblemDetail(adminId, problemCreated.Id, CancellationToken.None);
    if (problemLink is null || linkedProblem is null || !System.Text.Json.JsonSerializer.SerializeToElement(linkedProblem).GetProperty("tickets").EnumerateArray().Any(ticket => ticket.GetProperty("id").GetString() == created.Id))
        throw new InvalidOperationException("The ticket was not linked to the problem.");
    var problemUnlink = await store.ChangeProblemTicketLink(adminId, problemCreated.Id, created.Id, true, CancellationToken.None);
    if (problemUnlink is null || System.Text.Json.JsonSerializer.SerializeToElement(await store.GetProblemDetail(adminId, problemCreated.Id, CancellationToken.None)).GetProperty("tickets").EnumerateArray().Any(ticket => ticket.GetProperty("id").GetString() == created.Id))
        throw new InvalidOperationException("The ticket was not unlinked from the problem.");
    currentTable = "create comment";
    var comment = await store.CreateTicketComment(adminId, created.Id, new CreateTicketCommentRequest { Type = "INTERNAL", Body = "Temporary API smoke note." }, CancellationToken.None);
    currentTable = "claim ticket";
    var claimed = await store.ClaimTicket(adminId, created.Id, CancellationToken.None);
    currentTable = "request ticket approval";
    var approval = await store.RequestTicketApproval(adminId, created.Id, new ApprovalRequest { Reason = "Temporary migration approval smoke test." }, CancellationToken.None);
    currentTable = "decide ticket approval";
    var approvalDecision = await store.DecideTicketApproval(adminId, created.Id, new ApprovalDecision { Decision = "APPROVED", Note = "Temporary migration approval decision." }, CancellationToken.None);
    currentTable = "upload ticket attachment";
    var uploaded = await store.AddTicketAttachment(adminId, created.Id, new CreateAttachment { FileName = "upload.txt", ContentType = "text/plain", ContentBase64 = "dXBsb2Fk", Visibility = "INTERNAL" }, CancellationToken.None);
    currentTable = "download ticket attachment";
    var downloaded = uploaded is null ? null : await store.GetTicketAttachment(adminId, created.Id, uploaded.Id, CancellationToken.None);
    if (downloaded is null || downloaded.FileName != "upload.txt" || System.Text.Encoding.UTF8.GetString(downloaded.Content) != "upload")
        throw new InvalidOperationException("The temporary attachment upload/download check failed.");
    currentTable = "saved ticket views";
    currentTable = "saved view create";
    var savedView = await store.SaveTicketView(adminId, new SaveTicketViewRequest { Name = "Temporary smoke view", Query = new Dictionary<string, string?> { ["status"] = "OPEN" } }, CancellationToken.None);
    currentTable = "saved view update";
    _ = await store.SaveTicketView(adminId, new SaveTicketViewRequest { Name = "Temporary smoke view", Query = new Dictionary<string, string?> { ["status"] = "IN_PROGRESS" } }, CancellationToken.None);
    currentTable = "saved view list";
    var savedViews = await store.GetSavedTicketViews(adminId, CancellationToken.None);
    var savedId = (string)savedView.GetType().GetProperty("id")!.GetValue(savedView)!;
    currentTable = "saved view delete";
    if (!savedViews.Any(view => Convert.ToString(view["id"], CultureInfo.InvariantCulture) == savedId) || !await store.DeleteSavedTicketView(adminId, savedId, CancellationToken.None))
        throw new InvalidOperationException("The temporary saved-view check failed.");
    currentTable = "assign ticket";
    var assigned = await store.AssignTicket(adminId, created.Id, new AssignTicketRequest { AssignedUserId = adminId, Reason = "Temporary API smoke assignment." }, CancellationToken.None);
    currentTable = "update ticket";
    var updated = await store.UpdateTicket(adminId, created.Id, new UpdateTicketRequest { Status = "IN_PROGRESS", Priority = "P2_HIGH", TagIds = [] }, CancellationToken.None);
    currentTable = "pause ticket SLA";
    await store.UpdateTicket(adminId, created.Id, new UpdateTicketRequest { Status = "WAITING_FOR_CLIENT" }, CancellationToken.None);
    currentTable = "resume ticket SLA";
    await store.UpdateTicket(adminId, created.Id, new UpdateTicketRequest { Status = "IN_PROGRESS" }, CancellationToken.None);
    currentTable = "dashboard SLA counts";
    var dashboard = await store.GetTicketDashboard(adminId, true, CancellationToken.None);
    using var dashboardJson = JsonDocument.Parse(JsonSerializer.Serialize(dashboard));
    if (!dashboardJson.RootElement.GetProperty("counts").TryGetProperty("unattended", out _))
        throw new InvalidOperationException("The dashboard did not return the business-hours first-response count.");
    currentTable = "verify writes";
    await using var verify = new MySqlCommand("SELECT t.ticket_number,t.status,t.priority,(SELECT COUNT(*) FROM ticket_comments c WHERE c.ticket_id=t.id),(SELECT COUNT(*) FROM ticket_attachments a WHERE a.ticket_id=t.id),(SELECT COUNT(*) FROM ticket_history h WHERE h.ticket_id=t.id),(SELECT COUNT(*) FROM ticket_sla s WHERE s.ticket_id=t.id AND s.paused_at IS NULL AND s.resolution_due_at>t.created_at) FROM tickets t WHERE t.id=@id", connection);
    verify.Parameters.AddWithValue("@id", created.Id);
    await using var result = await verify.ExecuteReaderAsync();
    if (!await result.ReadAsync() || result.GetString(1) != "IN_PROGRESS" || result.GetString(2) != "P2_HIGH" || Convert.ToInt64(result.GetValue(3), CultureInfo.InvariantCulture) != 1 || Convert.ToInt64(result.GetValue(4), CultureInfo.InvariantCulture) != 2 || Convert.ToInt64(result.GetValue(5), CultureInfo.InvariantCulture) < 10 || Convert.ToInt64(result.GetValue(6), CultureInfo.InvariantCulture) != 1 || comment is null || claimed is null || assigned is null || updated is null || approval is null || approvalDecision?.Decision != "APPROVED")
        throw new InvalidOperationException("A temporary API write verification failed.");
    Console.WriteLine("Replacement API smoke passed in a temporary database: ticket, claim, assignment, comment, approval request/decision, attachment upload/download and integrity, saved ticket views, status/priority update, history, audit, SLA pause/resume and dashboard calendar count.");
}

static string ResolveStoragePath(string root, string key)
{
    if (string.IsNullOrWhiteSpace(key) || Path.IsPathRooted(key) || key.Contains('\\') ||
        key.Split('/').Any(part => part is "" or "." or "..") ||
        !Regex.IsMatch(key, @"^[A-Za-z0-9._/-]+$", RegexOptions.CultureInvariant))
        throw new InvalidOperationException("Unsafe attachment storage path.");
    var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
    var target = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
    if (!target.StartsWith(fullRoot, StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe attachment storage path.");
    return target;
}

using System.Globalization;
using System.Text.RegularExpressions;
using Astra.Shared.Data;
using Astra.Shared.Security;
using MySqlConnector;

namespace Astra.Shared.Services;

public static partial class TicketNumbering
{
    [GeneratedRegex("^[A-Z][A-Z0-9]{1,15}$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();

    public static string NormalizePrefix(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (!PrefixPattern().IsMatch(normalized)) throw new ApiFault(400, "Use a ticket prefix of 2 to 16 letters or numbers, starting with a letter.");
        return normalized;
    }

    public static async Task<string> ReserveAsync(MySqlConnection connection, MySqlTransaction transaction, string ownerKind, Guid ownerId, string? requested, Guid? clientId = null, CancellationToken cancellationToken = default)
    {
        if (ownerKind is not ("CLIENT" or "SITE")) throw new ArgumentException("Unsupported prefix owner.", nameof(ownerKind));
        async Task<bool> Reserve(string prefix)
        {
            var parameters = new Dictionary<string, object?> { ["prefix"] = prefix, ["owner"] = ownerId, ["kind"] = ownerKind };
            var existing = await Database.QueryAsync(connection, "SELECT owner_kind,owner_id FROM ticket_prefix_registry WHERE prefix=@prefix FOR UPDATE", parameters, transaction, cancellationToken);
            if (existing.Count > 0) return existing[0]["owner_id"] is Guid id && id == ownerId && (string)existing[0]["owner_kind"]! == ownerKind;
            await Database.ExecuteAsync(connection, "INSERT INTO ticket_prefix_registry(prefix,owner_kind,owner_id) VALUES(@prefix,@kind,@owner)", parameters, transaction, cancellationToken);
            return true;
        }
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var prefix = NormalizePrefix(requested);
            if (!await Reserve(prefix)) throw new ApiFault(409, "Ticket prefix is already reserved.");
            return prefix;
        }
        if (ownerKind != "SITE" || clientId is not Guid client) throw new ApiFault(400, "Choose a ticket prefix.");
        var customers = await Database.QueryAsync(connection, "SELECT ticket_prefix FROM clients WHERE id=@id FOR UPDATE", new Dictionary<string, object?> { ["id"] = client }, transaction, cancellationToken);
        if (customers.Count != 1) throw new ApiFault(400, "Customer is required.");
        var basePrefix = (string)customers[0]["ticket_prefix"]!;
        await Database.ExecuteAsync(connection, "SELECT name FROM app_locks WHERE name='global' FOR UPDATE", new Dictionary<string, object?> { ["resource"] = "ASTRA site prefix:" + basePrefix }, transaction, cancellationToken);
        for (long suffix = 1; ; suffix++)
        {
            var candidate = basePrefix + "S" + suffix.ToString("D3", CultureInfo.InvariantCulture);
            if (candidate.Length > 16) throw new ApiFault(400, "Choose a shorter site ticket prefix.");
            if (await Reserve(candidate)) return candidate;
        }
    }

    public static async Task<string> NextTicketAsync(MySqlConnection connection, MySqlTransaction transaction, Guid clientId, Guid? siteId, CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?> { ["client"] = clientId, ["site"] = siteId };
        var records = siteId.HasValue
            ? await Database.QueryAsync(connection, "SELECT ticket_prefix FROM sites WHERE id=@site AND client_id=@client AND active=1 FOR UPDATE", parameters, transaction, cancellationToken)
            : await Database.QueryAsync(connection, "SELECT ticket_prefix FROM clients WHERE id=@client FOR UPDATE", parameters, transaction, cancellationToken);
        if (records.Count != 1) throw new ApiFault(400, siteId.HasValue ? "Site does not belong to this customer or is inactive." : "Customer is required.");
        var prefix = (string)records[0]["ticket_prefix"]!;
        var table = siteId.HasValue ? "site_ticket_sequences" : "client_ticket_sequences";
        var key = siteId.HasValue ? "site_id" : "client_id";
        parameters["id"] = siteId ?? clientId;
        var current = await Database.QueryAsync(connection, $"SELECT next_number FROM {table} WHERE {key}=@id FOR UPDATE", parameters, transaction, cancellationToken);
        long number;
        if (current.Count == 0)
        {
            number = 1;
            await Database.ExecuteAsync(connection, $"INSERT {table}({key},next_number) VALUES(@id,2)", parameters, transaction, cancellationToken);
        }
        else
        {
            number = Convert.ToInt64(current[0]["next_number"], CultureInfo.InvariantCulture);
            if (number == long.MaxValue) throw new ApiFault(409, "Ticket number range is exhausted.");
            await Database.ExecuteAsync(connection, $"UPDATE {table} SET next_number=next_number+1 WHERE {key}=@id", parameters, transaction, cancellationToken);
        }
        return prefix + "-" + number.ToString("D6", CultureInfo.InvariantCulture);
    }
}

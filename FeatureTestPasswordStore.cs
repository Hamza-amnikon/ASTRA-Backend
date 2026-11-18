using System.Text.Json;
using MySqlConnector;

public sealed partial class MySqlStore
{
    private static string? DemoLoginDomain(string email)
    {
        try { return "@" + NormalizeDomain(email[(email.LastIndexOf('@') + 1)..]); }
        catch (ArgumentException) { return null; }
    }

    public async Task<KnownAccount?> FindPasswordTestAccount(TestPasswordAccount account, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        const string sql = """
            SELECT u.session_version FROM users u
            WHERE u.id=@id AND u.email=@email AND u.active=1
              AND (u.access_expires_at IS NULL OR u.access_expires_at>UTC_TIMESTAMP(6))
              AND EXISTS(SELECT 1 FROM user_roles WHERE user_id=u.id AND role_code IN ('TECHNICIAN','TEAM_LEAD','MANAGER','INTERN','USER'))
              AND NOT EXISTS(
                  SELECT 1 FROM client_users cu JOIN clients c ON c.id=cu.client_id
                  WHERE cu.user_id=u.id AND LEFT(c.primary_email,1)='@'
                    AND (c.id<>'00000000-0000-4000-8000-000000000132' OR NOT EXISTS(SELECT 1 FROM client_users official WHERE official.user_id=u.id AND official.client_id<>'00000000-0000-4000-8000-000000000132'))
                    AND NOT (CAST(c.primary_email AS BINARY) <=> @domain)
              )
            """;
        await using var command = new MySqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", account.UserId);
        command.Parameters.AddWithValue("@email", account.Login);
        command.Parameters.AddWithValue("@domain", (object?)DemoLoginDomain(account.Login) ?? DBNull.Value);
        var version = await command.ExecuteScalarAsync(ct);
        return version is null ? null : new KnownAccount(account.UserId, Convert.ToInt32(version));
    }

    public async Task<KnownAccount?> EnsurePasswordTestAccount(TestPasswordAccount account, CancellationToken ct)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var lookup = new MySqlCommand("SELECT 1 FROM users WHERE id=@id", connection);
        lookup.Parameters.AddWithValue("@id", account.UserId);
        if (await lookup.ExecuteScalarAsync(ct) is null) {
            var admin = await EnsureLocalPreviewAccount(ct);
            await ProvisionPasswordTestAccount(admin.Id, account, account.Role, ct);
        }
        return await FindPasswordTestAccount(account, ct);
    }

    public async Task ProvisionPasswordTestAccount(string actor, TestPasswordAccount account, string role, CancellationToken ct, string? clientId = null, string? supervisorUserId = null)
    {
        if (!TestPasswordAccounts.IsDemoEmail(account.Login) || !TestPasswordAccounts.IsDemoStaffRole(role))
            throw new ArgumentException("Invalid test account.");
        if (role == "USER" && clientId != "00000000-0000-4000-8000-000000000132")
            throw new ArgumentException("Employee accounts must belong to Amnikon Internal.");
        if (role == "INTERN" && clientId is not null)
            throw new ArgumentException("Demo interns are assigned to internal work. Choose a supervisor.");
        if (role == "MANAGER" && string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("Select a client for the demo manager.");
        var body = JsonSerializer.SerializeToElement(new {
            roles = new[] { role }, clientIds = clientId is null ? Array.Empty<string>() : new[] { clientId },
            siteIds = Array.Empty<string>(),
            departmentIds = Array.Empty<string>(), leadDepartmentIds = Array.Empty<string>(), permissions = Array.Empty<string>(),
            active = true, workScope = clientId is null || clientId == "00000000-0000-4000-8000-000000000132" ? "INTERNAL" : "BOTH", changeAccess = "NONE",
            supervisorUserId = role == "INTERN" ? supervisorUserId : null
        });
        await UpdateUserAccess(actor, account.UserId, body, ct,
            new DirectoryIdentity(account.UserId, account.DisplayName, account.Login, account.Login, "00000000-0000-0000-0000-000000000000"),
            demoStaff: true);
    }
}

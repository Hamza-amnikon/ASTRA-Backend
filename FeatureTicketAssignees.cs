using MySqlConnector;

public sealed partial class MySqlStore
{
    private const string TicketStaffRoles = "'INTERN','TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN'";

    private static string TicketAssigneeScope(string allowGlobalAdmin) => $"""
        (EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=u.id AND cu.client_id=@client)
         AND (@site IS NULL OR NOT EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=u.id)
              OR EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=u.id AND us.site_id=@site)))
        """;

    public async Task<List<Dictionary<string, object?>>> GetTicketAssignees(string userId, string clientId, string? siteId, CancellationToken ct)
    {
        if (ConnectionString is null) return [];
        var sql = $"""
            WITH actor AS (
                SELECT a.id, EXISTS(SELECT 1 FROM user_roles r WHERE r.user_id=a.id AND r.role_code='ADMIN') AS is_admin
                FROM users a
                WHERE a.id=@actor AND a.active=1
                  AND (a.access_expires_at IS NULL OR a.access_expires_at>UTC_TIMESTAMP(6))
                  AND EXISTS(SELECT 1 FROM user_roles r WHERE r.user_id=a.id AND r.role_code IN ({TicketStaffRoles}))
            )
            SELECT u.id,u.display_name AS name
            FROM users u CROSS JOIN actor a
            WHERE {TicketAssigneeScope("a.is_admin")}
              AND (@site IS NULL OR EXISTS(SELECT 1 FROM sites s WHERE s.id=@site AND s.client_id=@client))
              AND (a.is_admin OR (
                  (EXISTS(SELECT 1 FROM client_users cu WHERE cu.user_id=a.id AND cu.client_id=@client)
                   OR EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=a.id AND us.site_id=@site))
                  AND (@site IS NULL OR NOT EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=a.id)
                       OR EXISTS(SELECT 1 FROM user_sites us WHERE us.user_id=a.id AND us.site_id=@site))
              ))
            ORDER BY u.display_name,u.id
            """;
        return await FQuery(sql, ct, ("actor", userId), ("client", clientId), ("site", siteId));
    }

    private static async Task<bool> IsTicketAssigneeEligible(MySqlConnection connection, MySqlTransaction transaction,
        string assigneeId, string clientId, string? siteId, bool allowGlobalAdmin, CancellationToken ct)
    {
        var sql = $"SELECT EXISTS(SELECT 1 FROM users u WHERE u.id=@assignee AND {TicketAssigneeScope("@allowGlobalAdmin")})";
        await using var command = FCommand(connection, transaction, sql,
            ("assignee", assigneeId), ("client", clientId), ("site", siteId), ("allowGlobalAdmin", allowGlobalAdmin));
        return FBool(await command.ExecuteScalarAsync(ct));
    }
}

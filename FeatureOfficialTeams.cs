using MySqlConnector;
public sealed partial class MySqlStore
{
    public async Task<List<Dictionary<string, object?>>> FilterOfficialTeamMembers(string actor, List<Dictionary<string, object?>> members, CancellationToken ct)
    {
        var allowed = await FQuery(@"SELECT u.id FROM users u WHERE u.active=1
            AND (u.access_expires_at IS NULL OR u.access_expires_at>UTC_TIMESTAMP(6))
            AND EXISTS(SELECT 1 FROM user_roles r WHERE r.user_id=u.id AND r.role_code IN ('INTERN','TECHNICIAN','TEAM_LEAD','MANAGER','ADMIN'))
            AND (EXISTS(SELECT 1 FROM user_roles r WHERE r.user_id=@actor AND r.role_code='ADMIN')
              OR EXISTS(SELECT 1 FROM client_users mine JOIN client_users colleague ON colleague.client_id=mine.client_id
                        WHERE mine.user_id=@actor AND colleague.user_id=u.id AND mine.client_id<>'00000000-0000-4000-8000-000000000132')
              OR (EXISTS(SELECT 1 FROM client_users mine WHERE mine.user_id=@actor AND mine.client_id='00000000-0000-4000-8000-000000000132')
                  AND NOT EXISTS(SELECT 1 FROM client_users mine WHERE mine.user_id=@actor AND mine.client_id<>'00000000-0000-4000-8000-000000000132')
                  AND EXISTS(SELECT 1 FROM client_users colleague WHERE colleague.user_id=u.id AND colleague.client_id='00000000-0000-4000-8000-000000000132')
                  AND NOT EXISTS(SELECT 1 FROM client_users colleague WHERE colleague.user_id=u.id AND colleague.client_id<>'00000000-0000-4000-8000-000000000132')))", ct, ("actor",actor));
        var ids=allowed.Select(row=>Convert.ToString(row["id"])!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return members.Where(row=>ids.Contains(Convert.ToString(row["id"])!)).ToList();
    }
    public async Task<bool> IsOfficialTeamColleague(string actor, string colleague, CancellationToken ct) =>
        (await FilterOfficialTeamMembers(actor,[new Dictionary<string,object?> { ["id"]=colleague }],ct)).Count==1;
}

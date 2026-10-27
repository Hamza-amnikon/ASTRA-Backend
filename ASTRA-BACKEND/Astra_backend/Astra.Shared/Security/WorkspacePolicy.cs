namespace Astra.Shared.Security;

public sealed record Actor(Guid Id, string DisplayName, string Email, IReadOnlySet<string> Roles, IReadOnlySet<string> Permissions, IReadOnlyList<Guid> ClientIds, IReadOnlyList<Guid> SiteIds, string Workspace, string ChangeAccess, string WorkScope, IReadOnlySet<string>? GroupPermissions = null, IReadOnlyList<Guid>? DepartmentIds = null);

public static class WorkspacePolicy
{
    public static Actor Select(Actor account, string workspace)
    {
        var admin = account.Roles.Contains("ADMIN");
        return workspace switch
        {
            "admin" when admin => account with { Workspace = "admin" },
            "operations" when account.Roles.Overlaps(["ADMIN", "MANAGER", "TEAM_LEAD", "TECHNICIAN", "INTERN"]) => account with
            {
                Workspace = "operations",
                Roles = Set(account.Roles.Overlaps(["ADMIN", "MANAGER", "TEAM_LEAD", "TECHNICIAN"]) ? "TECHNICIAN" : "INTERN"),
                Permissions = PermissionsFor(account.Roles.Overlaps(["ADMIN", "MANAGER", "TEAM_LEAD", "TECHNICIAN"]) ? "TECHNICIAN" : "INTERN", account.GroupPermissions),
                ChangeAccess = account.ChangeAccess == "NONE" ? "NONE" : "CONTRIBUTOR"
            },
            "management" when account.Roles.Overlaps(["ADMIN", "MANAGER", "TEAM_LEAD"]) => account with
            {
                Workspace = "management",
                Roles = Set(admin || account.Roles.Contains("MANAGER") ? "MANAGER" : "TEAM_LEAD"),
                Permissions = PermissionsFor(admin || account.Roles.Contains("MANAGER") ? "MANAGER" : "TEAM_LEAD", account.GroupPermissions),
                ChangeAccess = admin ? "COORDINATOR" : account.ChangeAccess
            },
            "client" when account.Roles.Contains("USER") => account with { Workspace = "client", Roles = Set("USER"), Permissions = Set(), ChangeAccess = "NONE", WorkScope = "CLIENT" },
            _ => throw new ApiFault(403, "Select an authorized workspace.")
        };
    }

    public static bool OperationalWrite(string method, string path)
    {
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)) return false;
        var parts = path.Split('?', 2)[0].Trim('/').Split('/');
        if (parts.Length == 0) return false;
        if (parts[0] == "tickets" && parts.Length > 1 && parts[1] == "views") return false;
        if (parts[0] is "tickets" or "changes" or "problems") return true;
        if (parts.Length > 1 && ((parts[0] == "internal" && parts[1] == "tickets") || (parts[0] == "mail" && parts[1] == "intake"))) return true;
        if (parts[0] == "knowledge" && parts.Length == 3 && parts[2] == "tickets") return true;
        return parts[0] == "change-governance" && parts.Length > 1 && parts[1] is not ("groups" or "policy" or "templates" or "blackouts");
    }

    public static void RequireOperational(Actor actor, string method, string path)
    {
        if (actor.Workspace == "admin" && OperationalWrite(method, path)) throw new ApiFault(403, "Administration provides configuration and read-only service oversight.");
    }

    private static HashSet<string> Set(params string[] values) => new(values, StringComparer.Ordinal);

    private static readonly HashSet<string> StaffGroupGrants = Set("TICKETS_TEAM_VIEW", "TICKETS_ASSIGN", "TICKETS_APPROVE", "CATEGORY_MANAGE", "SLA_MANAGE", "REPORTS_VIEW", "TAGS_MANAGE");
    public static IReadOnlySet<string> PermissionsFor(string role, IReadOnlySet<string>? groups = null)
    {
        var permissions = role switch
        {
            "TECHNICIAN" => Set("TICKETS_TEAM_VIEW", "TICKETS_ASSIGN"),
            "TEAM_LEAD" => Set("TICKETS_TEAM_VIEW", "TICKETS_ASSIGN", "CATEGORY_MANAGE", "TAGS_MANAGE", "REPORTS_VIEW"),
            "MANAGER" => Set("TICKETS_TEAM_VIEW", "TICKETS_ASSIGN", "TICKETS_APPROVE", "CATEGORY_MANAGE", "TAGS_MANAGE", "SLA_MANAGE", "REPORTS_VIEW", "AUDIT_VIEW"),
            "ADMIN" => Set("*"),
            _ => Set()
        };
        if (role is "TECHNICIAN" or "TEAM_LEAD" or "MANAGER") permissions.UnionWith((groups ?? Set()).Where(StaffGroupGrants.Contains));
        return permissions;
    }
}

public sealed class ApiFault(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public static partial class FeatureApis
{
    private static string ProfilePhotoPath(string actor, IWebHostEnvironment environment) =>
        Path.Combine(environment.ContentRootPath, "App_Data", "profile-photos", Guid.Parse(actor).ToString("D") + ".image");

    private static void MapProfile(RouteGroupBuilder api)
    {
        api.MapGet("/profile", async (HttpContext c, MySqlStore s, CancellationToken ct) => {
            var offset = int.TryParse(c.Request.Query["offset"], out var minutes) ? minutes : 0;
            return Results.Ok(await s.GetPersonalProfile(Actor(c), Math.Clamp(offset, -840, 840), ct));
        });
        api.MapGet("/profile/photo", (HttpContext c, IWebHostEnvironment e) => {
            var path = ProfilePhotoPath(Actor(c), e);
            if (!File.Exists(path)) return Results.NotFound();
            var bytes = File.ReadAllBytes(path);
            c.Response.Headers.CacheControl = "private, no-store";
            return Results.File(bytes, bytes[0] == 0x89 ? "image/png" : "image/jpeg");
        });
        api.MapPut("/profile/photo", async (ProfilePhotoInput body, HttpContext c, IWebHostEnvironment e, CancellationToken ct) => {
            if (string.IsNullOrWhiteSpace(body.ContentBase64)) throw new ArgumentException("Choose a JPEG or PNG image.");
            if (body.ContentBase64.Length > 2_800_000) throw new ArgumentException("Choose an image smaller than 2 MB.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(body.ContentBase64); }
            catch (FormatException) { throw new ArgumentException("Choose a JPEG or PNG image."); }
            var png = bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10});
            var jpeg = bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff;
            if (bytes.Length > 2 * 1024 * 1024 || (!png && !jpeg)) throw new ArgumentException("Choose a JPEG or PNG image smaller than 2 MB.");
            var path = ProfilePhotoPath(Actor(c), e);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var stream = new FileStream(temp, options)) await stream.WriteAsync(bytes, ct);
                File.Move(temp, path, true);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
            return Results.Ok(new { saved = true });
        });
    }
}
public sealed record ProfilePhotoInput(string ContentBase64);

public sealed partial class MySqlStore
{
    public async Task<object> GetPersonalProfile(string actor, int offset, CancellationToken ct)
    {
        var person = (await FQuery("SELECT display_name,email,active,created_at FROM users WHERE id=@actor AND active=1 AND (access_expires_at IS NULL OR access_expires_at>UTC_TIMESTAMP(6))", ct, ("actor", actor))).FirstOrDefault() ?? throw new UnauthorizedAccessException();
        var roles = await FQuery("SELECT CASE WHEN role_code='USER' AND EXISTS(SELECT 1 FROM users employee WHERE employee.id=@actor AND employee.work_scope='INTERNAL') THEN 'EMPLOYEE' ELSE role_code END AS name FROM user_roles WHERE user_id=@actor ORDER BY role_code", ct, ("actor", actor));
        var msps = await FQuery("SELECT c.name FROM client_users cu JOIN clients c ON c.id=cu.client_id WHERE cu.user_id=@actor AND (c.id<>'00000000-0000-4000-8000-000000000132' OR NOT EXISTS(SELECT 1 FROM client_users official WHERE official.user_id=@actor AND official.client_id<>'00000000-0000-4000-8000-000000000132')) ORDER BY c.name", ct, ("actor", actor));
        var departments = await FQuery("SELECT d.name FROM department_users du JOIN departments d ON d.id=du.department_id WHERE du.user_id=@actor ORDER BY d.name", ct, ("actor", actor));
        var workload = (await FQuery("""
            SELECT COUNT(*) AS assigned,
              COALESCE(SUM(status NOT IN ('RESOLVED','CLOSED','CANCELLED')),0) AS open,
              COALESCE(SUM(status='IN_PROGRESS'),0) AS in_progress,
              COALESCE(SUM(status IN ('WAITING_FOR_CLIENT','WAITING_FOR_VENDOR','ON_HOLD')),0) AS waiting
            FROM tickets WHERE assigned_user_id=@actor
            """, ct, ("actor", actor))).Single();
        var resolved = await FQuery("""
            SELECT DATE_FORMAT(DATE_ADD(created_at, INTERVAL @offset MINUTE),'%Y-%m-%d') AS day,COUNT(DISTINCT ticket_id) AS resolved
            FROM ticket_history WHERE actor_user_id=@actor AND action='TICKET.STATUS_CHANGED'
              AND JSON_UNQUOTE(new_value)='RESOLVED'
              AND created_at>=DATE_SUB(DATE(DATE_ADD(UTC_TIMESTAMP(),INTERVAL @offset MINUTE)),INTERVAL 29 DAY)-INTERVAL @offset MINUTE
            GROUP BY day ORDER BY day
            """, ct, ("actor", actor), ("offset", offset));
        var qa = (await FQuery("""
            WITH latest AS (
              SELECT new_value,ROW_NUMBER() OVER(PARTITION BY ticket_id ORDER BY created_at DESC,id DESC) AS rn
              FROM ticket_history WHERE action='TICKET.QA_REVIEWED'
            )
            SELECT COUNT(*) AS reviewed_tickets,
              AVG(CAST(NULLIF(JSON_UNQUOTE(JSON_EXTRACT(new_value,'$.average')),'null') AS DECIMAL(8,3)) * CASE WHEN JSON_UNQUOTE(JSON_EXTRACT(new_value,'$.ratingScale'))='4' THEN 25 ELSE 20 END) AS rating_percent
            FROM latest WHERE rn=1 AND JSON_UNQUOTE(JSON_EXTRACT(new_value,'$.assigneeId'))=@actor
              AND NULLIF(JSON_UNQUOTE(JSON_EXTRACT(new_value,'$.average')),'null') IS NOT NULL
            """, ct, ("actor", actor))).Single();
        return new { person, roles, msps, departments, workload, resolved, qa };
    }
}

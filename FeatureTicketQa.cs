public sealed record TicketQaInput(int? SolutionAccuracy = null, int? TroubleshootingQuality = null, string? Comment = null,
    int? Communication = null, int? ProcessCompliance = null,
    int? Documentation = null, int? FixVerification = null, int? CriteriaVersion = null);

public static partial class FeatureApis
{
    private static void MapTicketQa(RouteGroupBuilder api)
    {
        api.MapPost("/tickets/{id:guid}/qa-reviews", async (string id, TicketQaInput body,
            HttpContext context, MySqlStore store, CancellationToken ct) =>
            Results.Ok(await store.SaveTicketQa(Actor(context), id, body, ct)));
    }
}

public sealed partial class MySqlStore
{
    public async Task<object> SaveTicketQa(string actor, string ticketId, TicketQaInput body, CancellationToken ct)
    {
        if (body.CriteriaVersion is not (null or 1 or 2 or 3)) throw new ArgumentException("Unsupported QA rating version.");
        var ratingScale = body.CriteriaVersion == 3 ? 4 : 5;
        var currentCriteria = body.CriteriaVersion is 2 or 3 || body.Communication.HasValue || body.ProcessCompliance.HasValue ||
            (!body.Documentation.HasValue && !body.FixVerification.HasValue);
        var third = currentCriteria ? body.Communication : body.Documentation;
        var fourth = currentCriteria ? body.ProcessCompliance : body.FixVerification;
        var ratings = new[] { body.SolutionAccuracy, body.TroubleshootingQuality, third, fourth };
        if (ratings.Any(rating => rating.HasValue && (rating.Value < 1 || rating.Value > ratingScale)))
            throw new ArgumentException($"Selected QA ratings must be from 1 to {ratingScale}.");
        var rated = ratings.Where(rating => rating.HasValue).Select(rating => rating!.Value).ToArray();
        var comment = FText(body.Comment, "QA comment", 0, 2000);
        await using var connection = CreateConnection();
        await connection.OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        var reviewer = (await FRows(connection, tx, """
            SELECT u.display_name,EXISTS(SELECT 1 FROM user_roles r WHERE r.user_id=u.id AND r.role_code='ADMIN') AS is_admin
            FROM users u WHERE u.id=@actor AND u.active=1
              AND (u.access_expires_at IS NULL OR u.access_expires_at>UTC_TIMESTAMP(6))
              AND EXISTS(SELECT 1 FROM user_roles r WHERE r.user_id=u.id AND r.role_code IN ('TEAM_LEAD','MANAGER','ADMIN'))
            """, ct, ("actor", actor))).FirstOrDefault() ?? throw new UnauthorizedAccessException();
        var ticket = (await FRows(connection, tx,
            $"SELECT t.id,t.assigned_user_id FROM tickets t WHERE t.id=@ticket AND {FScope("t")} FOR UPDATE",
            ct, ("ticket", ticketId), ("actor", actor), ("admin", FBool(reviewer["is_admin"]))))
            .FirstOrDefault() ?? throw new KeyNotFoundException();
        var review = new {
            solutionAccuracy = body.SolutionAccuracy,
            troubleshootingQuality = body.TroubleshootingQuality,
            criteriaVersion = body.CriteriaVersion == 3 ? 3 : currentCriteria ? 2 : 1,
            ratingScale,
            communication = currentCriteria ? (int?)third : null,
            processCompliance = currentCriteria ? (int?)fourth : null,
            documentation = currentCriteria ? null : (int?)third,
            fixVerification = currentCriteria ? null : (int?)fourth,
            comment,
            average = rated.Length == 0 ? (double?)null : rated.Average(),
            assigneeId = ticket["assigned_user_id"],
        };
        await FExecute(connection, tx, """
            INSERT INTO ticket_history(ticket_id,actor_user_id,action,new_value,reason)
            VALUES(@ticket,@actor,'TICKET.QA_REVIEWED',CAST(@review AS JSON),@reason)
            """, ct, ("ticket", ticketId), ("actor", actor), ("review", FJson(review)),
            ("reason", "QA review recorded"));
        await AddAudit(connection, tx, actor, "TICKET.QA_REVIEWED", "TICKET", ticketId, review, ct);
        await tx.CommitAsync(ct);
        return new { saved = true };
    }
}

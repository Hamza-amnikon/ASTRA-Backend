public sealed partial class MySqlStore
{
    private static (string Sql, List<(string Name, object? Value)> Args) TicketExtraFilters(IQueryCollection? query)
    {
        var args = new List<(string, object?)>();
        var terms = new List<string>();
        if (query is null)
            return ("", args);
        foreach (var (key, column) in new[] { ("clientId", "client_id"), ("siteId", "site_id"), ("categoryId", "category_id"), ("assignedUserId", "assigned_user_id") })
        {
            var value = query[key].ToString();
            if (value.Length == 0)
                continue;
            if (key == "assignedUserId" && value == "UNASSIGNED")
            {
                terms.Add("t.assigned_user_id IS NULL");
                continue;
            }
            value = FGuid(value, key);
            var parameter = "@filter_" + key;
            args.Add((parameter, value));
            terms.Add("t." + column + "=" + parameter);
        }
        var priority = query["priority"].ToString();
        if (priority.Length > 0)
        {
            if (!new[] { "P1_CRITICAL", "P2_HIGH", "P3_MEDIUM", "P4_LOW" }.Contains(priority))
                throw new ArgumentException("Choose a valid priority.");
            args.Add(("@filter_priority", priority));
            terms.Add("t.priority=@filter_priority");
        }
        var source = query["source"].ToString();
        if (source.Length > 0)
        {
            if (source.Length > 50)
                throw new ArgumentException("Choose a valid source.");
            args.Add(("@filter_source", source));
            terms.Add("t.source=@filter_source");
        }
        var tag = query["tagId"].ToString();
        if (tag.Length > 0)
        {
            args.Add(("@filter_tag", FGuid(tag, "tag")));
            terms.Add("EXISTS(SELECT 1 FROM ticket_tags ft WHERE ft.ticket_id=t.id AND ft.tag_id=@filter_tag)");
        }
        foreach (var key in new[] { "from", "to" })
        {
            var text = query[key].ToString();
            if (text.Length == 0)
                continue;
            if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
                throw new ArgumentException("Choose valid filter dates.");
            var bound = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
            if (key == "to")
                bound = bound.AddDays(1);
            args.Add(("@filter_" + key, bound));
            terms.Add("t.created_at" + (key == "from" ? ">=" : "<") + "@filter_" + key);
        }
        var dashboard = query["dashboardFilter"].ToString();
        if (dashboard.Length > 0)
            terms.Add(dashboard switch
            {
                "WAITING" => "t.status IN ('WAITING_FOR_CLIENT','WAITING_FOR_VENDOR','ON_HOLD')",
                "PENDING_APPROVAL" => "t.approval_status='PENDING'",
                "AWAITING_FIRST_RESPONSE" => "t.first_response_at IS NULL AND t.status NOT IN ('RESOLVED','CLOSED','CANCELLED')",
                "RESOLVED_RECENTLY" => "t.resolved_at>=UTC_TIMESTAMP(6)-INTERVAL 7 DAY",
                _ => throw new ArgumentException("Choose a valid dashboard filter.")
            });
        var sla = query["slaStatus"].ToString();
        if (sla.Length > 0)
        {
            if (!new[] { "NOT_TRACKED", "MET", "PAUSED", "BREACHED", "AT_RISK", "ON_TRACK" }.Contains(sla))
                throw new ArgumentException("Choose a valid SLA status.");
            args.Add(("@filter_sla", sla));
            terms.Add("(CASE WHEN sla.resolution_due_at IS NULL OR t.status='CANCELLED' THEN 'NOT_TRACKED' WHEN sla.resolution_completed_at IS NOT NULL AND sla.resolution_completed_at<=sla.resolution_due_at THEN 'MET' WHEN sla.paused_at IS NOT NULL THEN 'PAUSED' WHEN UTC_TIMESTAMP(6)>=sla.resolution_due_at THEN 'BREACHED' WHEN UTC_TIMESTAMP(6)>=sla.resolution_warning_at THEN 'AT_RISK' ELSE 'ON_TRACK' END)=@filter_sla");
        }
        return (terms.Count == 0 ? "" : " AND " + string.Join(" AND ", terms), args);
    }
}

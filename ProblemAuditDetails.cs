internal static class ProblemAuditDetails
{
    public static Dictionary<string, object?> Changes(string number,
        IReadOnlyDictionary<string, object?> previous, IReadOnlyDictionary<string, object?> current)
    {
        var before = new Dictionary<string, object?>();
        var after = new Dictionary<string, object?>();
        foreach (var (field, value) in current)
        {
            previous.TryGetValue(field, out var old);
            if (Equals(old, value)) continue;
            before[field] = old;
            after[field] = value;
        }
        return new()
        {
            ["problemNumber"] = number,
            ["before"] = before,
            ["after"] = after,
            ["changedFields"] = after.Keys.ToArray()
        };
    }
}

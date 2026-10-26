using System.Globalization;
using Astra.Api.Security;

namespace Astra.Api.Data;

public static class RequestQuery
{
    public static string Text(IQueryCollection query, string key, string fallback = "", int maximum = 200)
    {
        if (!query.TryGetValue(key, out var value)) return fallback;
        if (value.Count != 1 || value.ToString().Length > maximum) throw new ApiFault(400, "Invalid filter: " + key);
        return value.ToString();
    }
    public static int Integer(IQueryCollection query, string key, int fallback, int maximum)
    {
        var value = Text(query, key, fallback.ToString(CultureInfo.InvariantCulture));
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result < 1 || result > maximum) throw new ApiFault(400, "Invalid filter: " + key);
        return result;
    }
    public static Guid? Id(IQueryCollection query, string key)
    {
        if (!query.ContainsKey(key)) return null;
        if (!Guid.TryParse(Text(query, key), out var result)) throw new ApiFault(400, "Invalid filter: " + key);
        return result;
    }
    public static void Only(IQueryCollection query, params string[] allowed)
    {
        if (query.Keys.Any(k => !allowed.Contains(k, StringComparer.Ordinal))) throw new ApiFault(400, "Invalid filters.");
    }
    public static string Like(string value) => "%" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal) + "%";
}

using System.Text.Json;
using MySqlConnector;

namespace Astra.Shared.Data;

public static class Audit
{
    public static Task WriteAsync(MySqlConnection connection, MySqlTransaction transaction, Guid actor, string action, string type, Guid entity, object details, CancellationToken cancellationToken = default) => Database.ExecuteAsync(connection, "INSERT INTO audit_logs(actor_user_id,action,entity_type,entity_id,details) VALUES(@actor,@action,@type,@entity,@details)", new Dictionary<string, object?> { ["actor"] = actor, ["action"] = action, ["type"] = type, ["entity"] = entity, ["details"] = JsonSerializer.Serialize(details, JsonSerializerOptions.Web) }, transaction, cancellationToken);
}

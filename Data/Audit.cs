using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace Astra.Api.Data;

public static class Audit
{
    public static Task WriteAsync(SqlConnection connection, SqlTransaction transaction, Guid actor, string action, string type, Guid entity, object details, CancellationToken cancellationToken = default) => Database.ExecuteAsync(connection, "INSERT dbo.audit_logs(actor_user_id,action,entity_type,entity_id,details) VALUES(@actor,@action,@type,@entity,@details)", new Dictionary<string, object?> { ["actor"] = actor, ["action"] = action, ["type"] = type, ["entity"] = entity, ["details"] = JsonSerializer.Serialize(details, JsonSerializerOptions.Web) }, transaction, cancellationToken);
}

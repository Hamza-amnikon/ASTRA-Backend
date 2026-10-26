using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StackExchange.Redis;

namespace Astra.Api.Security;

public sealed record SessionRecord(Guid UserId, string CsrfToken, int Version = 0);
public interface ISessionStore
{
    Task<SessionRecord?> ReadAsync(string token);
    Task PutAsync(string token, SessionRecord session, TimeSpan lifetime);
    Task DeleteAsync(string token);
    Task CheckAsync();
}

public sealed class RedisSessionStore(IConfiguration config) : ISessionStore, IAsyncDisposable
{
    private readonly Lazy<Task<ConnectionMultiplexer>> connection = new(() =>
    {
        var value = config["REDIS_URL"];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("redis" or "rediss")) throw new InvalidOperationException("Redis is not configured.");
        var credentials = uri.UserInfo.Split(':', 2);
        var options = new ConfigurationOptions { AbortOnConnectFail = false, ConnectTimeout = 5000, AsyncTimeout = 5000, Ssl = uri.Scheme == "rediss", SslHost = uri.Host, ClientName = "ASTRA .NET session store" };
        options.EndPoints.Add(uri.Host, uri.IsDefaultPort ? 6379 : uri.Port);
        if (credentials.Length == 2)
        {
            options.User = string.IsNullOrEmpty(credentials[0]) ? null : Uri.UnescapeDataString(credentials[0]);
            options.Password = Uri.UnescapeDataString(credentials[1]);
        }
        if (int.TryParse(uri.AbsolutePath.Trim('/'), out var database)) options.DefaultDatabase = database;
        return ConnectionMultiplexer.ConnectAsync(options);
    });

    private static string Key(string token) => "session:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    public async Task<SessionRecord?> ReadAsync(string token)
    {
        var value = await (await connection.Value).GetDatabase().StringGetAsync(Key(token));
        if (value.IsNullOrEmpty) return null;
        try { return JsonSerializer.Deserialize<SessionRecord>(value.ToString(), JsonSerializerOptions.Web); }
        catch (JsonException) { return null; }
    }
    public async Task PutAsync(string token, SessionRecord session, TimeSpan lifetime) => await (await connection.Value).GetDatabase().StringSetAsync(Key(token), JsonSerializer.Serialize(session, JsonSerializerOptions.Web), lifetime);
    public async Task DeleteAsync(string token) => await (await connection.Value).GetDatabase().KeyDeleteAsync(Key(token));
    public async Task CheckAsync() => await (await connection.Value).GetDatabase().PingAsync();
    public async ValueTask DisposeAsync() { if (connection.IsValueCreated) await (await connection.Value).DisposeAsync(); }
}

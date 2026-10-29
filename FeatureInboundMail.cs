using System.Net.Http.Headers;
using System.Text.Json;

public sealed record InboxMessage(string Id, string Sender, string Subject, string Body, DateTime ReceivedAt, string? ProviderId = null);
public sealed record InboxPage(InboxMessage[] Messages, string? Next);
public sealed record InboxCheckpoint(string? Next, DateTime Watermark);
public interface IMailInbox
{
    Task<InboxPage> ReadAsync(string mailbox, string? cursor, DateTime since, CancellationToken ct);
    Task<InboxAttachmentImport> ReadAttachmentsAsync(string mailbox, string providerMessageId, CancellationToken ct);
}

public sealed partial class GraphMailInbox(IConfiguration config, HttpClient? httpClient = null) : IMailInbox, IDisposable
{
    private readonly HttpClient client = httpClient ?? new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
    public async Task<InboxPage> ReadAsync(string mailbox, string? cursor, DateTime since, CancellationToken ct)
    {
        var token = await AccessToken(ct);
        var root = "https://graph.microsoft.com/v1.0/users/" + Uri.EscapeDataString(mailbox) + "/mailFolders/inbox/messages";
        var url = cursor is not null && IsContinuation(cursor, root) ? cursor : root + "?$top=10&$orderby=receivedDateTime%20asc&$filter=" + Uri.EscapeDataString("receivedDateTime ge " + since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")) + "&$select=id,internetMessageId,from,subject,body,receivedDateTime";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Prefer", "outlook.body-content-type=\"text\", IdType=\"ImmutableId\"");
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new FeatureFailure(502, response.StatusCode == System.Net.HttpStatusCode.Forbidden ? "GRAPH_MAILBOX_FORBIDDEN" : "GRAPH_READ_FAILED", "Microsoft mailbox reading failed. Check application mail permissions and mailbox scope.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var messages = new List<InboxMessage>();
        foreach (var item in json.RootElement.GetProperty("value").EnumerateArray())
        {
            var provider = item.GetProperty("id").GetString() ?? "";
            if (provider.Length == 0)
                continue;
            var id = item.TryGetProperty("internetMessageId", out var internet) ? internet.GetString() : null;
            if (string.IsNullOrEmpty(id) || id.Length > 255)
                id = "<graph-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(provider))).ToLowerInvariant() + "@import.invalid>";
            var sender = item.TryGetProperty("from", out var from) && from.TryGetProperty("emailAddress", out var address) && address.TryGetProperty("address", out var value) ? value.GetString() ?? "" : "";
            sender = sender.Trim().ToLowerInvariant();
            if (!System.Net.Mail.MailAddress.TryCreate(sender, out _))
                sender = "unknown@import.invalid";
            var subject = item.TryGetProperty("subject", out var sub) ? sub.GetString() ?? "" : "";
            var body = item.TryGetProperty("body", out var content) && content.TryGetProperty("content", out var text) ? text.GetString() ?? "" : "";
            if (body.Length > 100000)
                body = body[..100000] + "\n[Message shortened; see the original mailbox.]";
            var received = item.GetProperty("receivedDateTime").GetDateTime().ToUniversalTime();
            messages.Add(new(id, sender, subject, body, received, provider));
        }
        var next = json.RootElement.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        if (next is not null && !IsContinuation(next, root))
            throw new FeatureFailure(502, "GRAPH_CURSOR_INVALID", "Microsoft returned an unexpected mailbox continuation link.");
        return new(messages.ToArray(), next);
    }
    public void Dispose()
    {
        if (httpClient is null)
            client.Dispose();
        tokenGate.Dispose();
    }
}
public sealed class FeatureInboxWorker(MySqlStore store, IConfiguration config, ILogger<FeatureInboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.GetConnectionString("MySql")))
            return;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (config.GetValue("ASTRA_MAIL_WORKER_ENABLED", true) && config.GetValue("ASTRA_MAIL_INBOUND_ENABLED", true))
                    await store.SyncInboxes(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch { logger.LogWarning("Mailbox synchronization could not complete. Check Email administration for provider status."); }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }
}
public sealed partial class MySqlStore
{
    public async Task SyncInboxes(CancellationToken ct)
    {
        var settings = await MailSettingsRow(ct);
        if (!FBool(settings["enabled"]) || (string?)settings["inbound_provider"] != "MICROSOFT_365")
            return;
        foreach (var (column, channel) in new[] { ("ticket_address", "TICKET"), ("change_address", "CHANGE") })
        {
            var mailbox = ValidEmail((string?)settings[column], "mailbox");
            await using var c = CreateConnection();
            await c.OpenAsync(ct);
            var lockName = "astra-inbox-" + SupportHash(c.Database + ":" + mailbox)[..48];
            if (FInt((await FRows(c, null, "SELECT GET_LOCK(@name,0) AS acquired", ct, ("name", lockName)))[0]["acquired"]) != 1)
                continue;
            try
            {
                var state = (await FRows(c, null, "SELECT * FROM mail_sync_state WHERE mailbox=@mailbox", ct, ("mailbox", mailbox))).FirstOrDefault();
                var since = state?.GetValueOrDefault("last_sync_at") as DateTime? ?? settings["inbound_since"] as DateTime? ?? DateTime.UtcNow;
                string? cursor = null;
                if (state?.GetValueOrDefault("cursor") is string saved)
                {
                    try
                    {
                        var checkpoint = JsonSerializer.Deserialize<InboxCheckpoint>(saved);
                        if (checkpoint is not null)
                        {
                            since = checkpoint.Watermark;
                            cursor = checkpoint.Next;
                        }
                    }
                    catch (JsonException) { if (saved.StartsWith("https://graph.microsoft.com/", StringComparison.Ordinal)) cursor = saved; }
                }
                if (settings["inbound_since"] is DateTime boundary && since < boundary)
                    since = boundary;
                InboxPage page;
                var settingsChanged = false;
                try
                {
                    page = await (mailInbox ?? throw new FeatureFailure(503, "GRAPH_NOT_CONFIGURED", "Mailbox reader is not configured.")).ReadAsync(mailbox, cursor, since.AddMinutes(-2), ct);
                    foreach (var message in page.Messages.DistinctBy(m => m.Id))
                    {
                        // Commit one message with its files at a time, bounding memory to one message.
                        // The page checkpoint advances only after every message succeeds.
                        if (!await ImportInboxMessage(c, settings, column, channel, mailbox, message, ct))
                        {
                            settingsChanged = true;
                            break;
                        }
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    await using var fail = await c.BeginTransactionAsync(ct);
                    await FExecute(c, fail, "INSERT INTO mail_sync_state(mailbox,last_error_code) VALUES(@mailbox,@error) ON DUPLICATE KEY UPDATE last_error_code=@error", ct, ("mailbox", mailbox), ("error", e is FeatureFailure failure ? failure.Code : e is MySqlConnector.MySqlException ? "MAIL_STORAGE_FAILED" : "GRAPH_READ_FAILED"));
                    await fail.CommitAsync(ct);
                    continue;
                }
                if (settingsChanged)
                    continue;
                await using var tx = await c.BeginTransactionAsync(ct);
                var current = (await FRows(c, tx, "SELECT * FROM mail_settings WHERE id=1 FOR SHARE", ct)).First();
                if (!FBool(current["enabled"]) || !Equals(current[column], mailbox) || !Equals(current["inbound_provider"], "MICROSOFT_365") || !Equals(current["inbound_since"], settings["inbound_since"]))
                    continue;
                var latest = page.Messages.Length > 0 ? page.Messages.Max(x => x.ReceivedAt) : since;
                if (latest < since)
                    latest = since;
                await FExecute(c, tx, "INSERT INTO mail_sync_state(mailbox,`cursor`,last_sync_at,last_error_code) VALUES(@mailbox,@cursor,UTC_TIMESTAMP(6),NULL) ON DUPLICATE KEY UPDATE `cursor`=@cursor,last_sync_at=UTC_TIMESTAMP(6),last_error_code=NULL", ct, ("mailbox", mailbox), ("cursor", FJson(new InboxCheckpoint(page.Next, latest))));
                await tx.CommitAsync(ct);
            }
            finally { await FRows(c, null, "SELECT RELEASE_LOCK(@name)", CancellationToken.None, ("name", lockName)); }
        }
    }
}

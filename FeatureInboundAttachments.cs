using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MySqlConnector;

public sealed record InboxAttachment(string ProviderId, string FileName, string ContentType, byte[] Content);
public sealed record InboxAttachmentImport(InboxAttachment[] Files, string[] Warnings);

public static class InboundMailFiles
{
    public const int MaxFiles = 10;
    public const int MaxFileBytes = 5 * 1024 * 1024;
    public const int MaxTotalBytes = 25 * 1024 * 1024;
    public const string LimitWarning = "Some files were left in the mailbox because the message exceeds the limit of 10 files, 5 MB per file, or 25 MB in total.";
    public const string TypeWarning = "Some unsupported files or embedded messages were left in the original mailbox.";
    public const string InvalidWarning = "Some files could not be imported because their names or content were invalid. See the original mailbox.";

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".txt"] = "text/plain",
        [".log"] = "text/plain",
        [".csv"] = "text/csv",
        [".zip"] = "application/zip",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation"
    };
    public static string? ContentType(string name, string? type)
    {
        if (name.Length is < 1 or > 180 || name is "." or ".." || name.Contains('/') || name.Contains('\\') || name.Any(char.IsControl) || !Types.TryGetValue(Path.GetExtension(name), out var expected))
            return null;
        type = type?.Split(';')[0].Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(type) || type == "application/octet-stream" || type == expected ? expected : null;
    }
    public static InboxAttachmentImport Validate(InboxAttachmentImport batch)
    {
        var files = new List<InboxAttachment>();
        var warnings = new HashSet<string>(batch.Warnings, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        foreach (var file in batch.Files)
        {
            var type = ContentType(file.FileName, file.ContentType);
            if (type is null || string.IsNullOrWhiteSpace(file.ProviderId))
            {
                warnings.Add(TypeWarning);
                continue;
            }
            if (!seen.Add(file.ProviderId))
                continue;
            if (file.Content.Length == 0)
            {
                warnings.Add(InvalidWarning);
                continue;
            }
            if (file.Content.Length > MaxFileBytes || files.Count >= MaxFiles || file.Content.Length > MaxTotalBytes - total)
            {
                warnings.Add(LimitWarning);
                continue;
            }
            files.Add(file with
            {
                ContentType = type
            });
            total += file.Content.Length;
        }
        return new(files.ToArray(), warnings.Order(StringComparer.Ordinal).ToArray());
    }
}

public sealed partial class GraphMailInbox
{
    private readonly SemaphoreSlim tokenGate = new(1, 1);
    private string? cachedToken, credentialFingerprint;
    private DateTimeOffset tokenExpires;
    private async Task<string> AccessToken(CancellationToken ct)
    {
        var auth = AuthSettings.Load(config);
        if (!Guid.TryParse(auth.TenantId, out var tenant) || !Guid.TryParse(auth.ClientId, out _) || string.IsNullOrWhiteSpace(auth.ClientSecret))
            throw new FeatureFailure(503, "GRAPH_NOT_CONFIGURED", "Configure Microsoft mailbox application credentials.");
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(auth.TenantId + ":" + auth.ClientId + ":" + auth.ClientSecret)));
        await tokenGate.WaitAsync(ct);
        try
        {
            if (cachedToken is not null && credentialFingerprint == fingerprint && tokenExpires > DateTimeOffset.UtcNow)
                return cachedToken;
            using var response = await client.PostAsync($"https://login.microsoftonline.com/{tenant:D}/oauth2/v2.0/token", new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = auth.ClientId, ["client_secret"] = auth.ClientSecret, ["scope"] = "https://graph.microsoft.com/.default", ["grant_type"] = "client_credentials" }), ct);
            if (!response.IsSuccessStatusCode)
                throw new FeatureFailure(502, "GRAPH_AUTH_FAILED", "Microsoft mailbox authentication failed.");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            cachedToken = json.RootElement.GetProperty("access_token").GetString();
            if (string.IsNullOrWhiteSpace(cachedToken))
                throw new FeatureFailure(502, "GRAPH_AUTH_FAILED", "Microsoft mailbox authentication failed.");
            var lifetime = json.RootElement.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds) ? seconds : 3600;
            tokenExpires = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(lifetime - 120, 1, 3480));
            credentialFingerprint = fingerprint;
            return cachedToken;
        }
        finally { tokenGate.Release(); }
    }
    public async Task<InboxAttachmentImport> ReadAttachmentsAsync(string mailbox, string providerMessageId, CancellationToken ct)
    {
        if (providerMessageId is "" or "." or ".." || providerMessageId.Length > 4096)
            throw new FeatureFailure(502, "GRAPH_MESSAGE_INVALID", "Microsoft returned an invalid message identifier.");
        var token = await AccessToken(ct);
        var root = "https://graph.microsoft.com/v1.0/users/" + Uri.EscapeDataString(mailbox) + "/messages/" + Uri.EscapeDataString(providerMessageId) + "/attachments";
        string? next = root + "?$top=25&$select=id,name,contentType,size,isInline";
        var files = new List<InboxAttachment>();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var scanned = 0;
        var total = 0;
        var pages = 0;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (next is not null)
        {
            if (!IsContinuation(next, root))
                throw new FeatureFailure(502, "GRAPH_CURSOR_INVALID", "Microsoft returned an unexpected attachment continuation link.");
            if (++pages > 10 || !visited.Add(next))
            {
                warnings.Add(InboundMailFiles.LimitWarning);
                break;
            }
            using var request = AttachmentRequest(next, token);
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                warnings.Add(InboundMailFiles.InvalidWarning);
                break;
            }
            if (!response.IsSuccessStatusCode)
                throw new FeatureFailure(502, "GRAPH_ATTACHMENT_READ_FAILED", "Email files could not be read. ASTRA will retry without advancing the mailbox checkpoint.");
            using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            foreach (var metadata in page.RootElement.GetProperty("value").EnumerateArray())
            {
                if (++scanned > 100 || files.Count >= InboundMailFiles.MaxFiles)
                {
                    warnings.Add(InboundMailFiles.LimitWarning);
                    next = null;
                    break;
                }
                if (Text(metadata, "@odata.type").TrimStart('#') != "microsoft.graph.fileAttachment")
                {
                    warnings.Add(InboundMailFiles.TypeWarning);
                    continue;
                }
                var id = Text(metadata, "id");
                var name = Text(metadata, "name");
                var type = InboundMailFiles.ContentType(name, Text(metadata, "contentType"));
                if (id is "" or "." or ".." || id.Length > 4096 || type is null)
                {
                    warnings.Add(InboundMailFiles.TypeWarning);
                    continue;
                }
                if (!seen.Add(id))
                    continue;
                if (metadata.TryGetProperty("size", out var size))
                {
                    if (size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var length) || length < 0)
                    {
                        warnings.Add(InboundMailFiles.InvalidWarning);
                        continue;
                    }
                    if (length > InboundMailFiles.MaxFileBytes || length > InboundMailFiles.MaxTotalBytes - total)
                    {
                        warnings.Add(InboundMailFiles.LimitWarning);
                        continue;
                    }
                }
                using var fileRequest = AttachmentRequest(root + "/" + Uri.EscapeDataString(id), token);
                using var fileResponse = await client.SendAsync(fileRequest, HttpCompletionOption.ResponseHeadersRead, ct);
                if (fileResponse.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    warnings.Add(InboundMailFiles.InvalidWarning);
                    continue;
                }
                if (!fileResponse.IsSuccessStatusCode)
                    throw new FeatureFailure(502, "GRAPH_ATTACHMENT_READ_FAILED", "Email files could not be read. ASTRA will retry without advancing the mailbox checkpoint.");
                using var content = await AttachmentJson(fileResponse, ct);
                if (content is null)
                {
                    warnings.Add(InboundMailFiles.LimitWarning);
                    continue;
                }
                var row = content.RootElement;
                type = InboundMailFiles.ContentType(Text(row, "name"), Text(row, "contentType"));
                var encoded = Text(row, "contentBytes");
                if (Text(row, "@odata.type").TrimStart('#') != "microsoft.graph.fileAttachment" || Text(row, "id") != id || type is null || encoded.Length == 0)
                {
                    warnings.Add(InboundMailFiles.InvalidWarning);
                    continue;
                }
                if (encoded.Length > ((InboundMailFiles.MaxFileBytes + 2) / 3) * 4)
                {
                    warnings.Add(InboundMailFiles.LimitWarning);
                    continue;
                }
                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(encoded);
                }
                catch (FormatException) { warnings.Add(InboundMailFiles.InvalidWarning); continue; }
                if (bytes.Length == 0)
                {
                    warnings.Add(InboundMailFiles.InvalidWarning);
                    continue;
                }
                if (bytes.Length > InboundMailFiles.MaxFileBytes || bytes.Length > InboundMailFiles.MaxTotalBytes - total)
                {
                    warnings.Add(InboundMailFiles.LimitWarning);
                    continue;
                }
                files.Add(new(id, Text(row, "name"), type, bytes));
                total += bytes.Length;
            }
            if (scanned > 100)
                break;
            if (files.Count >= InboundMailFiles.MaxFiles)
            {
                if (page.RootElement.TryGetProperty("@odata.nextLink", out _))
                    warnings.Add(InboundMailFiles.LimitWarning);
                break;
            }
            next = page.RootElement.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }
        return new(files.ToArray(), warnings.Order(StringComparer.Ordinal).ToArray());
    }
    private static string Text(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static bool IsContinuation(string value, string root)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "graph.microsoft.com" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.Query.Length > 0 && Uri.UnescapeDataString(uri.AbsolutePath) == Uri.UnescapeDataString(new Uri(root).AbsolutePath);
    }
    private static async Task<JsonDocument?> AttachmentJson(HttpResponseMessage response, CancellationToken ct)
    {
        const int limit = ((InboundMailFiles.MaxFileBytes + 2) / 3) * 4 + 128 * 1024;
        if (response.Content.Headers.ContentLength > limit)
            return null;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > limit)
                return null;
            output.Write(buffer, 0, count);
        }
        return JsonDocument.Parse(output.GetBuffer().AsMemory(0, (int)output.Length));
    }
    private static HttpRequestMessage AttachmentRequest(string url, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Prefer", "IdType=\"ImmutableId\"");
        return request;
    }
}

public sealed partial class MySqlStore
{
    private async Task<bool> ImportInboxMessage(MySqlConnection c, Dictionary<string, object?> settings, string column, string channel, string mailbox, InboxMessage message, CancellationToken ct)
    {
        var imported = await FRows(c, null, "SELECT b.message_id FROM mail_messages m JOIN astra_mail_attachment_batches b ON b.message_id=m.id WHERE m.mailbox=@mailbox AND m.message_id=@message", ct, ("mailbox", mailbox), ("message", message.Id));
        if (imported.Count > 0)
            return true;
        InboxAttachmentImport? batch = null;
        if (message.ProviderId is not null)
            batch = InboundMailFiles.Validate(await mailInbox!.ReadAttachmentsAsync(mailbox, message.ProviderId, ct));
        await using var tx = await c.BeginTransactionAsync(ct);
        var current = (await FRows(c, tx, "SELECT * FROM mail_settings WHERE id=1 FOR SHARE", ct)).Single();
        if (!FBool(current["enabled"]) || !Equals(current[column], mailbox) || !Equals(current["inbound_provider"], "MICROSOFT_365") || !Equals(current["inbound_since"], settings["inbound_since"]))
            return false;
        // Incoming text and files remain under administrator review; no sender is trusted automatically.
        await FExecute(c, tx, "INSERT INTO mail_messages(direction,channel,mailbox,message_id,sender_address,recipient_addresses,subject,body,state,last_error_code,created_at) VALUES('INBOUND',@channel,@mailbox,@message,@sender,JSON_ARRAY(@mailbox),@subject,@body,'REVIEW','INBOUND_REVIEW_REQUIRED',@received) ON DUPLICATE KEY UPDATE id=id", ct, ("channel", channel), ("mailbox", mailbox), ("message", message.Id), ("sender", message.Sender), ("subject", message.Subject), ("body", message.Body), ("received", message.ReceivedAt));
        if (batch is not null)
        {
            var row = (await FRows(c, tx, "SELECT id FROM mail_messages WHERE mailbox=@mailbox AND message_id=@message FOR UPDATE", ct, ("mailbox", mailbox), ("message", message.Id))).Single();
            await SaveInboundFiles(c, tx, (string)row["id"]!, batch, ct);
        }
        await tx.CommitAsync(ct);
        return true;
    }
    private static async Task SaveInboundFiles(MySqlConnection c, MySqlTransaction tx, string message, InboxAttachmentImport batch, CancellationToken ct)
    {
        if ((await FRows(c, tx, "SELECT message_id FROM astra_mail_attachment_batches WHERE message_id=@message", ct, ("message", message))).Count > 0)
            return;
        var existing = await FRows(c, tx, "SELECT id,file_name,content_type,content FROM mail_attachments WHERE message_id=@message ORDER BY id FOR UPDATE", ct, ("message", message));
        foreach (var file in batch.Files)
        {
            // Match each legacy file once so backfilling does not duplicate retained attachments.
            var match = existing.FirstOrDefault(row => Equals(row["file_name"], file.FileName) && InboundMailFiles.ContentType((string)row["file_name"]!, (string?)row["content_type"]) == file.ContentType && ((byte[])row["content"]!).AsSpan().SequenceEqual(file.Content));
            var id = match is null ? Guid.NewGuid().ToString("D") : (string)match["id"]!;
            if (match is not null)
                existing.Remove(match);
            else
                await FExecute(c, tx, "INSERT INTO mail_attachments(id,message_id,file_name,content_type,content,size_bytes) VALUES(@id,@message,@name,@type,@content,@size)", ct, ("id", id), ("message", message), ("name", file.FileName), ("type", file.ContentType), ("content", file.Content), ("size", file.Content.Length));
            await FExecute(c, tx, "INSERT INTO astra_mail_attachment_integrity(attachment_id,sha256) VALUES(@id,@hash) ON DUPLICATE KEY UPDATE sha256=sha256", ct, ("id", id), ("hash", Convert.ToHexString(SHA256.HashData(file.Content)).ToLowerInvariant()));
        }
        await FExecute(c, tx, "INSERT INTO astra_mail_attachment_batches(message_id,warnings) VALUES(@message,CAST(@warnings AS JSON))", ct, ("message", message), ("warnings", FJson(batch.Warnings)));
    }
}

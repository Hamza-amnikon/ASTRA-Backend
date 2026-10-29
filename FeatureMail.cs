using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MailKit.Security;
using MimeKit;

public sealed record MailConnection(string Host, int Port, string TlsMode, string Username, string Password, string Sender);
public interface IMailTransport
{
    Task VerifyAsync(MailConnection connection, CancellationToken ct);
    Task SendAsync(MailConnection connection, string[] recipients, string subject, string body, CancellationToken ct, string? messageId = null);
}
public sealed class MailKitTransport : IMailTransport
{
    private static async Task<MailKit.Net.Smtp.SmtpClient> Connect(MailConnection connection, CancellationToken ct)
    {
        var client = new MailKit.Net.Smtp.SmtpClient { Timeout = 15000, SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13 };
        try
        {
            await client.ConnectAsync(connection.Host, connection.Port, connection.TlsMode == "TLS" ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct);
            await client.AuthenticateAsync(connection.Username, connection.Password, ct);
            return client;
        }
        catch { client.Dispose(); throw; }
    }
    public async Task VerifyAsync(MailConnection connection, CancellationToken ct)
    {
        using var client = await Connect(connection, ct);
        await client.DisconnectAsync(true, ct);
    }
    public async Task SendAsync(MailConnection connection, string[] recipients, string subject, string body, CancellationToken ct, string? messageId = null)
    {
        using var client = await Connect(connection, ct);
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(connection.Sender));
        foreach (var email in recipients)
            message.To.Add(MailboxAddress.Parse(email));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };
        if (messageId is not null)
            message.MessageId = messageId.Trim('<', '>');
        await client.SendAsync(message, ct);
        await client.DisconnectAsync(true, ct);
    }
}
public static partial class FeatureApis
{
    private static void MapMail(RouteGroupBuilder api)
    {
        api.MapGet("/admin/email", async (HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.EmailSettings(Actor(c), t)));
        api.MapPatch("/admin/email", async (JsonElement body, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.SaveEmailSettings(Actor(c), body, t)));
        api.MapGet("/admin/email/status", async (HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.EmailStatus(Actor(c), t)));
        api.MapPost("/admin/email/verify", async (HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.VerifyEmail(Actor(c), t)));
        api.MapPost("/admin/email/test", async (JsonElement body, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.TestEmail(Actor(c), body, t)));
        api.MapGet("/mail/tickets/{id:guid}", async (string id, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.MailTimeline(Actor(c), "TICKET", id, t)));
        api.MapGet("/mail/changes/{id:guid}", async (string id, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.MailTimeline(Actor(c), "CHANGE", id, t)));
        api.MapGet("/mail/attachments/{id:guid}", async (string id, HttpContext c, MySqlStore s, CancellationToken t) => { var f = await s.MailAttachment(Actor(c), id, t); return Results.File(f.Content, f.ContentType, f.FileName); });
        api.MapGet("/mail/review/{id:guid}", async (string id, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.MailReview(Actor(c), id, t)));
        api.MapPost("/mail/review/{id:guid}", async (string id, JsonElement body, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.SaveMailReview(Actor(c), id, body, t)));
        api.MapPost("/mail/intake/{id:guid}", async (string id, JsonElement body, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.MailIntake(Actor(c), id, body, t)));
        api.MapPost("/changes/{id:guid}/communications/{communicationId:guid}/send", async (string id, string communicationId, HttpContext c, MySqlStore s, CancellationToken t) => Results.Ok(await s.SendChangeCommunication(Actor(c), id, communicationId, t)));
    }
}
public sealed partial class MySqlStore
{
    private IMailTransport MailTransport => mailTransport ?? new MailKitTransport();
    private byte[]? MailConfigKey()
    {
        var key = configuration["MAIL_CONFIG_KEY"];
        if (string.IsNullOrEmpty(key) && configuration["ASTRA_PRIVATE_RUNTIME_SETTINGS_FILE"] is { Length: > 0 } path)
        {
            try
            {
                if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                    return null;
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                if (json.RootElement.TryGetProperty("mailConfigKey", out var value))
                    key = value.GetString();
            }
            catch { return null; }
        }
        try
        {
            return key?.Length == 64 ? Convert.FromHexString(key) : null;
        }
        catch (FormatException) { return null; }
    }
    private string SealMailSecret(string password)
    {
        var key = MailConfigKey() ?? throw new FeatureFailure(503, "MAIL_KEY_MISSING", "Email credential encryption is not configured.");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var bytes = Encoding.UTF8.GetBytes(password);
        var cipher = new byte[bytes.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, bytes, cipher, tag, Encoding.UTF8.GetBytes("amnikon:smtp-password:v1"));
        return string.Join('.', "v1", Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(cipher));
    }
    private string RevealMailSecret(string? encrypted)
    {
        try
        {
            var parts = encrypted?.Split('.') ?? [];
            if (parts.Length != 4 || parts[0] != "v1")
                throw new CryptographicException();
            var key = MailConfigKey() ?? throw new CryptographicException();
            var cipher = Convert.FromBase64String(parts[3]);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(Convert.FromBase64String(parts[1]), cipher, Convert.FromBase64String(parts[2]), plain, Encoding.UTF8.GetBytes("amnikon:smtp-password:v1"));
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is CryptographicException or FormatException) { throw new FeatureFailure(503, "MAIL_CREDENTIALS_UNAVAILABLE", "The saved email credentials cannot be opened. Check the mail encryption key or save new credentials."); }
    }
    private async Task<Dictionary<string, object?>> MailSettingsRow(CancellationToken ct) => (await FQuery("SELECT * FROM mail_settings WHERE id=1", ct)).FirstOrDefault() ?? throw new FeatureFailure(503, "MAIL_NOT_CONFIGURED", "Configure the email connection before using this feature.");
    private static string ValidEmail(string? text, string label)
    {
        text = text?.Trim().ToLowerInvariant() ?? "";
        if (text.Length > 254 || !System.Net.Mail.MailAddress.TryCreate(text, out var address) || address.Address != text || text.Any(char.IsControl))
            throw new ArgumentException($"Enter a valid {label} email address.");
        return text;
    }
    private MailConnection MailConnectionFor(Dictionary<string, object?> row)
    {
        var host = (string)row["smtp_host"]!;
        var port = FInt(row["smtp_port"]);
        var mode = (string)row["tls_mode"]!;
        if (!System.Text.RegularExpressions.Regex.IsMatch(host, "^(?=.{1,253}$)(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\\.)+[a-zA-Z]{2,63}$") || port is not (465 or 587 or 2525) || (port == 465) != (mode == "TLS") || mode is not ("TLS" or "STARTTLS"))
            throw new ArgumentException("Use a valid SMTP hostname and TLS on 465 or STARTTLS on 587/2525.");
        var username = (string?)row["smtp_username"];
        if (string.IsNullOrWhiteSpace(username) || row["password_ciphertext"] is null)
            throw new ArgumentException("Save the SMTP username and password first.");
        return new(host, port, mode, username, RevealMailSecret((string?)row["password_ciphertext"]), ValidEmail((string?)row["ticket_address"], "sender"));
    }
    public async Task<object> EmailSettings(string actor, CancellationToken ct)
    {
        await RequireAdmin(actor, ct);
        var s = await MailSettingsRow(ct);
        var auth = AuthSettings.Load(configuration);
        return new
        {
            enabled = FBool(s["enabled"]),
            smtpHost = s["smtp_host"],
            smtpPort = s["smtp_port"],
            tlsMode = s["tls_mode"],
            smtpUsername = s["smtp_username"],
            passwordSaved = s["password_ciphertext"] is string { Length: > 0 },
            ticketAddress = s["ticket_address"],
            changeAddress = s["change_address"],
            inboundProvider = s["inbound_provider"],
            inboundSince = s["inbound_since"],
            encryptionReady = MailConfigKey() is not null,
            microsoftConfigured = Guid.TryParse(auth.ClientId, out _) && Guid.TryParse(auth.TenantId, out _) && auth.ClientSecret.Length >= 16
        };
    }
    public async Task<object> SaveEmailSettings(string actor, JsonElement b, CancellationToken ct)
    {
        await RequireAdmin(actor, ct);
        var host = FText(JString(b, "smtpHost"), "SMTP host", 1, 253).ToLowerInvariant();
        var port = JInt(b, "smtpPort");
        var mode = JChoice(b, "tlsMode", "STARTTLS", "TLS");
        var username = FText(JString(b, "smtpUsername"), "SMTP username", 0, 300);
        var ticket = ValidEmail(JString(b, "ticketAddress"), "ticket mailbox");
        var change = ValidEmail(JString(b, "changeAddress"), "change mailbox");
        var inbound = JChoice(b, "inboundProvider", "DISABLED", "MICROSOFT_365");
        if (ticket == change)
            throw new ArgumentException("Use separate ticket and change inboxes.");
        if (port is not (465 or 587 or 2525) || (port == 465) != (mode == "TLS") || !System.Text.RegularExpressions.Regex.IsMatch(host, "^(?=.{1,253}$)(?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\\.)+[a-zA-Z]{2,63}$"))
            throw new ArgumentException("Use a valid SMTP hostname and TLS on 465 or STARTTLS on 587/2525.");
        if (inbound == "MICROSOFT_365")
        {
            var auth = AuthSettings.Load(configuration);
            if (!Guid.TryParse(auth.ClientId, out _) || !Guid.TryParse(auth.TenantId, out _) || auth.ClientSecret.Length < 16)
                throw new ArgumentException("Microsoft 365 mailbox access needs Entra application credentials and mailbox-scoped mail-read permission.");
        }
        await using var c = CreateConnection();
        await c.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var before = (await FRows(c, tx, "SELECT * FROM mail_settings WHERE id=1 FOR UPDATE", ct)).FirstOrDefault() ?? throw new KeyNotFoundException();
        var password = JOptional(b, "password");
        if (password is not null)
            FText(password, "Password", 1, 1000);
        if (before["password_ciphertext"] is not null && password is null && (!string.Equals(host, (string?)before["smtp_host"], StringComparison.OrdinalIgnoreCase) || username != (string?)before["smtp_username"]))
            throw new ArgumentException("Enter the provider password when changing the SMTP host or username.");
        var encrypted = password is null ? (string?)before["password_ciphertext"] : SealMailSecret(password);
        if (JBool(b, "enabled") && (encrypted is null || username.Length == 0))
            throw new ArgumentException("Save SMTP credentials before enabling outgoing email.");
        var changed = ticket != (string?)before["ticket_address"] || change != (string?)before["change_address"] || inbound != (string?)before["inbound_provider"];
        await FExecute(c, tx, "UPDATE mail_settings SET enabled=@enabled,smtp_host=@host,smtp_port=@port,tls_mode=@mode,smtp_username=@username,password_ciphertext=@password,ticket_address=@ticket,change_address=@change,inbound_provider=@inbound,inbound_since=IF(@changed=1,UTC_TIMESTAMP(6),inbound_since),updated_at=UTC_TIMESTAMP(6) WHERE id=1", ct, ("enabled", JBool(b, "enabled")), ("host", host), ("port", port), ("mode", mode), ("username", username), ("password", encrypted), ("ticket", ticket), ("change", change), ("inbound", inbound), ("changed", changed));
        if (changed)
        {
            await FExecute(c, tx, "UPDATE mail_sync_state SET `cursor`=NULL,last_sync_at=NULL,last_error_code=NULL", ct);
            await FExecute(c, tx, "UPDATE mail_messages SET state='FAILED',last_error_code='MAILBOX_CHANGED' WHERE state='QUEUED'", ct);
        }
        await AddAudit(c, tx, actor, "EMAIL.SETTINGS_UPDATED", "EMAIL_SETTINGS", "1", new
        {
            enabled = JBool(b, "enabled"),
            smtpHost = host,
            smtpPort = port,
            tlsMode = mode,
            ticketAddress = ticket,
            changeAddress = change,
            inboundProvider = inbound,
            credentialsChanged = password is not null
        }, ct);
        await tx.CommitAsync(ct);
        return new
        {
            saved = true
        };
    }
    public async Task<object> EmailStatus(string actor, CancellationToken ct)
    {
        await RequireAdmin(actor, ct);
        return new
        {
            messages = await FQuery("SELECT id,direction,channel,ticket_id,change_id,mailbox,subject,state,attempts,last_error_code,created_at,processed_at FROM mail_messages ORDER BY created_at DESC LIMIT 100", ct),
            sync = await FQuery("SELECT mailbox,last_sync_at,last_error_code FROM mail_sync_state", ct)
        };
    }
    public async Task<object> VerifyEmail(string actor, CancellationToken ct)
    {
        await RequireAdmin(actor, ct);
        var settings = await MailSettingsRow(ct);
        var connection = MailConnectionFor(settings);
        try
        {
            await MailTransport.VerifyAsync(connection, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException) { throw new FeatureFailure(502, "SMTP_FAILED", "SMTP connection or authentication failed. Check the saved settings and outbound connection rules."); }
        return new
        {
            connected = true,
            message = "SMTP connection and authentication succeeded. Sender permissions and final delivery still depend on your email provider."
        };
    }
    public async Task<object> TestEmail(string actor, JsonElement b, CancellationToken ct)
    {
        await RequireAdmin(actor, ct);
        if (JString(b, "channel") != "TICKET")
            throw new ArgumentException("CAB email is disabled.");
        var settings = await MailSettingsRow(ct);
        if (!FBool(settings["enabled"]))
            throw new ArgumentException("Enable outgoing email before sending a test.");
        var user = (await FQuery("SELECT email FROM users WHERE id=@id", ct, ("id", actor)))[0];
        var recipient = ValidEmail((string?)user["email"], "recipient");
        if (recipient.EndsWith(".invalid", StringComparison.Ordinal))
            throw new ArgumentException("The local test account has no deliverable mailbox. Use a configured staff account to send a test.");
        await using var c = CreateConnection();
        await c.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await FRows(c, tx, "SELECT id FROM users WHERE id=@actor FOR UPDATE", ct, ("actor", actor));
        if ((await FRows(c, tx, "SELECT id FROM mail_messages WHERE channel='TEST' AND created_by_user_id=@actor AND created_at>UTC_TIMESTAMP(6)-INTERVAL 1 MINUTE LIMIT 1", ct, ("actor", actor))).Count > 0)
            throw new FeatureFailure(429, "RATE_LIMITED", "Wait one minute before sending another test.");
        var id = await QueueMail(c, tx, actor, "TEST", (string)settings["ticket_address"]!, [recipient], "ASTRA email configuration test", "This is the test email you requested from ASTRA Service Desk.", null, null, ct);
        await AddAudit(c, tx, actor, "EMAIL.TEST_REQUESTED", "EMAIL", id, new
        {
            recipient
        }, ct);
        await tx.CommitAsync(ct);
        return new
        {
            queued = true,
            recipient
        };
    }
    private static async Task<string> QueueMail(MySqlConnector.MySqlConnection c, MySqlConnector.MySqlTransaction tx, string? actor, string channel, string sender, string[] recipients, string subject, string body, string? ticket, string? change, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("D");
        await FExecute(c, tx, "INSERT INTO mail_messages(id,direction,channel,mailbox,message_id,sender_address,recipient_addresses,subject,body,state,created_by_user_id,ticket_id,change_id) VALUES(@id,'OUTBOUND',@channel,@sender,@message,@sender,CAST(@recipients AS JSON),@subject,@body,'QUEUED',@actor,@ticket,@change)", ct, ("id", id), ("channel", channel), ("sender", sender), ("message", "<astra-dotnet-" + Guid.NewGuid().ToString("N") + "@" + sender.Split('@').Last() + ">"), ("recipients", FJson(recipients)), ("subject", subject), ("body", body), ("actor", actor), ("ticket", ticket), ("change", change));
        return id;
    }
    public async Task<object> MailTimeline(string actor, string kind, string id, CancellationToken ct)
    {
        if (kind == "CHANGE")
            await VisibleChange(actor, id, ct);
        else if (await GetTicketDetail(actor, id, ct) is null)
            throw new KeyNotFoundException();
        var roles = await FQuery("SELECT role_code FROM user_roles WHERE user_id=@actor", ct, ("actor", actor));
        if (!roles.Any(x => (string?)x["role_code"] is "INTERN" or "TECHNICIAN" or "TEAM_LEAD" or "MANAGER" or "ADMIN"))
            return Array.Empty<object>();
        return await FQuery("SELECT m.id,m.direction,m.sender_address,m.subject,m.body,m.state,m.created_at,COALESCE((SELECT JSON_ARRAYAGG(JSON_OBJECT('id',a.id,'file_name',a.file_name,'size_bytes',a.size_bytes)) FROM mail_attachments a WHERE a.message_id=m.id),JSON_ARRAY()) AS attachments,COALESCE((SELECT b.warnings FROM astra_mail_attachment_batches b WHERE b.message_id=m.id),JSON_ARRAY()) AS attachment_warnings FROM mail_messages m WHERE " + (kind == "CHANGE" ? "change_id" : "ticket_id") + "=@id ORDER BY m.created_at LIMIT 200", ct, ("id", id));
    }
    public async Task<Dictionary<string, object?>> MailReview(string actor, string id, CancellationToken ct)
    {
        await RequireAdmin(actor, ct);
        var message = (await FQuery("SELECT id,direction,channel,ticket_id,change_id,mailbox,sender_address,subject,body,state,attempts,last_error_code,created_at FROM mail_messages WHERE id=@id", ct, ("id", id))).FirstOrDefault() ?? throw new KeyNotFoundException();
        message["attachments"] = await FQuery("SELECT id,file_name,content_type,size_bytes FROM mail_attachments WHERE message_id=@id ORDER BY created_at,id", ct, ("id", id));
        message["attachment_warnings"] = (await FQuery("SELECT warnings FROM astra_mail_attachment_batches WHERE message_id=@id", ct, ("id", id))).FirstOrDefault()?.GetValueOrDefault("warnings") ?? System.Text.Json.JsonSerializer.SerializeToElement(Array.Empty<string>());
        return message;
    }
    public async Task<TicketAttachmentFile> MailAttachment(string actor, string id, CancellationToken ct)
    {
        var row = (await FQuery("SELECT a.*,m.ticket_id,m.change_id,i.sha256 FROM mail_attachments a JOIN mail_messages m ON m.id=a.message_id LEFT JOIN astra_mail_attachment_integrity i ON i.attachment_id=a.id WHERE a.id=@id", ct, ("id", id))).FirstOrDefault() ?? throw new KeyNotFoundException();
        if (row["ticket_id"] is string ticket)
            await MailTimeline(actor, "TICKET", ticket, ct);
        else if (row["change_id"] is string change)
            await MailTimeline(actor, "CHANGE", change, ct);
        else
            await RequireAdmin(actor, ct);
        var roles = await FQuery("SELECT role_code FROM user_roles WHERE user_id=@actor", ct, ("actor", actor));
        if (!roles.Any(x => (string?)x["role_code"] is "INTERN" or "TECHNICIAN" or "TEAM_LEAD" or "MANAGER" or "ADMIN"))
            throw new UnauthorizedAccessException();
        var bytes = (byte[])row["content"]!;
        var intact = bytes.Length == Convert.ToInt64(row["size_bytes"]);
        if (row["sha256"] is string hash)
        {
            try
            {
                intact &= CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(hash));
            }
            catch (FormatException) { intact = false; }
        }
        if (!intact)
            throw new FeatureFailure(503, "MAIL_ATTACHMENT_INTEGRITY_FAILED", "This email file failed its integrity check. Contact your administrator.");
        return new((string)row["file_name"]!, (string)row["content_type"]!, bytes);
    }
    public async Task<object> SaveMailReview(string actor, string id, JsonElement b, CancellationToken ct)
    {
        await RequireAdmin(actor, ct);
        var action = JChoice(b, "action", "IGNORE", "TICKET", "CHANGE", "RETRY");
        var reason = FText(JString(b, "reason"), "Review reason", 10, 2000);
        var record = JOptional(b, "recordId");
        if (action == "TICKET" && await GetTicketDetail(actor, FGuid(record, "ticket"), ct) is null)
            throw new KeyNotFoundException();
        if (action == "CHANGE")
            await VisibleChange(actor, FGuid(record, "change"), ct);
        await using var c = CreateConnection();
        await c.OpenAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var message = (await FRows(c, tx, "SELECT * FROM mail_messages WHERE id=@id FOR UPDATE", ct, ("id", id))).FirstOrDefault() ?? throw new KeyNotFoundException();
        if (action == "RETRY")
        {
            if ((string?)message["direction"] != "OUTBOUND" || (string?)message["state"] != "FAILED")
                throw new ArgumentException("Only failed outgoing messages can be retried. Review unknown delivery outcomes before resending.");
            await FExecute(c, tx, "UPDATE mail_messages SET state='QUEUED',attempts=0,next_attempt_at=UTC_TIMESTAMP(6),lease_until=NULL,last_error_code=NULL,message_id=@message WHERE id=@id", ct, ("id", id), ("message", "<astra-dotnet-" + Guid.NewGuid().ToString("N") + "@" + ((string)message["mailbox"]!).Split('@').Last() + ">"));
        }
        else
        {
            if ((string?)message["direction"] != "INBOUND" || (string?)message["state"] is not ("REVIEW" or "UNKNOWN"))
                throw new ArgumentException("This message is not awaiting review.");
            if (action is "TICKET" or "CHANGE" && (string?)message["channel"] != action)
                throw new ArgumentException("Choose the record type assigned to this mailbox.");
            await FExecute(c, tx, "UPDATE mail_messages SET state=@state,ticket_id=@ticket,change_id=@change,last_error_code='MANUALLY_REVIEWED',processed_at=UTC_TIMESTAMP(6) WHERE id=@id", ct, ("id", id), ("state", action == "IGNORE" ? "IGNORED" : "RECEIVED"), ("ticket", action == "TICKET" ? record : null), ("change", action == "CHANGE" ? record : null));
        }
        await AddAudit(c, tx, actor, "EMAIL.REVIEWED", "EMAIL", id, new
        {
            action,
            recordId = record,
            reason
        }, ct);
        await tx.CommitAsync(ct);
        return new
        {
            saved = true
        };
    }
    public Task<object> SendChangeCommunication(string actor, string changeId, string communication, CancellationToken ct)
    {
        throw new FeatureFailure(400, "CAB_EMAIL_DISABLED", "CAB email is disabled. Record updates within Change Management.");
    }
}

public sealed class FeatureMailWorker(MySqlStore store, IConfiguration config, ILogger<FeatureMailWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(config.GetConnectionString("MySql")))
            return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (config.GetValue("ASTRA_MAIL_WORKER_ENABLED", true))
                    await store.DispatchNextMail(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch { logger.LogWarning("Email processing could not complete. Check the email status page and database availability."); }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }
}
public sealed partial class MySqlStore
{
    public async Task DispatchNextMail(CancellationToken ct)
    {
        var settings = await MailSettingsRow(ct);
        if (!FBool(settings["enabled"]))
            return;
        var connection = MailConnectionFor(settings);
        await using var c = CreateConnection();
        await c.OpenAsync(ct);
        Dictionary<string, object?>? mail;
        await using (var tx = await c.BeginTransactionAsync(ct))
        {
            // Never replay legacy queued mail. Only explicit new .NET requests enter this worker.
            await FExecute(c, tx, "UPDATE mail_messages SET state='UNKNOWN',last_error_code='DELIVERY_OUTCOME_UNKNOWN',lease_until=NULL WHERE state='SENDING' AND lease_until<UTC_TIMESTAMP(6) AND message_id LIKE '<astra-dotnet-%'", ct);
            mail = (await FRows(c, tx, "SELECT * FROM mail_messages WHERE state='QUEUED' AND direction='OUTBOUND' AND next_attempt_at<=UTC_TIMESTAMP(6) AND message_id LIKE '<astra-dotnet-%' ORDER BY created_at LIMIT 1 FOR UPDATE SKIP LOCKED", ct)).FirstOrDefault();
            if (mail is null)
            {
                await tx.CommitAsync(ct);
                return;
            }
            var sender = (string)(mail["channel"] is "CHANGE" ? settings["change_address"]! : settings["ticket_address"]!);
            if (!string.Equals((string?)mail["mailbox"], sender, StringComparison.OrdinalIgnoreCase) && (mail["ticket_id"] is null || (await FRows(c, tx, "SELECT r.id FROM client_mail_routes r JOIN tickets t ON t.client_id=r.client_id AND t.email_reply_address=r.address WHERE t.id=@ticket AND r.address=@sender AND r.active=1 AND r.outbound_authorized=1", ct, ("ticket", mail["ticket_id"]), ("sender", mail["mailbox"]))).Count == 0))
            {
                await FExecute(c, tx, "UPDATE mail_messages SET state='FAILED',last_error_code='MAILBOX_CHANGED' WHERE id=@id", ct, ("id", mail["id"]));
                await tx.CommitAsync(ct);
                return;
            }
            await FExecute(c, tx, "UPDATE mail_messages SET state='SENDING',attempts=attempts+1,lease_until=UTC_TIMESTAMP(6)+INTERVAL 2 MINUTE WHERE id=@id", ct, ("id", mail["id"]));
            await tx.CommitAsync(ct);
        }
        var state = "SENT";
        string? error = null;
        try
        {
            await MailTransport.SendAsync(connection with
            {
                Sender = (string)mail["mailbox"]!
            }, FStrings(mail["recipient_addresses"]), (string)mail["subject"]!, (string)mail["body"]!, ct, (string)mail["message_id"]!);
        }
        catch (MailKit.Security.AuthenticationException) { state = "FAILED"; error = "SMTP_AUTHENTICATION_FAILED"; }
        catch (MailKit.Net.Smtp.SmtpCommandException) { state = "FAILED"; error = "SMTP_MESSAGE_REJECTED"; }
        catch (Exception e) when (e is not OperationCanceledException) { state = "UNKNOWN"; error = "DELIVERY_OUTCOME_UNKNOWN"; }
        await using var finish = await c.BeginTransactionAsync(ct);
        await FExecute(c, finish, "UPDATE mail_messages SET state=@state,last_error_code=@error,processed_at=IF(@state='SENT',UTC_TIMESTAMP(6),processed_at),lease_until=NULL WHERE id=@id AND state='SENDING'", ct, ("id", mail["id"]), ("state", state), ("error", error));
        if (mail["communication_id"] is not null)
            await FExecute(c, finish, "UPDATE change_communications SET status=@state,last_error_code=@error,attempts=attempts+1,sent_at=IF(@state='SENT',UTC_TIMESTAMP(6),sent_at) WHERE id=@id", ct, ("id", mail["communication_id"]), ("state", state), ("error", error));
        await finish.CommitAsync(ct);
    }
}

# Run the .NET backend locally

This repository uses the .NET 10 SDK and MySQL 8.4. From its root:

```bash
dotnet build
cp .env.example .env
chmod 600 .env
```

If `.env` already exists, keep its private settings instead of copying over it.
`.env` is ignored by Git and is not automatically loaded by ASP.NET Core.
Set `ConnectionStrings__MySql` to a compatible development database. The schema
and behavior scripts are under `Database/Migrations`; review their coverage notes
before applying them to a new empty database. Do not apply schema creation scripts
to your existing populated ticket database.

For a localhost-only preview, configure these private environment settings:

```text
ASPNETCORE_ENVIRONMENT=Development
ASPNETCORE_URLS=http://127.0.0.1:5200
APP_BASE_URL=http://localhost:3200
ASTRA_LOCAL_TEST_LOGIN=true
ASTRA_MAIL_WORKER_ENABLED=false
ASTRA_MAIL_INBOUND_ENABLED=false
ASTRA_AUTH_CALLBACK_READY=false
```

Use shell-safe quoted `export KEY='value'` entries in your private `.env`, then run:

```bash
source .env
dotnet bin/Debug/net10.0/Astra.Api.dll
```

Keep the terminal open. Run the React frontend from
[ASTRA-Frontend](https://github.com/Hamza-amnikon/ASTRA-Frontend/tree/mahinoor)
and select **Enter local test workspace**. This creates a development administrator
in the configured database. Use an isolated development database for this preview.
The test login requires Development, a loopback request, and an allowed origin.
For a forwarded in-app preview, list its exact localhost origin in
`ASTRA_LOCAL_PREVIEW_ORIGINS` and restart the backend.

## Email setup

The startup migration creates the missing `mail_settings` singleton using schema
defaults, so `/admin/email` can display the configuration form in a fresh database.
It preserves existing settings and does not enable delivery.

Set a private `MAIL_CONFIG_KEY` containing 64 random hexadecimal characters before
saving SMTP credentials. Generate a new key with `openssl rand -hex 32`, store it
privately, and retain it for decrypting saved credentials. Do not commit this key.
Configure your provider and mailboxes through the administrator email page.
Verify provider credentials and explicitly enable the appropriate workers when
ready to send or process real email. Do not copy lab secrets into source code.

`/health` checks the process. Database connectivity can be checked in Development
at `/api/migration/database-health`. A healthy process alone does not confirm that
login, database queries, or email delivery work.

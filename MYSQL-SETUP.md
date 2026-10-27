# Astra MySQL setup

The active working copy is `/home/mahinoor/Astra-SPARK-Structure`.

| Machine | Role |
|---|---|
| `172.31.3.13` | React/Vite frontend and ten .NET 10 API hosts |
| `172.31.4.5` | MySQL 8.4, database `astra_ticketing` |

## Current deployment state

The converted backend builds successfully and passed 78 HTTP/database integration checks in a disposable MySQL 8.4 instance. All ten original controller route contracts are preserved. The administrator installed the real schema on 26 October 2026. The MySQL configuration is now active, all ten live API readiness checks passed, and the real application account connects over TLS from the application machine.

Active private configuration is in `ASTRA-BACKEND/Astra_backend/runtime.env`; the prepared copy is `.run/runtime.mysql.env`. It contains the supplied database credential and existing session/login settings; do not commit, print or distribute it. The public application origin is prepared as `http://localhost:8080`, matching the Windows SSH tunnel. `runtime.env.example` contains only placeholders.

## Database administrator step

The database administrator must run the initial schema setup. The application account does not need permanent table-creation privileges.

The prepared `.run/astra-mysql-setup.tar.gz` contains only:

- `astra-mysql-setup.sql`: initial schema, reference configuration and SLA routines.
- `setup-mysql-database.sh`: empty-database checks, timezone loading, import and final account grants.

When SSH between the two servers is refused, relay the package through Windows PowerShell:

```powershell
scp -3 mahinoor@172.31.3.13:/home/mahinoor/Astra-SPARK-Structure/.run/astra-mysql-setup.tar.gz Mahinoor@172.31.4.5:astra-mysql-setup.tar.gz
```

On the database machine, extract into a dedicated folder and run the setup:

```bash
mkdir -p ~/astra-mysql-setup
tar -xzf ~/astra-mysql-setup.tar.gz -C ~/astra-mysql-setup
sudo bash ~/astra-mysql-setup/setup-mysql-database.sh
```

The script requires the existing empty `astra_ticketing` database and `astra_app` account restricted to host `172.31.3.13`. It stops before importing if any tables already exist. Do not rerun after a partial error without inspecting the database first. It does not drop or overwrite existing tables.

The setup loads named timezone data into MySQL's system timezone tables because SLA calculations use IANA timezones. It creates 69 original application tables plus two small transaction-lock/sequence tables, inserts reference configuration and the two existing development identities, and installs the SLA functions. It creates no sample tickets or external customer records. Schema version 24 is written only after both SLA functions are installed.

Final application permissions are `SELECT`, `INSERT`, `UPDATE`, and `DELETE` on `astra_ticketing`, plus `EXECUTE` on the two SLA functions. Temporary schema-level migration privileges are removed if they were granted.

## Application activation

Activation was completed on 26 October 2026. The following checks describe the deployment procedure:

1. Check the real schema version, table count, TLS connection and both SLA routines using `astra_app` from `172.31.3.13`.
2. Activate `.run/runtime.mysql.env` as the backend's private `ASTRA-BACKEND/Astra_backend/runtime.env` without printing its contents.
3. Restart the shared development API launcher. Its ten hosts share one supervisor, so this briefly interrupts all development APIs. The separate applications on ports 5000 and 5033 must not be restarted.
4. Check all ten `/ready` endpoints, then verify login and read-only requests through the actual Vite proxy at port 5174.
5. Confirm the Windows browser at `http://localhost:8080` can load Astra. Do not insert test tickets into the real database without a deliberate live acceptance step.

The React source and original folders `/home/mahinoor/Astra-Ticketing-Frontend` and `/home/mahinoor/Astra.Api` were not changed for this conversion. The private pre-conversion source/configuration archive is `.run/before-mysql-20261026-163727.tar.gz`; it contains secrets and is not a distribution artifact.

## Conversion details

- MySqlConnector 2.6.2 replaces SQL Server and EF SQL Server dependencies. Notification preferences and read markers use the same parameterized SQL boundary as the other modules.
- UUIDs use ASCII `CHAR(36)` and the provider's `Char36` GUID format. Instants are stored as UTC `DATETIME(6)`; calendar dates and times retain their separate types.
- MySQL JSON queries, aggregation, pagination, joined updates, generated identifiers and explicit InnoDB row locks replace SQL Server syntax.
- Global prefix and first-administrator operations serialize using an existing lock row. Problem numbers use a transactional sequence row. Ticket/customer/site sequence operations retain their transaction boundaries.
- The initial schema derives from the original 24 migrations. PostgreSQL trigram/vector indexes are not reproduced as equivalent MySQL indexes; current .NET searches use their existing substring-search behavior.
- MySQL rejects CHECK constraints on certain foreign-key columns with automatic deletion actions. For help-request recipients, change approvers and change stakeholder identity columns, those foreign keys use `RESTRICT` so the original identity CHECK constraints remain enforced. These records must be explicitly reassigned or removed before deleting the referenced account/department/contact.
- Audit reference lookup includes the entity type. Generic audit-detail references with an ambiguous cross-table UUID are left unresolved instead of displaying an incorrect name.
- Development local administrators load real organization scopes when MySQL is configured. This keeps the existing workspace permissions intact while allowing the operational workspace to see newly created organizations. Local login remains Development-only.

## Verification

Completed:

- .NET solution build: 0 warnings, 0 errors.
- All ten original controller route contracts preserved.
- Initial schema, reference seeds, timezone data, SLA routines and final application grants imported in disposable MySQL 8.4.
- 78 successful HTTP/database checks, covering every API host's readiness, local login, reads, administration searches, groups, domains, calendar writes, notifications, ticket creation and updates, public replies, attachments, problem creation/linking/notes, reporting, audit lookup and logout.

The integration harness is `scripts/test-mysql-integration.py`. It refuses to run unless the private local test socket `/tmp/astra-mysql-validation/mysql.sock` exists and never points at `172.31.4.5`. Its records live only in that disposable database; its Redis test sessions use database 15 and are removed by logout. Detailed startup logs remain under `/tmp/astra-mysql-validation`.

Live verification: all ten `/ready` checks passed. Through the actual Vite proxy, a local administrator created one explicitly authorized test requester under the internal organization and ticket `INT-000001` (`TEST — System issue`), id `f05dd4e6-c3b6-4394-8bc8-7ecbf19008db`. Creation returned HTTP 201, a fresh API detail read returned the same ticket, and a separate direct TLS MySQL query confirmed the persisted row in `astra_ticketing` on `172.31.4.5`, with status `NEW` and priority `P3_MEDIUM`.

Still pending: browser acceptance by the user, Microsoft Entra configuration, external integrations and production hosting. Features already absent from the supplied .NET backend, including change management, knowledge/catalogue, support/help, email administration and extended ticket actions such as assignment/transfer/approval/help-request writes, remain outside this conversion. The Vite development server is not a production hosting configuration.

## Ticket-page repair verification

Saved-view GET/POST/DELETE, internal directory/department reads and ticket-authorized email timeline/attachment reads are now implemented. Their frontend proxy mappings are active. Unknown API modules return JSON 501 rather than the Vite HTML entry page. Optional directory and mail response shapes are validated so malformed responses cannot crash the ticket detail.

The expanded isolated HTTP/MySQL suite passed 95 checks, and 7 frontend route/proxy tests passed. Live headless browser verification confirmed My Tickets displays INT-000001 and its subject, the detail page renders, and a deliberately malformed optional-directory response is handled without a page crash. Browser evidence is in .run/live-browser/my-tickets.png and .run/live-browser/system-issue-detail.png. The live verification script is scripts/check-live-ticket-ui.cjs and requires the development password through a private environment variable.

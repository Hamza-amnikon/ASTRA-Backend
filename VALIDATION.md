# ASTRA structure migration validation — 23 October 2026

## MySQL conversion update — 26 October 2026

### Ticket-page repair

The live My Tickets failure was caused by the missing saved-view API. Added account-scoped saved-view list/save/delete endpoints with validation, same-name updates and ownership checks. Live browser verification then exposed a separate detail-page crash: unmapped internal-directory requests fell through to the Vite HTML page and were treated as list data. Added real staff-directory and department reads, ticket-authorized email timeline/attachment reads, their proxy mappings and defensive optional-list validation. Unsupported API modules now return explicit JSON 501 responses in development, matching the existing production fallback.

Verification: .NET build passed with 0 warnings/errors; frontend lint and production build passed; 7 frontend routing/proxy tests passed; 95 HTTP/MySQL checks passed against the disposable database, including saved-view lifecycle/ownership and new page dependencies. All 10 original controller route contracts remain preserved, with the 3 deliberate saved-view additions checked separately. After deployment, headless Chromium against the live Vite/MySQL application verified that My Tickets displays INT-000001 and TEST — System issue, the ticket detail renders, and malformed optional directory data does not crash the page. Neither page produced JavaScript exceptions or failing API requests. Screenshots are under .run/live-browser/.


The backend source now uses MySqlConnector and native MySQL queries. The solution builds with 0 warnings/errors, all ten controller route contracts remain preserved, and 78 real HTTP/database checks passed against a disposable MySQL 8.4 instance. The initial 71-table schema, reference seeds, timezone data, two SLA functions and application grants were validated there.

The account on the real database machine at `172.31.4.5` connects over TLS from `172.31.3.13`. The administrator installed schema version 24 with 71 tables, timezone data and both SLA functions. MySQL configuration was activated and all ten live readiness checks passed. The authorized example ticket `INT-000001` (`TEST — System issue`) was created through the real Vite/API path, read back through the API, and independently confirmed in MySQL. See `MYSQL-SETUP.md` for current instructions, limitations and activation gates.

The remaining sections below record the earlier restructuring validation and its historical limitations.

The separate working copy is available locally in this workspace and on Ubuntu at `/home/mahinoor/Astra-SPARK-Structure`. The original SPARK projects and running ASTRA source folders were not edited. New API services were tested on separate temporary ports and then stopped.

| Check | Result |
|---|---|
| Windows .NET solution build | Passed: 0 warnings, 0 errors |
| Ubuntu .NET solution build | Passed: 0 warnings, 0 errors |
| Windows and Ubuntu Vite production builds | Passed |
| Ubuntu frontend lint | Passed: 0 warnings, 0 errors |
| Frontend routing and real Vite proxy tests | 6 passed on Windows and Ubuntu |
| Original domain-controller HTTP route contracts | All 10 preserved |
| Windows isolated backend runtime checks | All 10 hosts passed health, anonymous rejection, controller isolation and expected unconfigured readiness |
| Ubuntu isolated backend runtime checks | All 10 hosts passed the same checks |
| Original services on 3100, 5000 and 5033 | Health remained OK after working-copy validation |
| Headless browser checks on Windows | Passed login form, local-login request, workspace chooser, workspace navigation, API failure feedback, responsive drawer, dynamic ticket route and absence of browser exceptions |

Browser checks use explicitly test-only responses and a test-only identity. They do not access a live account, fabricate deployed ticket records, or validate a real database. The browser fixtures reside only in `scripts/check-astra-ui.cjs` and are never included in the frontend application bundle.

## Reproduce browser checks

Start the frontend on a separate development port:

```bash
cd ASTRA-FRONTEND/Astra/frontend
npm run dev -- --port 15174
```

In a second terminal in the frontend folder:

```bash
npm run test:ui
```

On Windows the script uses installed Microsoft Edge in headless mode. On Linux install the Playwright Chromium browser first with `npx playwright install chromium`, plus any platform dependencies requested by Playwright. Set `ASTRA_UI_BASE_URL` when using a different local development URL.

Other checks and the module/port map are in `README.md`.

## Still unverified or incomplete in the supplied application

- Real database connectivity, SQL Server schema compatibility and data operations.
- Microsoft Entra configuration, callback registration, live sign-in and authorization with company accounts.
- Integration with a real Redis session store across all ten deployed services.
- Existing frontend API calls absent from the supplied .NET backend, including change management, knowledge/catalogue, support/help, email administration and extended ticket actions.
- MySQL migration, which is not included in this restructuring.
- Production hosting, HTTPS and deployment of this new working copy.

The new shared layout, navigation, login form, dialog and theme use MUI. Existing module-specific native controls and CSS were deliberately retained alongside them. No claim is made that every original ASTRA screen has been redesigned or every feature is live.

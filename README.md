# ASTRA Backend

Current C# / ASP.NET Core .NET 10 backend, with MySQL 8.4 data access and Redis sessions. The React UI is maintained in [ASTRA-Frontend](https://github.com/Hamza-amnikon/ASTRA-Frontend/tree/mahinoor).

## Structure

`ASTRA-BACKEND/Astra_backend/` contains ten API projects, the shared library, the solution, and MySQL migrations. Modules share one database and session infrastructure.

## Build and run

```bash
dotnet build ASTRA-BACKEND/Astra_backend/Astra.slnx
```

Privately copy `ASTRA-BACKEND/Astra_backend/runtime.env.example` to `runtime.env` and configure MySQL, Redis and development authentication. Then run:

```bash
bash scripts/start-astra-backend.sh
```

Services listen on ports 7301–7310. `/health` checks liveness; `/ready` checks dependencies; `/swagger` is available in Development. Secrets and generated build files are excluded from Git.

See MYSQL-SETUP.md for database setup and VALIDATION.md for historical validation and known limitations. Those records describe the combined working copy before the repositories were separated. Do not rerun database initialization against a populated database.

The previous single SQL Server backend is preserved in Git history. This branch contains the current modular MySQL backend.

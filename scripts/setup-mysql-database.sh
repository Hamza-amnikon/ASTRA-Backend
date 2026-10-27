#!/usr/bin/env bash
set -euo pipefail

if [[ "$EUID" -ne 0 ]]; then
    echo 'Run this script with sudo on the database machine.' >&2
    exit 1
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
schema_file="$script_dir/astra-mysql-setup.sql"
if [[ ! -f "$schema_file" ]]; then
    echo 'Keep astra-mysql-setup.sql beside this script.' >&2
    exit 1
fi

table_count="$(mysql --batch --skip-column-names -e "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='astra_ticketing'")"
if [[ "$table_count" != 0 ]]; then
    echo 'astra_ticketing already contains tables. Stopped without changing them.' >&2
    exit 1
fi

database_count="$(mysql --batch --skip-column-names -e "SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name='astra_ticketing'")"
account_count="$(mysql --batch --skip-column-names -e "SELECT COUNT(*) FROM mysql.user WHERE user='astra_app' AND host='172.31.3.13'")"
if [[ "$database_count" != 1 || "$account_count" != 1 ]]; then
    echo 'The existing astra_ticketing database and astra_app@172.31.3.13 account are required.' >&2
    exit 1
fi

# Populate named IANA zones for SLA calculations, including DST and holidays.
mysql_tzinfo_to_sql /usr/share/zoneinfo | mysql mysql
mysql astra_ticketing < "$schema_file"

# Remove only temporary schema-level migration privileges actually granted.
mysql astra_ticketing <<'SQL'
SET @temporary_grants=(SELECT GROUP_CONCAT(PRIVILEGE_TYPE SEPARATOR ',') FROM information_schema.SCHEMA_PRIVILEGES
    WHERE GRANTEE="'astra_app'@'172.31.3.13'" AND TABLE_SCHEMA='astra_ticketing'
      AND PRIVILEGE_TYPE IN ('CREATE','ALTER','INDEX','REFERENCES','CREATE ROUTINE','ALTER ROUTINE','EXECUTE'));
SET @revoke_sql=IF(@temporary_grants IS NULL,'DO 0',CONCAT('REVOKE ',@temporary_grants," ON astra_ticketing.* FROM 'astra_app'@'172.31.3.13'"));
PREPARE revoke_statement FROM @revoke_sql;
EXECUTE revoke_statement;
DEALLOCATE PREPARE revoke_statement;
GRANT SELECT,INSERT,UPDATE,DELETE ON astra_ticketing.* TO 'astra_app'@'172.31.3.13';
GRANT EXECUTE ON FUNCTION astra_ticketing.sla_business_minutes_between TO 'astra_app'@'172.31.3.13';
GRANT EXECUTE ON FUNCTION astra_ticketing.sla_add_business_minutes TO 'astra_app'@'172.31.3.13';
SELECT version FROM app_schema_state WHERE id=1;
SELECT COUNT(*) AS application_tables FROM information_schema.tables WHERE table_schema='astra_ticketing';
SQL
echo 'Astra MySQL setup completed. No tickets or customer records were imported.'

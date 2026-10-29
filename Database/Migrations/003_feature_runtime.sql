-- Supporting runtime state for the .NET customer support verification flow.
-- Existing source records are not modified or removed.
-- Fresh databases need the singleton before administrators can open the mail form.
-- Use schema defaults (outgoing and incoming mail disabled) and retain saved settings.
INSERT INTO mail_settings (id) VALUES (1) ON DUPLICATE KEY UPDATE id = id;
CREATE TABLE IF NOT EXISTS astra_mail_attachment_batches (
  message_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  warnings JSON NOT NULL,
  imported_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  FOREIGN KEY (message_id) REFERENCES mail_messages(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;
CREATE TABLE IF NOT EXISTS astra_mail_attachment_integrity (
  attachment_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  sha256 CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  FOREIGN KEY (attachment_id) REFERENCES mail_attachments(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;
CREATE TABLE IF NOT EXISTS client_mail_routes (
  id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()) PRIMARY KEY,
  client_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  address VARCHAR(254) NOT NULL,
  active BOOLEAN NOT NULL DEFAULT true,
  outbound_authorized BOOLEAN NOT NULL DEFAULT false,
  UNIQUE KEY client_mail_route_address (address),
  FOREIGN KEY (client_id) REFERENCES clients(id) ON DELETE RESTRICT
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;
CREATE TABLE IF NOT EXISTS astra_support_challenges (
  id CHAR(64) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  email VARCHAR(254) NOT NULL,
  domain_rule_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  revision INT,
  code_digest CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  attempts INT NOT NULL DEFAULT 0,
  expires_at DATETIME(6) NOT NULL,
  INDEX astra_support_challenge_expiry (expires_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;
CREATE TABLE IF NOT EXISTS astra_support_sessions (
  id CHAR(64) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  email VARCHAR(254) NOT NULL,
  domain_rule_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  client_id CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  revision INT NOT NULL,
  csrf_token CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  expires_at DATETIME(6) NOT NULL,
  INDEX astra_support_session_expiry (expires_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;
CREATE TABLE IF NOT EXISTS astra_support_rates (
  id CHAR(64) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,
  count INT NOT NULL,
  expires_at DATETIME(6) NOT NULL,
  INDEX astra_support_rate_expiry (expires_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

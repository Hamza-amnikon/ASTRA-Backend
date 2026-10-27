-- ASTRA MySQL 8.4 initial schema. Run once against an EMPTY astra_ticketing database.

-- Derived from the original 24 migrations; contains reference configuration, not tickets.

USE astra_ticketing;

SET NAMES utf8mb4;

SET time_zone = '+00:00';

CREATE TABLE `access_groups` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `name` VARCHAR(320) NOT NULL,
  `description` LONGTEXT NOT NULL DEFAULT (''),
  `kind` VARCHAR(320) NOT NULL,
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `permissions` JSON NOT NULL DEFAULT (JSON_ARRAY()),
  `active` TINYINT(1) NOT NULL DEFAULT 1,
  `revision` INT NOT NULL DEFAULT 1,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `updated_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  UNIQUE (`id`, `client_id`),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `app_schema_state` (
  `id` SMALLINT NOT NULL,
  `version` INT NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `assets` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `site_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `name` VARCHAR(320) NOT NULL,
  `asset_type` VARCHAR(320) NOT NULL,
  `serial_number` VARCHAR(320) NULL,
  `external_reference` VARCHAR(450) NULL,
  `active` TINYINT(1) NOT NULL DEFAULT 1,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `updated_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `audit_logs` (
  `id` BIGINT NOT NULL AUTO_INCREMENT,
  `actor_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `action` VARCHAR(320) NOT NULL,
  `entity_type` VARCHAR(320) NOT NULL,
  `entity_id` VARCHAR(320) NULL,
  `details` JSON NOT NULL DEFAULT (JSON_OBJECT()),
  `request_id` VARCHAR(320) NULL,
  `source_ip` VARCHAR(45) NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cab_groups` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `name` VARCHAR(320) NOT NULL,
  `quorum` INT NOT NULL,
  `active` TINYINT(1) NOT NULL DEFAULT 1,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  UNIQUE (`name`),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `cab_members` (
  `group_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  PRIMARY KEY (`group_id`, `user_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `categories` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `parent_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `name` VARCHAR(320) NOT NULL,
  `description` LONGTEXT NULL,
  `active` TINYINT(1) NOT NULL DEFAULT 1,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `updated_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `change_approvals` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `change_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `approver_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `department_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `sequence` INT NOT NULL DEFAULT 1,
  `required` TINYINT(1) NOT NULL DEFAULT 1,
  `status` VARCHAR(320) NOT NULL DEFAULT 'PENDING',
  `decision_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `decision_note` LONGTEXT NULL,
  `decided_at` DATETIME(6) NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `revision` INT NOT NULL DEFAULT 1,
  `cab_group_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `change_attachments` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `change_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `uploaded_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `file_name` VARCHAR(320) NOT NULL,
  `content_type` VARCHAR(320) NOT NULL,
  `size_bytes` BIGINT NOT NULL,
  `sha256` VARCHAR(320) NOT NULL,
  `content` LONGBLOB NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `change_blackouts` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `name` VARCHAR(320) NOT NULL,
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `site_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `starts_at` DATETIME(6) NOT NULL,
  `ends_at` DATETIME(6) NOT NULL,
  `active` TINYINT(1) NOT NULL DEFAULT 1,
  `created_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `change_communications` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `change_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `recipient_emails` JSON NOT NULL,
  `subject` VARCHAR(320) NOT NULL,
  `body` LONGTEXT NOT NULL,
  `include_client` TINYINT(1) NOT NULL DEFAULT 0,
  `status` VARCHAR(320) NOT NULL DEFAULT 'DRAFT',
  `attempts` INT NOT NULL DEFAULT 0,
  `last_error_code` LONGTEXT NULL,
  `sent_at` DATETIME(6) NULL,
  `created_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `change_governance_policy` (
  `id` SMALLINT NOT NULL,
  `high_risk_cab` TINYINT(1) NOT NULL DEFAULT 1,
  `minimum_approvals` INT NOT NULL DEFAULT 1,
  `review_required` TINYINT(1) NOT NULL DEFAULT 1,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `change_history` (
  `id` BIGINT NOT NULL AUTO_INCREMENT,
  `change_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `actor_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `event` VARCHAR(320) NOT NULL,
  `details` JSON NOT NULL DEFAULT (JSON_OBJECT()),
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `change_reviews` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `change_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `revision` INT NOT NULL,
  `outcome` VARCHAR(320) NOT NULL,
  `summary` LONGTEXT NOT NULL,
  `follow_up` LONGTEXT NOT NULL,
  `actor_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `change_stakeholders` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `change_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `external_name` VARCHAR(320) NULL,
  `external_email` VARCHAR(320) NULL,
  `stakeholder_role` VARCHAR(320) NOT NULL DEFAULT 'STAKEHOLDER',
  `added_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `contact_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `change_tasks` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `change_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `department_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `assigned_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `title` VARCHAR(320) NOT NULL,
  `instructions` LONGTEXT NOT NULL DEFAULT (''),
  `status` VARCHAR(320) NOT NULL DEFAULT 'TODO',
  `due_at` DATETIME(6) NULL,
  `completed_at` DATETIME(6) NULL,
  `update_note` LONGTEXT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `change_teams` (
  `change_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `department_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `role` VARCHAR(320) NOT NULL,
  `status` VARCHAR(320) NOT NULL DEFAULT 'PENDING',
  PRIMARY KEY (`change_id`, `department_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `change_templates` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `name` VARCHAR(320) NOT NULL,
  `category` VARCHAR(320) NOT NULL,
  `description` LONGTEXT NOT NULL,
  `implementation_plan` LONGTEXT NOT NULL,
  `test_plan` LONGTEXT NOT NULL,
  `rollback_plan` LONGTEXT NOT NULL,
  `active` TINYINT(1) NOT NULL DEFAULT 1,
  `created_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `updated_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  UNIQUE (`name`),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `change_ticket_links` (
  `change_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `linked_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  PRIMARY KEY (`change_id`, `ticket_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `change_updates` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `change_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `author_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `update_type` VARCHAR(320) NOT NULL,
  `body` LONGTEXT NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `changes` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `change_number` VARCHAR(320) NOT NULL,
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `site_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `department_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `title` VARCHAR(320) NOT NULL,
  `description` LONGTEXT NOT NULL,
  `category` VARCHAR(320) NOT NULL,
  `change_type` VARCHAR(320) NOT NULL,
  `risk` VARCHAR(320) NOT NULL,
  `status` VARCHAR(320) NOT NULL DEFAULT 'DRAFT',
  `impact` VARCHAR(320) NOT NULL,
  `outage_expected` TINYINT(1) NOT NULL DEFAULT 0,
  `downtime_minutes` INT NOT NULL DEFAULT 0,
  `reason` LONGTEXT NOT NULL,
  `business_justification` VARCHAR(320) NOT NULL,
  `implementation_plan` LONGTEXT NOT NULL,
  `test_plan` LONGTEXT NOT NULL,
  `rollback_plan` LONGTEXT NOT NULL,
  `communication_plan` LONGTEXT NOT NULL DEFAULT (''),
  `client_notification_enabled` TINYINT(1) NOT NULL DEFAULT 0,
  `owner_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `requested_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `planned_start` DATETIME(6) NULL,
  `planned_end` DATETIME(6) NULL,
  `actual_start` DATETIME(6) NULL,
  `actual_end` DATETIME(6) NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `updated_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `revision` INT NOT NULL DEFAULT 1,
  `cab_group_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `cab_quorum` INT NOT NULL DEFAULT 0,
  `affected_resources` JSON NOT NULL DEFAULT (JSON_ARRAY()),
  UNIQUE (`change_number`),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `client_email_domains` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `domain` VARCHAR(320) NOT NULL,
  `site_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `active` TINYINT(1) NOT NULL DEFAULT 1,
  `revision` INT NOT NULL DEFAULT 1,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `updated_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  UNIQUE (`domain`),
  UNIQUE (`id`, `client_id`),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `client_mail_routes` (
  `address` VARCHAR(320) NOT NULL,
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `active` TINYINT(1) NOT NULL DEFAULT 0,
  `outbound_authorized` TINYINT(1) NOT NULL DEFAULT 0,
  PRIMARY KEY (`address`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `client_ticket_sequences` (
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `next_number` BIGINT NOT NULL DEFAULT '1',
  PRIMARY KEY (`client_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `client_users` (
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`client_id`, `user_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `clients` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `name` VARCHAR(320) NOT NULL,
  `status` VARCHAR(320) NOT NULL DEFAULT 'ACTIVE',
  `primary_email` VARCHAR(320) NULL,
  `primary_phone` VARCHAR(320) NULL,
  `notes` LONGTEXT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `updated_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `organization_type` VARCHAR(320) NOT NULL DEFAULT 'CLIENT',
  `ticket_prefix` VARCHAR(320) NOT NULL,
  PRIMARY KEY (`id`),
  UNIQUE (`ticket_prefix`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `contacts` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `name` VARCHAR(320) NOT NULL,
  `email` VARCHAR(320) NULL,
  `phone` VARCHAR(320) NULL,
  `active` TINYINT(1) NOT NULL DEFAULT 1,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `updated_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `site_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  UNIQUE (`id`, `client_id`),
  PRIMARY KEY (`id`),
  UNIQUE (`user_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `customer_group_members` (
  `group_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `contact_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  PRIMARY KEY (`group_id`, `contact_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `department_users` (
  `department_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `expertise` LONGTEXT NULL,
  `is_lead` TINYINT(1) NOT NULL DEFAULT 0,
  PRIMARY KEY (`department_id`, `user_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `departments` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `name` VARCHAR(320) NOT NULL,
  `description` LONGTEXT NULL,
  `active` TINYINT(1) NOT NULL DEFAULT 1,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `updated_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  UNIQUE (`name`),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `help_requests` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `requested_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `department_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `requested_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `expertise_needed` LONGTEXT NOT NULL,
  `status` VARCHAR(320) NOT NULL DEFAULT 'PENDING',
  `responded_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `response_note` LONGTEXT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `responded_at` DATETIME(6) NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `knowledge_articles` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `title` VARCHAR(320) NOT NULL,
  `body` LONGTEXT NOT NULL,
  `category_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `visibility` VARCHAR(320) NOT NULL,
  `status` VARCHAR(320) NOT NULL,
  `author_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `revision` INT NOT NULL DEFAULT 1,
  `updated_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `mail_attachments` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `message_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `file_name` VARCHAR(320) NOT NULL,
  `content_type` VARCHAR(320) NOT NULL,
  `content` LONGBLOB NOT NULL,
  `size_bytes` INT NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `mail_messages` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `direction` VARCHAR(320) NOT NULL,
  `channel` VARCHAR(320) NOT NULL,
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `change_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `comment_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `communication_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `mailbox` VARCHAR(320) NOT NULL,
  `message_id` VARCHAR(320) NOT NULL,
  `in_reply_to` VARCHAR(320) NULL,
  `reference_ids` JSON NOT NULL DEFAULT (JSON_ARRAY()),
  `sender_address` VARCHAR(320) NOT NULL,
  `recipient_addresses` JSON NOT NULL,
  `subject` VARCHAR(320) NOT NULL,
  `body` LONGTEXT NOT NULL,
  `state` VARCHAR(320) NOT NULL,
  `attempts` INT NOT NULL DEFAULT 0,
  `next_attempt_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `lease_until` DATETIME(6) NULL,
  `last_error_code` LONGTEXT NULL,
  `processed_at` DATETIME(6) NULL,
  `created_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `contacted_address` VARCHAR(320) NULL,
  UNIQUE (`mailbox`, `message_id`),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `mail_settings` (
  `id` SMALLINT NOT NULL,
  `enabled` TINYINT(1) NOT NULL DEFAULT 0,
  `smtp_host` VARCHAR(320) NOT NULL DEFAULT 'mail.smtp2go.com',
  `smtp_port` INT NOT NULL DEFAULT 587,
  `tls_mode` VARCHAR(320) NOT NULL DEFAULT 'STARTTLS',
  `smtp_username` VARCHAR(320) NOT NULL DEFAULT '',
  `password_ciphertext` LONGTEXT NULL,
  `ticket_address` VARCHAR(320) NOT NULL DEFAULT 'it@amnikontechnologies.com',
  `change_address` VARCHAR(320) NOT NULL DEFAULT 'change@amnikontechnologies.com',
  `inbound_provider` VARCHAR(320) NOT NULL DEFAULT 'DISABLED',
  `inbound_since` DATETIME(6) NULL,
  `updated_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `mail_sync_state` (
  `mailbox` VARCHAR(320) NOT NULL,
  `cursor` LONGTEXT NULL,
  `last_sync_at` DATETIME(6) NULL,
  `last_error_code` LONGTEXT NULL,
  PRIMARY KEY (`mailbox`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `notification_preferences` (
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `ticket_assigned` TINYINT(1) NOT NULL DEFAULT 1,
  `ticket_transferred` TINYINT(1) NOT NULL DEFAULT 1,
  `public_reply` TINYINT(1) NOT NULL DEFAULT 1,
  `sla_at_risk` TINYINT(1) NOT NULL DEFAULT 1,
  `sla_breached` TINYINT(1) NOT NULL DEFAULT 1,
  `ticket_unattended` TINYINT(1) NOT NULL DEFAULT 1,
  `updated_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`user_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `notifications` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `type` VARCHAR(320) NOT NULL,
  `title` VARCHAR(320) NOT NULL,
  `body` LONGTEXT NOT NULL,
  `event_key` VARCHAR(450) NOT NULL,
  `read_at` DATETIME(6) NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `change_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  UNIQUE (`event_key`),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `permissions` (
  `code` VARCHAR(320) NOT NULL,
  `name` VARCHAR(320) NOT NULL,
  `description` LONGTEXT NOT NULL,
  PRIMARY KEY (`code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `portal_ticket_submissions` (
  `request_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `domain_rule_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `requester_email` VARCHAR(320) NOT NULL,
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`request_id`),
  UNIQUE (`ticket_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `problem_history` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `problem_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `actor_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `action` VARCHAR(320) NOT NULL,
  `details` JSON NOT NULL DEFAULT (JSON_OBJECT()),
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `problem_tickets` (
  `problem_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `linked_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`problem_id`, `ticket_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `problems` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `number` BIGINT NOT NULL,
  `problem_number` VARCHAR(320) NULL,
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `site_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `title` VARCHAR(320) NOT NULL,
  `description` LONGTEXT NOT NULL,
  `priority` VARCHAR(320) NOT NULL DEFAULT 'P3_MEDIUM',
  `status` VARCHAR(320) NOT NULL DEFAULT 'OPEN',
  `root_cause` LONGTEXT NOT NULL DEFAULT (''),
  `workaround` LONGTEXT NOT NULL DEFAULT (''),
  `resolution` LONGTEXT NOT NULL DEFAULT (''),
  `owner_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `created_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `revision` INT NOT NULL DEFAULT 1,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `updated_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  UNIQUE (`id`, `client_id`),
  UNIQUE (`number`),
  PRIMARY KEY (`id`),
  UNIQUE (`problem_number`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `roles` (
  `code` VARCHAR(320) NOT NULL,
  `name` VARCHAR(320) NOT NULL,
  `description` LONGTEXT NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `saved_ticket_views` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `name` VARCHAR(320) NOT NULL,
  `query` JSON NOT NULL DEFAULT (JSON_OBJECT()),
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `updated_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `service_templates` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `name` VARCHAR(320) NOT NULL,
  `description` LONGTEXT NOT NULL,
  `category_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `active` TINYINT(1) NOT NULL DEFAULT 1,
  `instructions` LONGTEXT NOT NULL,
  `created_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `updated_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `site_ticket_sequences` (
  `site_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `next_number` BIGINT NOT NULL DEFAULT 1,
  PRIMARY KEY (`site_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `sites` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `name` VARCHAR(320) NOT NULL,
  `address` LONGTEXT NULL,
  `active` TINYINT(1) NOT NULL DEFAULT 1,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `updated_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `ticket_prefix` VARCHAR(320) NOT NULL,
  UNIQUE (`client_id`, `name`),
  UNIQUE (`id`, `client_id`),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `sla_calendars` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `name` VARCHAR(320) NOT NULL,
  `timezone` VARCHAR(320) NOT NULL,
  `working_days` JSON NOT NULL,
  `work_start` TIME NOT NULL,
  `work_end` TIME NOT NULL,
  `active` TINYINT(1) NOT NULL DEFAULT 1,
  `updated_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `updated_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `sql_timezone` VARCHAR(80) NULL,
  UNIQUE (`name`),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `sla_holidays` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `calendar_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `holiday_date` DATE NOT NULL,
  `name` VARCHAR(320) NOT NULL,
  UNIQUE (`calendar_id`, `holiday_date`),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `sla_policies` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `priority` VARCHAR(320) NOT NULL,
  `response_minutes` INT NOT NULL,
  `resolution_minutes` INT NOT NULL,
  `warning_percent` INT NOT NULL DEFAULT 75,
  `active` TINYINT(1) NOT NULL DEFAULT 1,
  `updated_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `updated_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `calendar_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  PRIMARY KEY (`id`),
  UNIQUE (`priority`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `staff_group_members` (
  `group_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  PRIMARY KEY (`group_id`, `user_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `tags` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `name` VARCHAR(320) NOT NULL,
  `description` LONGTEXT NULL,
  `active` TINYINT(1) NOT NULL DEFAULT 1,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `updated_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `ticket_approvals` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `requested_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `decided_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `status` VARCHAR(320) NOT NULL DEFAULT 'PENDING',
  `reason` LONGTEXT NOT NULL,
  `decision_note` LONGTEXT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `decided_at` DATETIME(6) NULL,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `ticket_assignments` (
  `id` BIGINT NOT NULL AUTO_INCREMENT,
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `previous_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `assigned_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `actor_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `reason` LONGTEXT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `ticket_attachments` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `uploaded_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `file_name` VARCHAR(320) NOT NULL,
  `content_type` VARCHAR(320) NOT NULL,
  `size_bytes` BIGINT NOT NULL,
  `storage_key` VARCHAR(450) NOT NULL,
  `sha256` VARCHAR(320) NOT NULL,
  `content` LONGBLOB NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `visibility` VARCHAR(320) NOT NULL DEFAULT 'INTERNAL',
  PRIMARY KEY (`id`),
  UNIQUE (`storage_key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `ticket_collaborators` (
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `added_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `reason` LONGTEXT NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`ticket_id`, `user_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `ticket_comments` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `author_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `comment_type` VARCHAR(320) NOT NULL,
  `body` LONGTEXT NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `ticket_history` (
  `id` BIGINT NOT NULL AUTO_INCREMENT,
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `actor_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `action` VARCHAR(320) NOT NULL,
  `previous_value` JSON NULL,
  `new_value` JSON NULL,
  `reason` LONGTEXT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `ticket_knowledge_links` (
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `article_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `linked_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`ticket_id`, `article_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `ticket_prefix_registry` (
  `prefix` VARCHAR(320) NOT NULL,
  `owner_kind` VARCHAR(320) NOT NULL,
  `owner_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  PRIMARY KEY (`prefix`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `ticket_sla` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `policy_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `target_priority` VARCHAR(320) NOT NULL,
  `response_warning_at` DATETIME(6) NOT NULL,
  `response_due_at` DATETIME(6) NOT NULL,
  `resolution_warning_at` DATETIME(6) NOT NULL,
  `resolution_due_at` DATETIME(6) NOT NULL,
  `response_completed_at` DATETIME(6) NULL,
  `resolution_completed_at` DATETIME(6) NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `updated_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `calendar_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `paused_at` DATETIME(6) NULL,
  `response_warning_remaining_minutes` INT NULL,
  `response_remaining_minutes` INT NULL,
  `resolution_warning_remaining_minutes` INT NULL,
  `resolution_remaining_minutes` INT NULL,
  PRIMARY KEY (`id`),
  UNIQUE (`ticket_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `ticket_sources` (
  `code` VARCHAR(320) NOT NULL,
  `name` VARCHAR(320) NOT NULL,
  `active` TINYINT(1) NOT NULL DEFAULT 1,
  `sort_order` INT NOT NULL DEFAULT 100,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `updated_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `ticket_tags` (
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `tag_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `added_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`ticket_id`, `tag_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `tickets` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `ticket_number` VARCHAR(320) NOT NULL,
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `requester_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `site_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `category_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `assigned_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `created_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `subject` VARCHAR(320) NOT NULL,
  `description` LONGTEXT NOT NULL,
  `source` VARCHAR(320) NOT NULL,
  `source_reference_id` VARCHAR(450) NULL,
  `source_conversation_id` VARCHAR(450) NULL,
  `source_message_id` VARCHAR(450) NULL,
  `source_url` LONGTEXT NULL,
  `external_thread_id` VARCHAR(450) NULL,
  `status` VARCHAR(320) NOT NULL DEFAULT 'NEW',
  `priority` VARCHAR(320) NOT NULL DEFAULT 'P3_MEDIUM',
  `approval_status` VARCHAR(320) NOT NULL DEFAULT 'NOT_REQUIRED',
  `first_response_at` DATETIME(6) NULL,
  `resolved_at` DATETIME(6) NULL,
  `closed_at` DATETIME(6) NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `updated_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `department_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `location_text` VARCHAR(320) NULL,
  `email_reply_address` VARCHAR(320) NULL,
  UNIQUE (`id`, `client_id`),
  PRIMARY KEY (`id`),
  UNIQUE (`ticket_number`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `user_permissions` (
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `permission_code` VARCHAR(320) NOT NULL,
  `granted_by` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`user_id`, `permission_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `user_roles` (
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `role_code` VARCHAR(320) NOT NULL,
  `granted_by` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`user_id`, `role_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `user_sites` (
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `site_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  PRIMARY KEY (`user_id`, `site_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `users` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `entra_object_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `entra_tenant_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `email` VARCHAR(320) NOT NULL,
  `display_name` VARCHAR(320) NOT NULL,
  `active` TINYINT(1) NOT NULL DEFAULT 1,
  `last_login_at` DATETIME(6) NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `updated_at` DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
  `session_version` INT NOT NULL DEFAULT 0,
  `work_scope` VARCHAR(320) NOT NULL DEFAULT 'CLIENT',
  `change_access` VARCHAR(320) NOT NULL DEFAULT 'NONE',
  `supervisor_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NULL,
  `access_expires_at` DATETIME(6) NULL,
  UNIQUE (`entra_tenant_id`, `entra_object_id`),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE app_locks(name VARCHAR(64) PRIMARY KEY) ENGINE=InnoDB;

INSERT INTO app_locks VALUES ('global');

CREATE TABLE app_sequences(name VARCHAR(64) PRIMARY KEY,next_number BIGINT NOT NULL) ENGINE=InnoDB;

INSERT INTO app_sequences VALUES ('problem',1),('change',1);

CREATE UNIQUE INDEX `clients_name_ci_uq` ON `clients` (`name`);

CREATE UNIQUE INDEX `users_email_ci_uq` ON `users` (`email`);

CREATE INDEX `contacts_client_idx` ON `contacts` (`client_id`);

CREATE INDEX `contacts_email_ci_idx` ON `contacts` (`email`);

CREATE INDEX `assets_client_idx` ON `assets` (`client_id`);

CREATE UNIQUE INDEX `assets_serial_ci_uq` ON `assets` (`serial_number`);

CREATE UNIQUE INDEX `categories_root_name_ci_uq` ON `categories` (((CASE WHEN `parent_id` IS NULL THEN `name` ELSE NULL END)));

CREATE UNIQUE INDEX `categories_child_name_ci_uq` ON `categories` (`parent_id`, `name`);

CREATE UNIQUE INDEX `tags_name_ci_uq` ON `tags` (`name`);

CREATE INDEX `audit_entity_idx` ON `audit_logs` (`entity_type`, `entity_id`, `created_at` DESC);

CREATE INDEX `audit_actor_idx` ON `audit_logs` (`actor_user_id`, `created_at` DESC);

CREATE INDEX `tickets_client_created_idx` ON `tickets` (`client_id`, `created_at` DESC);

CREATE INDEX `tickets_assignee_status_idx` ON `tickets` (`assigned_user_id`, `status`, `updated_at` DESC);

CREATE INDEX `tickets_status_priority_idx` ON `tickets` (`status`, `priority`, `updated_at` DESC);

CREATE INDEX `ticket_comments_ticket_idx` ON `ticket_comments` (`ticket_id`, `created_at`);

CREATE INDEX `ticket_assignments_ticket_idx` ON `ticket_assignments` (`ticket_id`, `created_at`);

CREATE UNIQUE INDEX `ticket_approvals_pending_uq` ON `ticket_approvals` (`ticket_id`);

CREATE INDEX `ticket_history_ticket_idx` ON `ticket_history` (`ticket_id`, `created_at`, `id`);

CREATE INDEX `ticket_attachments_ticket_idx` ON `ticket_attachments` (`ticket_id`, `created_at`);

CREATE INDEX `ticket_sla_due_idx` ON `ticket_sla` (`response_due_at`, `resolution_due_at`);

CREATE UNIQUE INDEX `saved_ticket_views_user_name_uq` ON `saved_ticket_views` (`user_id`, `name`);

CREATE INDEX `notifications_user_unread_idx` ON `notifications` (`user_id`, `read_at`, `created_at` DESC);

CREATE INDEX `changes_scope_idx` ON `changes` (`client_id`, `status`, `planned_start`);

CREATE UNIQUE INDEX `change_stakeholders_user_unique` ON `change_stakeholders` (`change_id`, `user_id`);

CREATE INDEX `mail_change_idx` ON `mail_messages` (`change_id`, `created_at`);

CREATE UNIQUE INDEX `change_stakeholders_email_unique` ON `change_stakeholders` (`change_id`, `external_email`);

CREATE UNIQUE INDEX `change_stakeholders_contact_unique` ON `change_stakeholders` (`change_id`, `contact_id`);

CREATE INDEX `contacts_site_idx` ON `contacts` (`site_id`);

CREATE UNIQUE INDEX `change_cab_vote_unique` ON `change_approvals` (((CASE WHEN `cab_group_id` IS NOT NULL THEN `change_id` ELSE NULL END)),`revision`,`approver_user_id`);

CREATE INDEX `change_window_idx` ON `changes` (`client_id`, `planned_start`, `planned_end`);

CREATE UNIQUE INDEX `mail_comment_once` ON `mail_messages` (((CASE WHEN `direction`='OUTBOUND' THEN `comment_id` ELSE NULL END)));

CREATE UNIQUE INDEX `mail_communication_once` ON `mail_messages` (((CASE WHEN `direction`='OUTBOUND' THEN `communication_id` ELSE NULL END)));

CREATE INDEX `mail_outbox_idx` ON `mail_messages` (`state`, `next_attempt_at`);

CREATE INDEX `mail_ticket_idx` ON `mail_messages` (`ticket_id`, `created_at`);

CREATE INDEX `problems_client_status_updated` ON `problems` (`client_id`, `status`, `updated_at` DESC, `id`);

CREATE INDEX `problems_owner_updated` ON `problems` (`owner_user_id`, `updated_at` DESC, `id`);

CREATE INDEX `problem_tickets_ticket` ON `problem_tickets` (`ticket_id`);

CREATE INDEX `problem_history_timeline` ON `problem_history` (`problem_id`, `created_at`, `id`);

CREATE UNIQUE INDEX `sites_id_client_portal_uq` ON `sites` (`id`, `client_id`);

CREATE INDEX `client_email_domains_client_idx` ON `client_email_domains` (`client_id`);

CREATE INDEX `contacts_portal_email_idx` ON `contacts` (`client_id`, `email`);

CREATE UNIQUE INDEX `contacts_group_client_uq` ON `contacts` (`id`, `client_id`);

CREATE UNIQUE INDEX `access_groups_name_uq` ON `access_groups` (`kind`,((COALESCE(`client_id`,'00000000-0000-0000-0000-000000000000'))),((TRIM(`name`))));

CREATE INDEX `staff_group_members_user_idx` ON `staff_group_members` (`user_id`);

CREATE INDEX `customer_group_members_contact_idx` ON `customer_group_members` (`contact_id`);

CREATE UNIQUE INDEX `sites_ticket_prefix_uq` ON `sites` (`ticket_prefix`);

ALTER TABLE `access_groups` ADD CONSTRAINT `access_groups_client_id_fkey` FOREIGN KEY (client_id) REFERENCES clients(id) ON DELETE RESTRICT;

ALTER TABLE `access_groups` ADD CONSTRAINT `access_groups_check_4` CHECK ((CHAR_LENGTH(description) <= 500));

ALTER TABLE `access_groups` ADD CONSTRAINT `access_groups_check_8` CHECK ((kind IN ('CUSTOMER', 'STAFF')));

ALTER TABLE `access_groups` ADD CONSTRAINT `access_groups_check_10` CHECK (((CHAR_LENGTH(TRIM(name)) >= 2) AND (CHAR_LENGTH(TRIM(name)) <= 120)));

ALTER TABLE `app_schema_state` ADD CONSTRAINT `app_schema_state_check_1` CHECK ((id = 1));

ALTER TABLE `app_schema_state` ADD CONSTRAINT `app_schema_state_check_4` CHECK ((version > 0));

ALTER TABLE `assets` ADD CONSTRAINT `assets_client_id_foreign` FOREIGN KEY (client_id) REFERENCES clients(id) ON DELETE RESTRICT;

ALTER TABLE `assets` ADD CONSTRAINT `assets_site_client_fk` FOREIGN KEY (site_id, client_id) REFERENCES sites(id, client_id) ON DELETE RESTRICT;

ALTER TABLE `assets` ADD CONSTRAINT `assets_site_id_foreign` FOREIGN KEY (site_id) REFERENCES sites(id) ON DELETE SET NULL;

ALTER TABLE `audit_logs` ADD CONSTRAINT `audit_logs_actor_user_id_foreign` FOREIGN KEY (actor_user_id) REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE `cab_groups` ADD CONSTRAINT `cab_groups_check_6` CHECK (((quorum > 0) AND (quorum <= 50)));

ALTER TABLE `cab_members` ADD CONSTRAINT `cab_members_group_id_fkey` FOREIGN KEY (group_id) REFERENCES cab_groups(id) ON DELETE CASCADE;

ALTER TABLE `cab_members` ADD CONSTRAINT `cab_members_user_id_fkey` FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE RESTRICT;

ALTER TABLE `categories` ADD CONSTRAINT `categories_parent_id_foreign` FOREIGN KEY (parent_id) REFERENCES categories(id) ON DELETE RESTRICT;

ALTER TABLE `change_approvals` ADD CONSTRAINT `change_approvals_approver_user_id_foreign` FOREIGN KEY (approver_user_id) REFERENCES users(id) ON DELETE RESTRICT;

ALTER TABLE `change_approvals` ADD CONSTRAINT `change_approvals_cab_group_id_fkey` FOREIGN KEY (cab_group_id) REFERENCES cab_groups(id) ON DELETE RESTRICT;

ALTER TABLE `change_approvals` ADD CONSTRAINT `change_approvals_change_id_foreign` FOREIGN KEY (change_id) REFERENCES changes(id) ON DELETE CASCADE;

ALTER TABLE `change_approvals` ADD CONSTRAINT `change_approvals_check_4` CHECK (((approver_user_id IS NOT NULL) OR (department_id IS NOT NULL)));

ALTER TABLE `change_approvals` ADD CONSTRAINT `change_approvals_decision_by_user_id_foreign` FOREIGN KEY (decision_by_user_id) REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE `change_approvals` ADD CONSTRAINT `change_approvals_department_id_foreign` FOREIGN KEY (department_id) REFERENCES departments(id) ON DELETE RESTRICT;

ALTER TABLE `change_approvals` ADD CONSTRAINT `change_approvals_check_13` CHECK ((status IN ('PENDING', 'APPROVED', 'REJECTED', 'CHANGES_REQUESTED')));

ALTER TABLE `change_attachments` ADD CONSTRAINT `change_attachments_change_id_foreign` FOREIGN KEY (change_id) REFERENCES changes(id) ON DELETE CASCADE;

ALTER TABLE `change_attachments` ADD CONSTRAINT `change_attachments_check_9` CHECK (((size_bytes > 0) AND (size_bytes <= 5242880)));

ALTER TABLE `change_attachments` ADD CONSTRAINT `change_attachments_uploaded_by_user_id_foreign` FOREIGN KEY (uploaded_by_user_id) REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE `change_blackouts` ADD CONSTRAINT `change_blackouts_check_1` CHECK ((ends_at > starts_at));

ALTER TABLE `change_blackouts` ADD CONSTRAINT `change_blackouts_check_2` CHECK (((site_id IS NULL) OR (client_id IS NOT NULL)));

ALTER TABLE `change_blackouts` ADD CONSTRAINT `change_blackouts_client_id_fkey` FOREIGN KEY (client_id) REFERENCES clients(id) ON DELETE RESTRICT;

ALTER TABLE `change_blackouts` ADD CONSTRAINT `change_blackouts_created_by_user_id_fkey` FOREIGN KEY (created_by_user_id) REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE `change_blackouts` ADD CONSTRAINT `change_blackouts_site_id_client_id_fkey` FOREIGN KEY (site_id, client_id) REFERENCES sites(id, client_id);

ALTER TABLE `change_blackouts` ADD CONSTRAINT `change_blackouts_site_id_fkey` FOREIGN KEY (site_id) REFERENCES sites(id) ON DELETE RESTRICT;

ALTER TABLE `change_communications` ADD CONSTRAINT `change_communications_change_id_foreign` FOREIGN KEY (change_id) REFERENCES changes(id) ON DELETE CASCADE;

ALTER TABLE `change_communications` ADD CONSTRAINT `change_communications_created_by_user_id_foreign` FOREIGN KEY (created_by_user_id) REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE `change_communications` ADD CONSTRAINT `change_communications_check_10` CHECK ((status IN ('DRAFT', 'QUEUED', 'SENT', 'FAILED')));

ALTER TABLE `change_governance_policy` ADD CONSTRAINT `change_governance_policy_check_1` CHECK ((id = 1));

ALTER TABLE `change_governance_policy` ADD CONSTRAINT `change_governance_policy_check_3` CHECK (((minimum_approvals >= 1) AND (minimum_approvals <= 10)));

ALTER TABLE `change_history` ADD CONSTRAINT `change_history_actor_user_id_foreign` FOREIGN KEY (actor_user_id) REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE `change_history` ADD CONSTRAINT `change_history_change_id_foreign` FOREIGN KEY (change_id) REFERENCES changes(id) ON DELETE CASCADE;

ALTER TABLE `change_reviews` ADD CONSTRAINT `change_reviews_actor_user_id_fkey` FOREIGN KEY (actor_user_id) REFERENCES users(id) ON DELETE RESTRICT;

ALTER TABLE `change_reviews` ADD CONSTRAINT `change_reviews_change_id_fkey` FOREIGN KEY (change_id) REFERENCES changes(id) ON DELETE RESTRICT;

ALTER TABLE `change_reviews` ADD CONSTRAINT `change_reviews_check_7` CHECK ((outcome IN ('SUCCESS', 'PARTIAL', 'FAILED', 'ROLLED_BACK')));

ALTER TABLE `change_stakeholders` ADD CONSTRAINT `change_stakeholders_added_by_user_id_foreign` FOREIGN KEY (added_by_user_id) REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE `change_stakeholders` ADD CONSTRAINT `change_stakeholders_change_id_foreign` FOREIGN KEY (change_id) REFERENCES changes(id) ON DELETE CASCADE;

ALTER TABLE `change_stakeholders` ADD CONSTRAINT `change_stakeholders_contact_id_foreign` FOREIGN KEY (contact_id) REFERENCES contacts(id) ON DELETE RESTRICT;

ALTER TABLE `change_stakeholders` ADD CONSTRAINT `change_stakeholders_check_6` CHECK ((((contact_id IS NOT NULL) AND (user_id IS NULL) AND (external_email IS NULL)) OR ((contact_id IS NULL) AND (user_id IS NOT NULL) AND (external_email IS NULL)) OR ((contact_id IS NULL) AND (user_id IS NULL) AND (external_email IS NOT NULL))));

ALTER TABLE `change_stakeholders` ADD CONSTRAINT `change_stakeholders_check_8` CHECK ((stakeholder_role IN ('STAKEHOLDER', 'BUSINESS_CONTACT', 'TECHNICAL_CONTACT', 'OBSERVER')));

ALTER TABLE `change_stakeholders` ADD CONSTRAINT `change_stakeholders_user_id_foreign` FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE RESTRICT;

ALTER TABLE `change_tasks` ADD CONSTRAINT `change_tasks_assigned_user_id_foreign` FOREIGN KEY (assigned_user_id) REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE `change_tasks` ADD CONSTRAINT `change_tasks_change_id_foreign` FOREIGN KEY (change_id) REFERENCES changes(id) ON DELETE CASCADE;

ALTER TABLE `change_tasks` ADD CONSTRAINT `change_tasks_department_id_foreign` FOREIGN KEY (department_id) REFERENCES departments(id) ON DELETE SET NULL;

ALTER TABLE `change_tasks` ADD CONSTRAINT `change_tasks_check_8` CHECK ((status IN ('TODO', 'IN_PROGRESS', 'BLOCKED', 'COMPLETED', 'WAIVED')));

ALTER TABLE `change_teams` ADD CONSTRAINT `change_teams_change_id_foreign` FOREIGN KEY (change_id) REFERENCES changes(id) ON DELETE CASCADE;

ALTER TABLE `change_teams` ADD CONSTRAINT `change_teams_department_id_foreign` FOREIGN KEY (department_id) REFERENCES departments(id) ON DELETE RESTRICT;

ALTER TABLE `change_teams` ADD CONSTRAINT `change_teams_check_5` CHECK ((role IN ('LEAD', 'IMPLEMENTER', 'REVIEWER')));

ALTER TABLE `change_teams` ADD CONSTRAINT `change_teams_check_7` CHECK ((status IN ('PENDING', 'ACTIVE', 'COMPLETED')));

ALTER TABLE `change_templates` ADD CONSTRAINT `change_templates_check_1` CHECK ((category IN ('SERVER', 'CLOUD', 'MICROSOFT_365', 'NETWORK', 'SECURITY', 'APPLICATION', 'OTHER')));

ALTER TABLE `change_templates` ADD CONSTRAINT `change_templates_created_by_user_id_fkey` FOREIGN KEY (created_by_user_id) REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE `change_ticket_links` ADD CONSTRAINT `change_ticket_links_change_id_foreign` FOREIGN KEY (change_id) REFERENCES changes(id) ON DELETE CASCADE;

ALTER TABLE `change_ticket_links` ADD CONSTRAINT `change_ticket_links_linked_by_user_id_foreign` FOREIGN KEY (linked_by_user_id) REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE `change_ticket_links` ADD CONSTRAINT `change_ticket_links_ticket_id_foreign` FOREIGN KEY (ticket_id) REFERENCES tickets(id) ON DELETE CASCADE;

ALTER TABLE `change_updates` ADD CONSTRAINT `change_updates_author_user_id_foreign` FOREIGN KEY (author_user_id) REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE `change_updates` ADD CONSTRAINT `change_updates_change_id_foreign` FOREIGN KEY (change_id) REFERENCES changes(id) ON DELETE CASCADE;

ALTER TABLE `change_updates` ADD CONSTRAINT `change_updates_check_7` CHECK ((update_type IN ('INTERNAL', 'IMPLEMENTATION', 'VALIDATION', 'CLIENT_COMMUNICATION')));

ALTER TABLE `changes` ADD CONSTRAINT `changes_cab_group_id_fkey` FOREIGN KEY (cab_group_id) REFERENCES cab_groups(id) ON DELETE RESTRICT;

ALTER TABLE `changes` ADD CONSTRAINT `changes_check_3` CHECK ((cab_quorum >= 0));

ALTER TABLE `changes` ADD CONSTRAINT `changes_check_5` CHECK ((category IN ('SERVER', 'CLOUD', 'MICROSOFT_365', 'NETWORK', 'SECURITY', 'APPLICATION', 'OTHER')));

ALTER TABLE `changes` ADD CONSTRAINT `changes_check_9` CHECK ((change_type IN ('STANDARD', 'NORMAL', 'EMERGENCY')));

ALTER TABLE `changes` ADD CONSTRAINT `changes_check_11` CHECK (((planned_end IS NULL) OR (planned_start IS NULL) OR (planned_end > planned_start)));

ALTER TABLE `changes` ADD CONSTRAINT `changes_client_id_foreign` FOREIGN KEY (client_id) REFERENCES clients(id) ON DELETE RESTRICT;

ALTER TABLE `changes` ADD CONSTRAINT `changes_department_id_foreign` FOREIGN KEY (department_id) REFERENCES departments(id) ON DELETE SET NULL;

ALTER TABLE `changes` ADD CONSTRAINT `changes_check_19` CHECK ((downtime_minutes >= 0));

ALTER TABLE `changes` ADD CONSTRAINT `changes_owner_user_id_foreign` FOREIGN KEY (owner_user_id) REFERENCES users(id) ON DELETE RESTRICT;

ALTER TABLE `changes` ADD CONSTRAINT `changes_requested_by_user_id_foreign` FOREIGN KEY (requested_by_user_id) REFERENCES users(id) ON DELETE RESTRICT;

ALTER TABLE `changes` ADD CONSTRAINT `changes_check_31` CHECK ((revision > 0));

ALTER TABLE `changes` ADD CONSTRAINT `changes_check_33` CHECK ((risk IN ('LOW', 'MEDIUM', 'HIGH', 'CRITICAL')));

ALTER TABLE `changes` ADD CONSTRAINT `changes_site_id_foreign` FOREIGN KEY (site_id) REFERENCES sites(id) ON DELETE SET NULL;

ALTER TABLE `changes` ADD CONSTRAINT `changes_check_37` CHECK ((status IN ('DRAFT', 'SUBMITTED', 'ASSESSMENT', 'AWAITING_APPROVAL', 'APPROVED', 'SCHEDULED', 'IN_PROGRESS', 'VALIDATION', 'COMPLETED', 'CLOSED', 'REJECTED', 'CANCELLED', 'FAILED', 'ROLLED_BACK')));

ALTER TABLE `client_email_domains` ADD CONSTRAINT `client_email_domains_client_id_fkey` FOREIGN KEY (client_id) REFERENCES clients(id) ON DELETE RESTRICT;

ALTER TABLE `client_email_domains` ADD CONSTRAINT `client_email_domains_check_4` CHECK (((domain = lower(TRIM(domain))) AND (REGEXP_LIKE(domain,'^[a-z0-9][a-z0-9.-]+[.][a-z0-9-]+$','c'))));

ALTER TABLE `client_email_domains` ADD CONSTRAINT `client_email_domains_site_id_client_id_fkey` FOREIGN KEY (site_id, client_id) REFERENCES sites(id, client_id) ON DELETE RESTRICT;

ALTER TABLE `client_mail_routes` ADD CONSTRAINT `client_mail_routes_check_1` CHECK (((address = lower(TRIM(address))) AND (address LIKE '%@%')));

ALTER TABLE `client_mail_routes` ADD CONSTRAINT `client_mail_routes_client_id_fkey` FOREIGN KEY (client_id) REFERENCES clients(id) ON DELETE RESTRICT;

ALTER TABLE `client_ticket_sequences` ADD CONSTRAINT `client_ticket_sequences_client_id_foreign` FOREIGN KEY (client_id) REFERENCES clients(id) ON DELETE CASCADE;

ALTER TABLE `client_users` ADD CONSTRAINT `client_users_client_id_foreign` FOREIGN KEY (client_id) REFERENCES clients(id) ON DELETE CASCADE;

ALTER TABLE `client_users` ADD CONSTRAINT `client_users_user_id_foreign` FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE;

ALTER TABLE `clients` ADD CONSTRAINT `clients_check_3` CHECK ((organization_type IN ('CLIENT', 'INTERNAL')));

ALTER TABLE `clients` ADD CONSTRAINT `clients_check_6` CHECK ((status IN ('ACTIVE', 'INACTIVE')));

ALTER TABLE `clients` ADD CONSTRAINT `clients_check_8` CHECK ((REGEXP_LIKE(ticket_prefix,'^[A-Z][A-Z0-9]{1,7}$','c')));

ALTER TABLE `contacts` ADD CONSTRAINT `contacts_client_id_foreign` FOREIGN KEY (client_id) REFERENCES clients(id) ON DELETE RESTRICT;

ALTER TABLE `contacts` ADD CONSTRAINT `contacts_site_client_fk` FOREIGN KEY (site_id, client_id) REFERENCES sites(id, client_id) ON DELETE RESTRICT;

ALTER TABLE `contacts` ADD CONSTRAINT `contacts_site_id_foreign` FOREIGN KEY (site_id) REFERENCES sites(id) ON DELETE SET NULL;

ALTER TABLE `contacts` ADD CONSTRAINT `contacts_user_id_foreign` FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE `customer_group_members` ADD CONSTRAINT `customer_group_members_contact_id_client_id_fkey` FOREIGN KEY (contact_id, client_id) REFERENCES contacts(id, client_id) ON DELETE RESTRICT;

ALTER TABLE `customer_group_members` ADD CONSTRAINT `customer_group_members_group_id_client_id_fkey` FOREIGN KEY (group_id, client_id) REFERENCES access_groups(id, client_id) ON DELETE CASCADE;

ALTER TABLE `department_users` ADD CONSTRAINT `department_users_department_id_foreign` FOREIGN KEY (department_id) REFERENCES departments(id) ON DELETE CASCADE;

ALTER TABLE `department_users` ADD CONSTRAINT `department_users_user_id_foreign` FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE;

ALTER TABLE `help_requests` ADD CONSTRAINT `help_requests_check_0` CHECK (((department_id IS NOT NULL) OR (requested_user_id IS NOT NULL)));

ALTER TABLE `help_requests` ADD CONSTRAINT `help_requests_department_id_foreign` FOREIGN KEY (department_id) REFERENCES departments(id) ON DELETE RESTRICT;

ALTER TABLE `help_requests` ADD CONSTRAINT `help_requests_requested_by_user_id_foreign` FOREIGN KEY (requested_by_user_id) REFERENCES users(id) ON DELETE RESTRICT;

ALTER TABLE `help_requests` ADD CONSTRAINT `help_requests_requested_user_id_foreign` FOREIGN KEY (requested_user_id) REFERENCES users(id) ON DELETE RESTRICT;

ALTER TABLE `help_requests` ADD CONSTRAINT `help_requests_responded_by_user_id_foreign` FOREIGN KEY (responded_by_user_id) REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE `help_requests` ADD CONSTRAINT `help_requests_check_10` CHECK ((status IN ('PENDING', 'ACCEPTED', 'DECLINED', 'COMPLETED')));

ALTER TABLE `help_requests` ADD CONSTRAINT `help_requests_ticket_id_foreign` FOREIGN KEY (ticket_id) REFERENCES tickets(id) ON DELETE CASCADE;

ALTER TABLE `knowledge_articles` ADD CONSTRAINT `knowledge_articles_author_user_id_fkey` FOREIGN KEY (author_user_id) REFERENCES users(id) ON DELETE RESTRICT;

ALTER TABLE `knowledge_articles` ADD CONSTRAINT `knowledge_articles_category_id_fkey` FOREIGN KEY (category_id) REFERENCES categories(id) ON DELETE SET NULL;

ALTER TABLE `knowledge_articles` ADD CONSTRAINT `knowledge_articles_client_id_fkey` FOREIGN KEY (client_id) REFERENCES clients(id) ON DELETE RESTRICT;

ALTER TABLE `knowledge_articles` ADD CONSTRAINT `knowledge_articles_check_9` CHECK ((status IN ('DRAFT', 'PUBLISHED', 'ARCHIVED')));

ALTER TABLE `knowledge_articles` ADD CONSTRAINT `knowledge_articles_check_13` CHECK ((visibility IN ('STAFF', 'CLIENT')));

ALTER TABLE `mail_attachments` ADD CONSTRAINT `mail_attachments_message_id_fkey` FOREIGN KEY (message_id) REFERENCES mail_messages(id) ON DELETE RESTRICT;

ALTER TABLE `mail_attachments` ADD CONSTRAINT `mail_attachments_check_8` CHECK (((size_bytes > 0) AND (size_bytes <= 5242880)));

ALTER TABLE `mail_messages` ADD CONSTRAINT `mail_messages_change_id_fkey` FOREIGN KEY (change_id) REFERENCES changes(id) ON DELETE RESTRICT;

ALTER TABLE `mail_messages` ADD CONSTRAINT `mail_messages_check_3` CHECK ((channel IN ('TICKET', 'CHANGE', 'TEST')));

ALTER TABLE `mail_messages` ADD CONSTRAINT `mail_messages_check_5` CHECK ((NOT ((ticket_id IS NOT NULL) AND (change_id IS NOT NULL))));

ALTER TABLE `mail_messages` ADD CONSTRAINT `mail_messages_comment_id_fkey` FOREIGN KEY (comment_id) REFERENCES ticket_comments(id) ON DELETE RESTRICT;

ALTER TABLE `mail_messages` ADD CONSTRAINT `mail_messages_communication_id_fkey` FOREIGN KEY (communication_id) REFERENCES change_communications(id) ON DELETE RESTRICT;

ALTER TABLE `mail_messages` ADD CONSTRAINT `mail_messages_created_by_user_id_fkey` FOREIGN KEY (created_by_user_id) REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE `mail_messages` ADD CONSTRAINT `mail_messages_check_10` CHECK ((direction IN ('INBOUND', 'OUTBOUND')));

ALTER TABLE `mail_messages` ADD CONSTRAINT `mail_messages_check_21` CHECK ((state IN ('QUEUED', 'SENDING', 'SENT', 'FAILED', 'UNKNOWN', 'RECEIVED', 'REVIEW', 'IGNORED')));

ALTER TABLE `mail_messages` ADD CONSTRAINT `mail_messages_ticket_id_fkey` FOREIGN KEY (ticket_id) REFERENCES tickets(id) ON DELETE RESTRICT;

ALTER TABLE `mail_settings` ADD CONSTRAINT `mail_settings_check_2` CHECK ((id = 1));

ALTER TABLE `mail_settings` ADD CONSTRAINT `mail_settings_check_4` CHECK ((inbound_provider IN ('DISABLED', 'MICROSOFT_365')));

ALTER TABLE `mail_settings` ADD CONSTRAINT `mail_settings_check_11` CHECK ((tls_mode IN ('STARTTLS', 'TLS')));

ALTER TABLE `notification_preferences` ADD CONSTRAINT `notification_preferences_user_id_foreign` FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE;

ALTER TABLE `notifications` ADD CONSTRAINT `notifications_change_id_foreign` FOREIGN KEY (change_id) REFERENCES changes(id) ON DELETE CASCADE;

ALTER TABLE `notifications` ADD CONSTRAINT `notifications_ticket_id_foreign` FOREIGN KEY (ticket_id) REFERENCES tickets(id) ON DELETE CASCADE;

ALTER TABLE `notifications` ADD CONSTRAINT `notifications_check_9` CHECK ((type IN ('TICKET_ASSIGNED', 'TICKET_TRANSFERRED', 'PUBLIC_REPLY', 'SLA_AT_RISK', 'SLA_BREACHED', 'TICKET_UNATTENDED', 'CHANGE_APPROVAL_REQUESTED', 'CHANGE_TASK_ASSIGNED', 'CHANGE_STATUS_UPDATED', 'HELP_REQUESTED', 'HELP_ACCEPTED')));

ALTER TABLE `notifications` ADD CONSTRAINT `notifications_user_id_foreign` FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE;

ALTER TABLE `portal_ticket_submissions` ADD CONSTRAINT `portal_ticket_submissions_domain_rule_id_client_id_fkey` FOREIGN KEY (domain_rule_id, client_id) REFERENCES client_email_domains(id, client_id);

ALTER TABLE `portal_ticket_submissions` ADD CONSTRAINT `portal_ticket_submissions_check_6` CHECK ((requester_email = lower(TRIM(requester_email))));

ALTER TABLE `portal_ticket_submissions` ADD CONSTRAINT `portal_ticket_submissions_ticket_id_client_id_fkey` FOREIGN KEY (ticket_id, client_id) REFERENCES tickets(id, client_id);

ALTER TABLE `problem_history` ADD CONSTRAINT `problem_history_actor_user_id_fkey` FOREIGN KEY (actor_user_id) REFERENCES users(id);

ALTER TABLE `problem_history` ADD CONSTRAINT `problem_history_problem_id_fkey` FOREIGN KEY (problem_id) REFERENCES problems(id);

ALTER TABLE `problem_tickets` ADD CONSTRAINT `problem_tickets_linked_by_user_id_fkey` FOREIGN KEY (linked_by_user_id) REFERENCES users(id);

ALTER TABLE `problem_tickets` ADD CONSTRAINT `problem_tickets_problem_id_client_id_fkey` FOREIGN KEY (problem_id, client_id) REFERENCES problems(id, client_id) ON DELETE RESTRICT;

ALTER TABLE `problem_tickets` ADD CONSTRAINT `problem_tickets_ticket_id_client_id_fkey` FOREIGN KEY (ticket_id, client_id) REFERENCES tickets(id, client_id) ON DELETE RESTRICT;

ALTER TABLE `problems` ADD CONSTRAINT `problems_check_0` CHECK (((status NOT IN ('KNOWN_ERROR', 'RESOLVED', 'CLOSED')) OR (CHAR_LENGTH(TRIM(root_cause)) >= 3)));

ALTER TABLE `problems` ADD CONSTRAINT `problems_check_1` CHECK (((status <> 'KNOWN_ERROR') OR (CHAR_LENGTH(TRIM(workaround)) >= 3)));

ALTER TABLE `problems` ADD CONSTRAINT `problems_check_2` CHECK (((status NOT IN ('RESOLVED', 'CLOSED')) OR (CHAR_LENGTH(TRIM(resolution)) >= 3)));

ALTER TABLE `problems` ADD CONSTRAINT `problems_client_id_fkey` FOREIGN KEY (client_id) REFERENCES clients(id) ON DELETE RESTRICT;

ALTER TABLE `problems` ADD CONSTRAINT `problems_created_by_user_id_fkey` FOREIGN KEY (created_by_user_id) REFERENCES users(id) ON DELETE RESTRICT;

ALTER TABLE `problems` ADD CONSTRAINT `problems_check_8` CHECK (((CHAR_LENGTH(TRIM(description)) >= 3) AND (CHAR_LENGTH(TRIM(description)) <= 10000)));

ALTER TABLE `problems` ADD CONSTRAINT `problems_owner_user_id_fkey` FOREIGN KEY (owner_user_id) REFERENCES users(id) ON DELETE RESTRICT;

ALTER TABLE `problems` ADD CONSTRAINT `problems_check_17` CHECK ((priority IN ('P1_CRITICAL', 'P2_HIGH', 'P3_MEDIUM', 'P4_LOW')));

ALTER TABLE `problems` ADD CONSTRAINT `problems_site_id_fkey` FOREIGN KEY (site_id) REFERENCES sites(id) ON DELETE RESTRICT;

ALTER TABLE `problems` ADD CONSTRAINT `problems_check_24` CHECK ((status IN ('OPEN', 'INVESTIGATING', 'KNOWN_ERROR', 'RESOLVED', 'CLOSED')));

ALTER TABLE `problems` ADD CONSTRAINT `problems_check_26` CHECK (((CHAR_LENGTH(TRIM(title)) >= 3) AND (CHAR_LENGTH(TRIM(title)) <= 200)));

ALTER TABLE `saved_ticket_views` ADD CONSTRAINT `saved_ticket_views_user_id_foreign` FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE;

ALTER TABLE `service_templates` ADD CONSTRAINT `service_templates_category_id_fkey` FOREIGN KEY (category_id) REFERENCES categories(id) ON DELETE RESTRICT;

ALTER TABLE `service_templates` ADD CONSTRAINT `service_templates_client_id_fkey` FOREIGN KEY (client_id) REFERENCES clients(id) ON DELETE RESTRICT;

ALTER TABLE `service_templates` ADD CONSTRAINT `service_templates_created_by_user_id_fkey` FOREIGN KEY (created_by_user_id) REFERENCES users(id) ON DELETE RESTRICT;

ALTER TABLE `site_ticket_sequences` ADD CONSTRAINT `site_ticket_sequences_check_0` CHECK ((next_number > 0));

ALTER TABLE `site_ticket_sequences` ADD CONSTRAINT `site_ticket_sequences_site_id_fkey` FOREIGN KEY (site_id) REFERENCES sites(id) ON DELETE RESTRICT;

ALTER TABLE `sites` ADD CONSTRAINT `sites_client_id_foreign` FOREIGN KEY (client_id) REFERENCES clients(id) ON DELETE RESTRICT;

ALTER TABLE `sites` ADD CONSTRAINT `sites_check_9` CHECK ((REGEXP_LIKE(ticket_prefix,'^[A-Z][A-Z0-9]{1,15}$','c')));

ALTER TABLE `sla_calendars` ADD CONSTRAINT `sla_calendars_check_1` CHECK ((work_end > work_start));

ALTER TABLE `sla_calendars` ADD CONSTRAINT `sla_calendars_updated_by_user_id_foreign` FOREIGN KEY (updated_by_user_id) REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE `sla_calendars` ADD CONSTRAINT `sla_calendars_check_12` CHECK (((JSON_LENGTH(working_days) >= 1) AND (JSON_LENGTH(working_days) <= 7)));

ALTER TABLE `sla_holidays` ADD CONSTRAINT `sla_holidays_calendar_id_foreign` FOREIGN KEY (calendar_id) REFERENCES sla_calendars(id) ON DELETE CASCADE;

ALTER TABLE `sla_policies` ADD CONSTRAINT `sla_policies_calendar_id_foreign` FOREIGN KEY (calendar_id) REFERENCES sla_calendars(id) ON DELETE RESTRICT;

ALTER TABLE `sla_policies` ADD CONSTRAINT `sla_policies_check_3` CHECK ((resolution_minutes >= response_minutes));

ALTER TABLE `sla_policies` ADD CONSTRAINT `sla_policies_check_7` CHECK ((priority IN ('P1_CRITICAL', 'P2_HIGH', 'P3_MEDIUM', 'P4_LOW')));

ALTER TABLE `sla_policies` ADD CONSTRAINT `sla_policies_check_10` CHECK (((resolution_minutes >= 1) AND (resolution_minutes <= 525600)));

ALTER TABLE `sla_policies` ADD CONSTRAINT `sla_policies_check_12` CHECK (((response_minutes >= 1) AND (response_minutes <= 525600)));

ALTER TABLE `sla_policies` ADD CONSTRAINT `sla_policies_updated_by_user_id_foreign` FOREIGN KEY (updated_by_user_id) REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE `sla_policies` ADD CONSTRAINT `sla_policies_check_16` CHECK (((warning_percent >= 1) AND (warning_percent <= 99)));

ALTER TABLE `staff_group_members` ADD CONSTRAINT `staff_group_members_group_id_fkey` FOREIGN KEY (group_id) REFERENCES access_groups(id) ON DELETE CASCADE;

ALTER TABLE `staff_group_members` ADD CONSTRAINT `staff_group_members_user_id_fkey` FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE;

ALTER TABLE `ticket_approvals` ADD CONSTRAINT `ticket_approvals_decided_by_user_id_foreign` FOREIGN KEY (decided_by_user_id) REFERENCES users(id) ON DELETE RESTRICT;

ALTER TABLE `ticket_approvals` ADD CONSTRAINT `ticket_approvals_requested_by_user_id_foreign` FOREIGN KEY (requested_by_user_id) REFERENCES users(id) ON DELETE RESTRICT;

ALTER TABLE `ticket_approvals` ADD CONSTRAINT `ticket_approvals_check_7` CHECK ((status IN ('PENDING', 'APPROVED', 'REJECTED')));

ALTER TABLE `ticket_approvals` ADD CONSTRAINT `ticket_approvals_ticket_id_foreign` FOREIGN KEY (ticket_id) REFERENCES tickets(id) ON DELETE CASCADE;

ALTER TABLE `ticket_assignments` ADD CONSTRAINT `ticket_assignments_actor_user_id_foreign` FOREIGN KEY (actor_user_id) REFERENCES users(id) ON DELETE RESTRICT;

ALTER TABLE `ticket_assignments` ADD CONSTRAINT `ticket_assignments_assigned_user_id_foreign` FOREIGN KEY (assigned_user_id) REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE `ticket_assignments` ADD CONSTRAINT `ticket_assignments_previous_user_id_foreign` FOREIGN KEY (previous_user_id) REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE `ticket_assignments` ADD CONSTRAINT `ticket_assignments_ticket_id_foreign` FOREIGN KEY (ticket_id) REFERENCES tickets(id) ON DELETE CASCADE;

ALTER TABLE `ticket_attachments` ADD CONSTRAINT `ticket_attachments_check_7` CHECK (((size_bytes > 0) AND (size_bytes <= 10485760)));

ALTER TABLE `ticket_attachments` ADD CONSTRAINT `ticket_attachments_ticket_id_foreign` FOREIGN KEY (ticket_id) REFERENCES tickets(id) ON DELETE CASCADE;

ALTER TABLE `ticket_attachments` ADD CONSTRAINT `ticket_attachments_uploaded_by_user_id_foreign` FOREIGN KEY (uploaded_by_user_id) REFERENCES users(id) ON DELETE RESTRICT;

ALTER TABLE `ticket_attachments` ADD CONSTRAINT `ticket_attachments_check_15` CHECK ((visibility IN ('INTERNAL', 'PUBLIC')));

ALTER TABLE `ticket_collaborators` ADD CONSTRAINT `ticket_collaborators_added_by_user_id_foreign` FOREIGN KEY (added_by_user_id) REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE `ticket_collaborators` ADD CONSTRAINT `ticket_collaborators_ticket_id_foreign` FOREIGN KEY (ticket_id) REFERENCES tickets(id) ON DELETE CASCADE;

ALTER TABLE `ticket_collaborators` ADD CONSTRAINT `ticket_collaborators_user_id_foreign` FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE;

ALTER TABLE `ticket_comments` ADD CONSTRAINT `ticket_comments_author_user_id_foreign` FOREIGN KEY (author_user_id) REFERENCES users(id) ON DELETE RESTRICT;

ALTER TABLE `ticket_comments` ADD CONSTRAINT `ticket_comments_check_3` CHECK ((comment_type IN ('PUBLIC', 'INTERNAL')));

ALTER TABLE `ticket_comments` ADD CONSTRAINT `ticket_comments_ticket_id_foreign` FOREIGN KEY (ticket_id) REFERENCES tickets(id) ON DELETE CASCADE;

ALTER TABLE `ticket_history` ADD CONSTRAINT `ticket_history_actor_user_id_foreign` FOREIGN KEY (actor_user_id) REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE `ticket_history` ADD CONSTRAINT `ticket_history_ticket_id_foreign` FOREIGN KEY (ticket_id) REFERENCES tickets(id) ON DELETE CASCADE;

ALTER TABLE `ticket_knowledge_links` ADD CONSTRAINT `ticket_knowledge_links_article_id_fkey` FOREIGN KEY (article_id) REFERENCES knowledge_articles(id) ON DELETE RESTRICT;

ALTER TABLE `ticket_knowledge_links` ADD CONSTRAINT `ticket_knowledge_links_linked_by_user_id_fkey` FOREIGN KEY (linked_by_user_id) REFERENCES users(id) ON DELETE RESTRICT;

ALTER TABLE `ticket_knowledge_links` ADD CONSTRAINT `ticket_knowledge_links_ticket_id_fkey` FOREIGN KEY (ticket_id) REFERENCES tickets(id) ON DELETE RESTRICT;

ALTER TABLE `ticket_prefix_registry` ADD CONSTRAINT `ticket_prefix_registry_check_0` CHECK (((owner_kind = 'LEGACY') = (owner_id IS NULL)));

ALTER TABLE `ticket_prefix_registry` ADD CONSTRAINT `ticket_prefix_registry_check_1` CHECK ((owner_kind IN ('CLIENT', 'SITE', 'LEGACY')));

ALTER TABLE `ticket_prefix_registry` ADD CONSTRAINT `ticket_prefix_registry_check_4` CHECK ((REGEXP_LIKE(prefix,'^[A-Z][A-Z0-9]{1,15}$','c')));

ALTER TABLE `ticket_sla` ADD CONSTRAINT `ticket_sla_calendar_id_foreign` FOREIGN KEY (calendar_id) REFERENCES sla_calendars(id) ON DELETE RESTRICT;

ALTER TABLE `ticket_sla` ADD CONSTRAINT `ticket_sla_policy_id_foreign` FOREIGN KEY (policy_id) REFERENCES sla_policies(id) ON DELETE RESTRICT;

ALTER TABLE `ticket_sla` ADD CONSTRAINT `ticket_sla_ticket_id_foreign` FOREIGN KEY (ticket_id) REFERENCES tickets(id) ON DELETE CASCADE;

ALTER TABLE `ticket_tags` ADD CONSTRAINT `ticket_tags_added_by_user_id_foreign` FOREIGN KEY (added_by_user_id) REFERENCES users(id) ON DELETE RESTRICT;

ALTER TABLE `ticket_tags` ADD CONSTRAINT `ticket_tags_tag_id_foreign` FOREIGN KEY (tag_id) REFERENCES tags(id) ON DELETE RESTRICT;

ALTER TABLE `ticket_tags` ADD CONSTRAINT `ticket_tags_ticket_id_foreign` FOREIGN KEY (ticket_id) REFERENCES tickets(id) ON DELETE CASCADE;

ALTER TABLE `tickets` ADD CONSTRAINT `tickets_check_0` CHECK ((approval_status IN ('NOT_REQUIRED', 'PENDING', 'APPROVED', 'REJECTED')));

ALTER TABLE `tickets` ADD CONSTRAINT `tickets_assigned_user_id_foreign` FOREIGN KEY (assigned_user_id) REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE `tickets` ADD CONSTRAINT `tickets_category_id_foreign` FOREIGN KEY (category_id) REFERENCES categories(id) ON DELETE RESTRICT;

ALTER TABLE `tickets` ADD CONSTRAINT `tickets_client_id_foreign` FOREIGN KEY (client_id) REFERENCES clients(id) ON DELETE RESTRICT;

ALTER TABLE `tickets` ADD CONSTRAINT `tickets_created_by_user_id_foreign` FOREIGN KEY (created_by_user_id) REFERENCES users(id) ON DELETE RESTRICT;

ALTER TABLE `tickets` ADD CONSTRAINT `tickets_department_id_foreign` FOREIGN KEY (department_id) REFERENCES departments(id) ON DELETE SET NULL;

ALTER TABLE `tickets` ADD CONSTRAINT `tickets_check_14` CHECK ((priority IN ('P1_CRITICAL', 'P2_HIGH', 'P3_MEDIUM', 'P4_LOW')));

ALTER TABLE `tickets` ADD CONSTRAINT `tickets_requester_client_fk` FOREIGN KEY (requester_id, client_id) REFERENCES contacts(id, client_id) ON DELETE RESTRICT;

ALTER TABLE `tickets` ADD CONSTRAINT `tickets_requester_id_foreign` FOREIGN KEY (requester_id) REFERENCES contacts(id) ON DELETE RESTRICT;

ALTER TABLE `tickets` ADD CONSTRAINT `tickets_site_client_fk` FOREIGN KEY (site_id, client_id) REFERENCES sites(id, client_id) ON DELETE RESTRICT;

ALTER TABLE `tickets` ADD CONSTRAINT `tickets_site_id_foreign` FOREIGN KEY (site_id) REFERENCES sites(id) ON DELETE SET NULL;

ALTER TABLE `tickets` ADD CONSTRAINT `tickets_source_fk` FOREIGN KEY (source) REFERENCES ticket_sources(code) ON UPDATE CASCADE ON DELETE RESTRICT;

ALTER TABLE `tickets` ADD CONSTRAINT `tickets_check_23` CHECK ((status IN ('NEW', 'OPEN', 'IN_PROGRESS', 'WAITING_FOR_CLIENT', 'WAITING_FOR_VENDOR', 'ON_HOLD', 'RESOLVED', 'CLOSED', 'CANCELLED')));

ALTER TABLE `user_permissions` ADD CONSTRAINT `user_permissions_granted_by_foreign` FOREIGN KEY (granted_by) REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE `user_permissions` ADD CONSTRAINT `user_permissions_permission_code_foreign` FOREIGN KEY (permission_code) REFERENCES permissions(code) ON DELETE CASCADE;

ALTER TABLE `user_permissions` ADD CONSTRAINT `user_permissions_user_id_foreign` FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE;

ALTER TABLE `user_roles` ADD CONSTRAINT `user_roles_granted_by_foreign` FOREIGN KEY (granted_by) REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE `user_roles` ADD CONSTRAINT `user_roles_role_code_foreign` FOREIGN KEY (role_code) REFERENCES roles(code);

ALTER TABLE `user_roles` ADD CONSTRAINT `user_roles_user_id_foreign` FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE;

ALTER TABLE `user_sites` ADD CONSTRAINT `user_sites_site_id_foreign` FOREIGN KEY (site_id) REFERENCES sites(id) ON DELETE CASCADE;

ALTER TABLE `user_sites` ADD CONSTRAINT `user_sites_user_id_foreign` FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE;

ALTER TABLE `users` ADD CONSTRAINT `users_check_1` CHECK ((change_access IN ('NONE', 'CONTRIBUTOR', 'COORDINATOR', 'APPROVER', 'CHANGE_MANAGER')));

ALTER TABLE `users` ADD CONSTRAINT `users_supervisor_user_id_foreign` FOREIGN KEY (supervisor_user_id) REFERENCES users(id) ON DELETE SET NULL;

ALTER TABLE `users` ADD CONSTRAINT `users_check_14` CHECK ((work_scope IN ('INTERNAL', 'CLIENT', 'BOTH', 'ALL')));

ALTER TABLE access_groups ADD CONSTRAINT access_groups_permissions_kind CHECK (JSON_TYPE(permissions)='ARRAY' AND ((kind='CUSTOMER' AND client_id IS NOT NULL AND JSON_CONTAINS(JSON_ARRAY('PORTAL_ORGANIZATION_VIEW','PORTAL_ORGANIZATION_REPLY'),permissions)) OR (kind='STAFF' AND client_id IS NULL AND JSON_CONTAINS(JSON_ARRAY('TICKETS_TEAM_VIEW','TICKETS_ASSIGN','TICKETS_APPROVE','CATEGORY_MANAGE','SLA_MANAGE','REPORTS_VIEW','TAGS_MANAGE'),permissions))));

ALTER TABLE access_groups ADD CONSTRAINT access_groups_reply_requires_view CHECK (NOT JSON_CONTAINS(permissions,JSON_QUOTE('PORTAL_ORGANIZATION_REPLY')) OR JSON_CONTAINS(permissions,JSON_QUOTE('PORTAL_ORGANIZATION_VIEW')));

START TRANSACTION;

INSERT INTO `app_schema_state` (`id`,`version`,`created_at`) VALUES (1,1,'2026-10-26 11:08:26.796');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('bfbc10e5-ad29-4a3a-9009-356297d08393',NULL,'General Support','Requests that do not fit another available category.',1,'2026-10-26 11:08:27.091','2026-10-26 11:08:27.091');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('10000000-0000-4000-8000-000000000001',NULL,'Hardware','Hardware support requests',1,'2026-10-26 11:08:27.147','2026-10-26 11:08:27.147');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('10000000-0000-4000-8000-000000000002',NULL,'Software','Software support requests',1,'2026-10-26 11:08:27.162','2026-10-26 11:08:27.162');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('10000000-0000-4000-8000-000000000003',NULL,'Microsoft 365','Microsoft 365 support requests',1,'2026-10-26 11:08:27.173','2026-10-26 11:08:27.173');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('10000000-0000-4000-8000-000000000004',NULL,'Identity and Access','Identity and Access support requests',1,'2026-10-26 11:08:27.186','2026-10-26 11:08:27.186');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('10000000-0000-4000-8000-000000000005',NULL,'Network and Connectivity','Network and Connectivity support requests',1,'2026-10-26 11:08:27.199','2026-10-26 11:08:27.199');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('10000000-0000-4000-8000-000000000006',NULL,'Infrastructure','Infrastructure support requests',1,'2026-10-26 11:08:27.210','2026-10-26 11:08:27.210');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('10000000-0000-4000-8000-000000000007',NULL,'Security','Security support requests',1,'2026-10-26 11:08:27.221','2026-10-26 11:08:27.221');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('10000000-0000-4000-8000-000000000008',NULL,'Other','Other support requests',1,'2026-10-26 11:08:27.232','2026-10-26 11:08:27.232');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('10000000-0000-4000-8000-000000000009',NULL,'Windows and Operating System','Windows and Operating System support requests',1,'2026-10-26 11:08:27.241','2026-10-26 11:08:27.241');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('10000000-0000-4000-8000-000000000010',NULL,'Printing and Scanning','Printing and Scanning support requests',1,'2026-10-26 11:08:27.262','2026-10-26 11:08:27.262');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('10000000-0000-4000-8000-000000000011',NULL,'File and Data Services','File and Data Services support requests',1,'2026-10-26 11:08:27.278','2026-10-26 11:08:27.278');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('10000000-0000-4000-8000-000000000012',NULL,'Business Applications','Business Applications support requests',1,'2026-10-26 11:08:27.290','2026-10-26 11:08:27.290');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('10000000-0000-4000-8000-000000000013',NULL,'User Lifecycle','User Lifecycle support requests',1,'2026-10-26 11:08:27.302','2026-10-26 11:08:27.302');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('10000000-0000-4000-8000-000000000014',NULL,'Mobile and Telephony','Mobile and Telephony support requests',1,'2026-10-26 11:08:27.315','2026-10-26 11:08:27.315');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000001','10000000-0000-4000-8000-000000000001','Desktop or Laptop','Desktop or Laptop tickets',1,'2026-10-26 11:08:27.150','2026-10-26 11:08:27.150');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000002','10000000-0000-4000-8000-000000000001','Printer or Scanner','Printer or Scanner tickets',1,'2026-10-26 11:08:27.154','2026-10-26 11:08:27.154');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000003','10000000-0000-4000-8000-000000000001','Peripheral','Peripheral tickets',1,'2026-10-26 11:08:27.156','2026-10-26 11:08:27.156');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000004','10000000-0000-4000-8000-000000000001','Server or Storage','Server or Storage tickets',1,'2026-10-26 11:08:27.159','2026-10-26 11:08:27.159');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000005','10000000-0000-4000-8000-000000000002','Application Issue','Application Issue tickets',1,'2026-10-26 11:08:27.164','2026-10-26 11:08:27.164');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000006','10000000-0000-4000-8000-000000000002','Installation or Upgrade','Installation or Upgrade tickets',1,'2026-10-26 11:08:27.166','2026-10-26 11:08:27.166');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000007','10000000-0000-4000-8000-000000000002','Licensing','Licensing tickets',1,'2026-10-26 11:08:27.168','2026-10-26 11:08:27.168');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000008','10000000-0000-4000-8000-000000000002','Operating System','Operating System tickets',1,'2026-10-26 11:08:27.171','2026-10-26 11:08:27.171');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000009','10000000-0000-4000-8000-000000000003','Outlook or Email','Outlook or Email tickets',1,'2026-10-26 11:08:27.176','2026-10-26 11:08:27.176');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000010','10000000-0000-4000-8000-000000000003','Microsoft Teams','Microsoft Teams tickets',1,'2026-10-26 11:08:27.178','2026-10-26 11:08:27.178');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000011','10000000-0000-4000-8000-000000000003','SharePoint or OneDrive','SharePoint or OneDrive tickets',1,'2026-10-26 11:08:27.180','2026-10-26 11:08:27.180');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000012','10000000-0000-4000-8000-000000000003','Office Applications','Office Applications tickets',1,'2026-10-26 11:08:27.183','2026-10-26 11:08:27.183');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000013','10000000-0000-4000-8000-000000000004','Password Reset','Password Reset tickets',1,'2026-10-26 11:08:27.189','2026-10-26 11:08:27.189');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000014','10000000-0000-4000-8000-000000000004','Account Lockout','Account Lockout tickets',1,'2026-10-26 11:08:27.192','2026-10-26 11:08:27.192');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000015','10000000-0000-4000-8000-000000000004','Multi-Factor Authentication','Multi-Factor Authentication tickets',1,'2026-10-26 11:08:27.195','2026-10-26 11:08:27.195');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000016','10000000-0000-4000-8000-000000000004','Permissions or Access','Permissions or Access tickets',1,'2026-10-26 11:08:27.197','2026-10-26 11:08:27.197');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000017','10000000-0000-4000-8000-000000000005','LAN or Wi-Fi','LAN or Wi-Fi tickets',1,'2026-10-26 11:08:27.201','2026-10-26 11:08:27.201');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000018','10000000-0000-4000-8000-000000000005','VPN','VPN tickets',1,'2026-10-26 11:08:27.204','2026-10-26 11:08:27.204');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000019','10000000-0000-4000-8000-000000000005','Internet','Internet tickets',1,'2026-10-26 11:08:27.206','2026-10-26 11:08:27.206');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000020','10000000-0000-4000-8000-000000000005','DNS or DHCP','DNS or DHCP tickets',1,'2026-10-26 11:08:27.208','2026-10-26 11:08:27.208');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000021','10000000-0000-4000-8000-000000000006','Windows Server','Windows Server tickets',1,'2026-10-26 11:08:27.212','2026-10-26 11:08:27.212');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000022','10000000-0000-4000-8000-000000000006','Active Directory','Active Directory tickets',1,'2026-10-26 11:08:27.214','2026-10-26 11:08:27.214');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000023','10000000-0000-4000-8000-000000000006','Hyper-V or Virtual Machine','Hyper-V or Virtual Machine tickets',1,'2026-10-26 11:08:27.216','2026-10-26 11:08:27.216');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000024','10000000-0000-4000-8000-000000000006','Backup or Veeam','Backup or Veeam tickets',1,'2026-10-26 11:08:27.218','2026-10-26 11:08:27.218');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000025','10000000-0000-4000-8000-000000000007','Phishing or Spam','Phishing or Spam tickets',1,'2026-10-26 11:08:27.224','2026-10-26 11:08:27.224');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000026','10000000-0000-4000-8000-000000000007','Endpoint Security','Endpoint Security tickets',1,'2026-10-26 11:08:27.226','2026-10-26 11:08:27.226');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000027','10000000-0000-4000-8000-000000000007','Firewall','Firewall tickets',1,'2026-10-26 11:08:27.228','2026-10-26 11:08:27.228');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000028','10000000-0000-4000-8000-000000000007','Security Incident','Security Incident tickets',1,'2026-10-26 11:08:27.230','2026-10-26 11:08:27.230');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000029','10000000-0000-4000-8000-000000000008','General Support','General Support tickets',1,'2026-10-26 11:08:27.234','2026-10-26 11:08:27.234');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000030','10000000-0000-4000-8000-000000000008','Service Request','Service Request tickets',1,'2026-10-26 11:08:27.237','2026-10-26 11:08:27.237');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000031','10000000-0000-4000-8000-000000000008','Other Issue','Other Issue tickets',1,'2026-10-26 11:08:27.239','2026-10-26 11:08:27.239');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000032','10000000-0000-4000-8000-000000000009','Windows Login','Windows Login tickets',1,'2026-10-26 11:08:27.243','2026-10-26 11:08:27.243');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000033','10000000-0000-4000-8000-000000000009','User Profile','User Profile tickets',1,'2026-10-26 11:08:27.246','2026-10-26 11:08:27.246');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000034','10000000-0000-4000-8000-000000000009','Startup or Boot','Startup or Boot tickets',1,'2026-10-26 11:08:27.249','2026-10-26 11:08:27.249');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000035','10000000-0000-4000-8000-000000000009','Windows Update','Windows Update tickets',1,'2026-10-26 11:08:27.252','2026-10-26 11:08:27.252');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000036','10000000-0000-4000-8000-000000000009','Driver Issue','Driver Issue tickets',1,'2026-10-26 11:08:27.255','2026-10-26 11:08:27.255');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000037','10000000-0000-4000-8000-000000000009','System Crash','System Crash tickets',1,'2026-10-26 11:08:27.257','2026-10-26 11:08:27.257');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000038','10000000-0000-4000-8000-000000000010','Printer Offline','Printer Offline tickets',1,'2026-10-26 11:08:27.264','2026-10-26 11:08:27.264');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000039','10000000-0000-4000-8000-000000000010','Print Queue','Print Queue tickets',1,'2026-10-26 11:08:27.266','2026-10-26 11:08:27.266');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000040','10000000-0000-4000-8000-000000000010','Printer Driver','Printer Driver tickets',1,'2026-10-26 11:08:27.269','2026-10-26 11:08:27.269');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000041','10000000-0000-4000-8000-000000000010','Scanning Issue','Scanning Issue tickets',1,'2026-10-26 11:08:27.271','2026-10-26 11:08:27.271');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000042','10000000-0000-4000-8000-000000000010','Print Quality','Print Quality tickets',1,'2026-10-26 11:08:27.274','2026-10-26 11:08:27.274');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000043','10000000-0000-4000-8000-000000000010','Network Printer','Network Printer tickets',1,'2026-10-26 11:08:27.276','2026-10-26 11:08:27.276');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000044','10000000-0000-4000-8000-000000000011','Shared Folder','Shared Folder tickets',1,'2026-10-26 11:08:27.280','2026-10-26 11:08:27.280');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000045','10000000-0000-4000-8000-000000000011','File Permissions','File Permissions tickets',1,'2026-10-26 11:08:27.282','2026-10-26 11:08:27.282');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000046','10000000-0000-4000-8000-000000000011','Deleted File Recovery','Deleted File Recovery tickets',1,'2026-10-26 11:08:27.284','2026-10-26 11:08:27.284');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000047','10000000-0000-4000-8000-000000000011','Storage Quota','Storage Quota tickets',1,'2026-10-26 11:08:27.286','2026-10-26 11:08:27.286');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000048','10000000-0000-4000-8000-000000000011','Data Transfer','Data Transfer tickets',1,'2026-10-26 11:08:27.288','2026-10-26 11:08:27.288');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000049','10000000-0000-4000-8000-000000000012','ERP or CRM','ERP or CRM tickets',1,'2026-10-26 11:08:27.292','2026-10-26 11:08:27.292');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000050','10000000-0000-4000-8000-000000000012','Accounting Application','Accounting Application tickets',1,'2026-10-26 11:08:27.294','2026-10-26 11:08:27.294');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000051','10000000-0000-4000-8000-000000000012','HR or Payroll','HR or Payroll tickets',1,'2026-10-26 11:08:27.296','2026-10-26 11:08:27.296');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000052','10000000-0000-4000-8000-000000000012','Database Access','Database Access tickets',1,'2026-10-26 11:08:27.298','2026-10-26 11:08:27.298');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000053','10000000-0000-4000-8000-000000000012','Custom Application','Custom Application tickets',1,'2026-10-26 11:08:27.300','2026-10-26 11:08:27.300');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000054','10000000-0000-4000-8000-000000000013','New User Onboarding','New User Onboarding tickets',1,'2026-10-26 11:08:27.304','2026-10-26 11:08:27.304');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000055','10000000-0000-4000-8000-000000000013','User Offboarding','User Offboarding tickets',1,'2026-10-26 11:08:27.306','2026-10-26 11:08:27.306');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000056','10000000-0000-4000-8000-000000000013','Department Transfer','Department Transfer tickets',1,'2026-10-26 11:08:27.308','2026-10-26 11:08:27.308');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000057','10000000-0000-4000-8000-000000000013','Equipment Allocation','Equipment Allocation tickets',1,'2026-10-26 11:08:27.311','2026-10-26 11:08:27.311');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000058','10000000-0000-4000-8000-000000000013','Access Removal','Access Removal tickets',1,'2026-10-26 11:08:27.313','2026-10-26 11:08:27.313');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000059','10000000-0000-4000-8000-000000000014','Mobile Device','Mobile Device tickets',1,'2026-10-26 11:08:27.317','2026-10-26 11:08:27.317');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000060','10000000-0000-4000-8000-000000000014','VoIP or Extension','VoIP or Extension tickets',1,'2026-10-26 11:08:27.321','2026-10-26 11:08:27.321');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000061','10000000-0000-4000-8000-000000000014','Softphone','Softphone tickets',1,'2026-10-26 11:08:27.323','2026-10-26 11:08:27.323');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000062','10000000-0000-4000-8000-000000000014','Calling Issue','Calling Issue tickets',1,'2026-10-26 11:08:27.326','2026-10-26 11:08:27.326');

INSERT INTO `categories` (`id`,`parent_id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('20000000-0000-4000-8000-000000000063','10000000-0000-4000-8000-000000000014','Mobile Connectivity','Mobile Connectivity tickets',1,'2026-10-26 11:08:27.328','2026-10-26 11:08:27.328');

INSERT INTO `change_governance_policy` (`id`,`high_risk_cab`,`minimum_approvals`,`review_required`) VALUES (1,1,1,1);

INSERT INTO `clients` (`id`,`name`,`status`,`primary_email`,`primary_phone`,`notes`,`created_at`,`updated_at`,`organization_type`,`ticket_prefix`) VALUES ('00000000-0000-4000-8000-000000000132','Amnikon Internal','ACTIVE',NULL,NULL,'Protected organization for internal service-desk work.','2026-10-26 11:08:27.419','2026-10-26 11:08:27.419','INTERNAL','INT');

INSERT INTO `departments` (`id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('fb21b0cf-4432-4119-ad2a-9ce98798d983','Service Desk','Ticket intake, triage and end-user support',1,'2026-10-26 11:08:27.642','2026-10-26 11:08:27.642');

INSERT INTO `departments` (`id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('2ab9d27e-568a-488f-83cb-e7197e3b4e97','Infrastructure & Servers','Windows Server, virtualization, storage and backup',1,'2026-10-26 11:08:27.643','2026-10-26 11:08:27.643');

INSERT INTO `departments` (`id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('9282ef1a-3032-438d-8a33-30be5f08ad1a','Network','Switching, routing, wireless, VPN and firewalls',1,'2026-10-26 11:08:27.644','2026-10-26 11:08:27.644');

INSERT INTO `departments` (`id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('c2486386-209b-4e9f-8e85-154047d789ef','Microsoft 365','Exchange Online, Teams, SharePoint, OneDrive and Entra ID',1,'2026-10-26 11:08:27.645','2026-10-26 11:08:27.645');

INSERT INTO `departments` (`id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('35f9b218-ee50-42fc-a870-069b1500ff9e','Security','Security controls, incidents, access and compliance',1,'2026-10-26 11:08:27.646','2026-10-26 11:08:27.646');

INSERT INTO `departments` (`id`,`name`,`description`,`active`,`created_at`,`updated_at`) VALUES ('0f7ebe3c-42ec-4cf2-bd5e-20737b3cc753','Applications','Business applications and application support',1,'2026-10-26 11:08:27.647','2026-10-26 11:08:27.647');

INSERT INTO `mail_settings` (`id`,`enabled`,`smtp_host`,`smtp_port`,`tls_mode`,`smtp_username`,`password_ciphertext`,`ticket_address`,`change_address`,`inbound_provider`,`inbound_since`,`updated_at`) VALUES (1,0,'mail.smtp2go.com',587,'STARTTLS','',NULL,'it@amnikontechnologies.com','change@amnikontechnologies.com','DISABLED',NULL,'2026-10-26 11:08:27.686');

INSERT INTO `permissions` (`code`,`name`,`description`) VALUES ('TICKETS_TEAM_VIEW','View site team tickets','View other technicians tickets at assigned sites');

INSERT INTO `permissions` (`code`,`name`,`description`) VALUES ('TICKETS_ASSIGN','Assign tickets','Assign or transfer tickets within permitted scope');

INSERT INTO `permissions` (`code`,`name`,`description`) VALUES ('TICKETS_APPROVE','Approve tickets','Approve or reject ticket approval requests');

INSERT INTO `permissions` (`code`,`name`,`description`) VALUES ('CATEGORY_MANAGE','Manage categories','Create, edit and remove ticket categories');

INSERT INTO `permissions` (`code`,`name`,`description`) VALUES ('SLA_MANAGE','Manage SLA','Configure SLA policies');

INSERT INTO `permissions` (`code`,`name`,`description`) VALUES ('REPORTS_VIEW','View reports','View and export operational reports');

INSERT INTO `permissions` (`code`,`name`,`description`) VALUES ('USERS_MANAGE','Manage users','Manage users, roles, permissions and scope');

INSERT INTO `permissions` (`code`,`name`,`description`) VALUES ('CLIENTS_MANAGE','Manage clients','Manage client records');

INSERT INTO `permissions` (`code`,`name`,`description`) VALUES ('CONTACTS_MANAGE','Manage contacts','Manage requester records');

INSERT INTO `permissions` (`code`,`name`,`description`) VALUES ('SITES_MANAGE','Manage sites','Manage client sites');

INSERT INTO `permissions` (`code`,`name`,`description`) VALUES ('TAGS_MANAGE','Manage tags','Manage ticket tags');

INSERT INTO `permissions` (`code`,`name`,`description`) VALUES ('AUDIT_VIEW','View audit history','Review system audit events');

INSERT INTO `roles` (`code`,`name`,`description`,`created_at`) VALUES ('USER','User','Built-in user role','2026-10-26 11:08:26.809');

INSERT INTO `roles` (`code`,`name`,`description`,`created_at`) VALUES ('TECHNICIAN','Technician','Built-in technician role','2026-10-26 11:08:26.809');

INSERT INTO `roles` (`code`,`name`,`description`,`created_at`) VALUES ('TEAM_LEAD','Team Lead','Built-in team_lead role','2026-10-26 11:08:26.809');

INSERT INTO `roles` (`code`,`name`,`description`,`created_at`) VALUES ('MANAGER','Manager','Built-in manager role','2026-10-26 11:08:26.809');

INSERT INTO `roles` (`code`,`name`,`description`,`created_at`) VALUES ('ADMIN','Admin','Built-in admin role','2026-10-26 11:08:26.809');

INSERT INTO `roles` (`code`,`name`,`description`,`created_at`) VALUES ('INTERN','Intern','Supervised internal service-desk contributor','2026-10-26 11:08:27.416');

INSERT INTO `sla_calendars` (`id`,`name`,`timezone`,`working_days`,`work_start`,`work_end`,`active`,`updated_by_user_id`,`created_at`,`updated_at`) VALUES ('00000000-0000-4000-8000-000000000126','Standard Support Hours','Asia/Kolkata','[1,2,3,4,5]','09:00:00','18:00:00',1,NULL,'2026-10-26 11:08:27.359','2026-10-26 11:08:27.359');

INSERT INTO `ticket_prefix_registry` (`prefix`,`owner_kind`,`owner_id`) VALUES ('INT','CLIENT','00000000-0000-4000-8000-000000000132');

INSERT INTO `ticket_sources` (`code`,`name`,`active`,`sort_order`,`created_at`,`updated_at`) VALUES ('MISSIVE','Missive',1,10,'2026-10-26 11:08:27.125','2026-10-26 11:08:27.125');

INSERT INTO `ticket_sources` (`code`,`name`,`active`,`sort_order`,`created_at`,`updated_at`) VALUES ('MICROSOFT_TEAMS','Microsoft Teams',1,20,'2026-10-26 11:08:27.125','2026-10-26 11:08:27.125');

INSERT INTO `ticket_sources` (`code`,`name`,`active`,`sort_order`,`created_at`,`updated_at`) VALUES ('PHONE_CALL','Phone Call',1,30,'2026-10-26 11:08:27.125','2026-10-26 11:08:27.125');

INSERT INTO `ticket_sources` (`code`,`name`,`active`,`sort_order`,`created_at`,`updated_at`) VALUES ('SMS','SMS',1,40,'2026-10-26 11:08:27.125','2026-10-26 11:08:27.125');

INSERT INTO `ticket_sources` (`code`,`name`,`active`,`sort_order`,`created_at`,`updated_at`) VALUES ('EMAIL','Email',1,50,'2026-10-26 11:08:27.125','2026-10-26 11:08:27.125');

INSERT INTO `ticket_sources` (`code`,`name`,`active`,`sort_order`,`created_at`,`updated_at`) VALUES ('OTHER','Other',1,60,'2026-10-26 11:08:27.138','2026-10-26 11:08:27.138');

INSERT INTO `ticket_sources` (`code`,`name`,`active`,`sort_order`,`created_at`,`updated_at`) VALUES ('CLIENT_PORTAL','Client Portal',1,100,'2026-10-26 11:08:27.686','2026-10-26 11:08:27.686');

INSERT INTO `client_mail_routes` (`address`,`client_id`,`active`,`outbound_authorized`) VALUES ('it@amnikontechnologies.com','00000000-0000-4000-8000-000000000132',1,0);

INSERT INTO `client_ticket_sequences` (`client_id`,`next_number`) VALUES ('00000000-0000-4000-8000-000000000132',1);

INSERT INTO `sla_policies` (`id`,`priority`,`response_minutes`,`resolution_minutes`,`warning_percent`,`active`,`updated_by_user_id`,`created_at`,`updated_at`,`calendar_id`) VALUES ('6422b0ec-10d5-4c6d-8c7e-b59340830279','P1_CRITICAL',30,240,75,1,NULL,'2026-10-26 11:08:27.070','2026-10-26 11:08:27.070','00000000-0000-4000-8000-000000000126');

INSERT INTO `sla_policies` (`id`,`priority`,`response_minutes`,`resolution_minutes`,`warning_percent`,`active`,`updated_by_user_id`,`created_at`,`updated_at`,`calendar_id`) VALUES ('3abb9c45-c589-4d84-b92a-2b51617eac5c','P2_HIGH',60,480,75,1,NULL,'2026-10-26 11:08:27.070','2026-10-26 11:08:27.070','00000000-0000-4000-8000-000000000126');

INSERT INTO `sla_policies` (`id`,`priority`,`response_minutes`,`resolution_minutes`,`warning_percent`,`active`,`updated_by_user_id`,`created_at`,`updated_at`,`calendar_id`) VALUES ('edfe5c6f-152f-4fd8-8bf3-3a0b79f1fd1b','P3_MEDIUM',240,960,75,1,NULL,'2026-10-26 11:08:27.070','2026-10-26 11:08:27.070','00000000-0000-4000-8000-000000000126');

INSERT INTO `sla_policies` (`id`,`priority`,`response_minutes`,`resolution_minutes`,`warning_percent`,`active`,`updated_by_user_id`,`created_at`,`updated_at`,`calendar_id`) VALUES ('8396eb41-278a-46fa-905a-01caae193786','P4_LOW',480,2400,75,1,NULL,'2026-10-26 11:08:27.070','2026-10-26 11:08:27.070','00000000-0000-4000-8000-000000000126');

INSERT INTO users(id,entra_object_id,entra_tenant_id,email,display_name,work_scope,change_access) VALUES ('10000000-0000-4000-8000-000000000001','10000000-0000-4000-8000-000000000001','00000000-0000-0000-0000-000000000000','admin@local.test','Local Admin','ALL','CHANGE_MANAGER');

INSERT INTO user_roles(user_id,role_code) VALUES ('10000000-0000-4000-8000-000000000001','ADMIN');

INSERT INTO users(id,entra_object_id,entra_tenant_id,email,display_name,work_scope,change_access) VALUES ('10000000-0000-4000-8000-000000000002','10000000-0000-4000-8000-000000000002','00000000-0000-0000-0000-000000000000','user@local.test','Local User','CLIENT','NONE');

INSERT INTO user_roles(user_id,role_code) VALUES ('10000000-0000-4000-8000-000000000002','USER');

UPDATE sla_calendars SET sql_timezone=timezone;

COMMIT;

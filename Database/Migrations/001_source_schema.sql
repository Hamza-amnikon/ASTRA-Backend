-- REHEARSAL ONLY: review coverage.json before applying to a new empty database.

SET NAMES utf8mb4 COLLATE utf8mb4_0900_as_cs;

SET SESSION time_zone = '+00:00';

SET SESSION sql_mode = 'STRICT_ALL_TABLES,NO_ZERO_DATE,NO_ZERO_IN_DATE,ERROR_FOR_DIVISION_BY_ZERO,NO_ENGINE_SUBSTITUTION';

CREATE TABLE `access_groups` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `name` VARCHAR(255) NOT NULL,
  `description` LONGTEXT NOT NULL DEFAULT (''),
  `kind` VARCHAR(255) NOT NULL,
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `permissions` JSON NOT NULL DEFAULT (JSON_ARRAY()),
  `active` BOOLEAN NOT NULL DEFAULT true,
  `revision` INT NOT NULL DEFAULT 1,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UNIQUE KEY `access_groups_id_client_id_key` (`id`, `client_id`),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `app_schema_state` (
  `id` SMALLINT NOT NULL,
  `version` INT NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `assets` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `site_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `name` LONGTEXT NOT NULL,
  `asset_type` LONGTEXT NOT NULL,
  `serial_number` VARCHAR(255),
  `external_reference` LONGTEXT,
  `active` BOOLEAN NOT NULL DEFAULT true,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `audit_logs` (
  `id` BIGINT NOT NULL AUTO_INCREMENT,
  `actor_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `action` LONGTEXT NOT NULL,
  `entity_type` VARCHAR(255) NOT NULL,
  `entity_id` VARCHAR(255),
  `details` JSON NOT NULL DEFAULT (JSON_OBJECT()),
  `request_id` LONGTEXT,
  `source_ip` VARCHAR(49),
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `cab_groups` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `name` VARCHAR(255) NOT NULL,
  `quorum` INT NOT NULL,
  `active` BOOLEAN NOT NULL DEFAULT true,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UNIQUE KEY `cab_groups_name_key` (`name`),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `cab_members` (
  `group_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  PRIMARY KEY (`group_id`, `user_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `categories` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `parent_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `name` VARCHAR(255) NOT NULL,
  `description` LONGTEXT,
  `active` BOOLEAN NOT NULL DEFAULT true,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `change_approvals` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `change_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `approver_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `department_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `sequence` INT NOT NULL DEFAULT 1,
  `required` BOOLEAN NOT NULL DEFAULT true,
  `status` LONGTEXT NOT NULL DEFAULT ('PENDING'),
  `decision_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `decision_note` LONGTEXT,
  `decided_at` DATETIME(6),
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `revision` INT NOT NULL DEFAULT 1,
  `cab_group_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `change_attachments` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `change_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `uploaded_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `file_name` LONGTEXT NOT NULL,
  `content_type` LONGTEXT NOT NULL,
  `size_bytes` BIGINT NOT NULL,
  `sha256` LONGTEXT NOT NULL,
  `content` LONGBLOB NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `change_blackouts` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `name` LONGTEXT NOT NULL,
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `site_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `starts_at` DATETIME(6) NOT NULL,
  `ends_at` DATETIME(6) NOT NULL,
  `active` BOOLEAN NOT NULL DEFAULT true,
  `created_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `change_communications` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `change_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `recipient_emails` JSON NOT NULL,
  `subject` LONGTEXT NOT NULL,
  `body` LONGTEXT NOT NULL,
  `include_client` BOOLEAN NOT NULL DEFAULT false,
  `status` LONGTEXT NOT NULL DEFAULT ('DRAFT'),
  `attempts` INT NOT NULL DEFAULT 0,
  `last_error_code` LONGTEXT,
  `sent_at` DATETIME(6),
  `created_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `change_governance_policy` (
  `id` SMALLINT NOT NULL,
  `high_risk_cab` BOOLEAN NOT NULL DEFAULT true,
  `minimum_approvals` INT NOT NULL DEFAULT 1,
  `review_required` BOOLEAN NOT NULL DEFAULT true,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `change_history` (
  `id` BIGINT NOT NULL AUTO_INCREMENT,
  `change_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `actor_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `event` LONGTEXT NOT NULL,
  `details` JSON NOT NULL DEFAULT (JSON_OBJECT()),
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `change_reviews` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `change_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `revision` INT NOT NULL,
  `outcome` LONGTEXT NOT NULL,
  `summary` LONGTEXT NOT NULL,
  `follow_up` LONGTEXT NOT NULL,
  `actor_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `change_stakeholders` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `change_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `external_name` LONGTEXT,
  `external_email` VARCHAR(255),
  `stakeholder_role` LONGTEXT NOT NULL DEFAULT ('STAKEHOLDER'),
  `added_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `contact_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `change_tasks` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `change_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `department_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `assigned_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `title` LONGTEXT NOT NULL,
  `instructions` LONGTEXT NOT NULL DEFAULT (''),
  `status` LONGTEXT NOT NULL DEFAULT ('TODO'),
  `due_at` DATETIME(6),
  `completed_at` DATETIME(6),
  `update_note` LONGTEXT,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `change_teams` (
  `change_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `department_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `role` LONGTEXT NOT NULL,
  `status` LONGTEXT NOT NULL DEFAULT ('PENDING'),
  PRIMARY KEY (`change_id`, `department_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `change_templates` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `name` VARCHAR(255) NOT NULL,
  `category` LONGTEXT NOT NULL,
  `description` LONGTEXT NOT NULL,
  `implementation_plan` LONGTEXT NOT NULL,
  `test_plan` LONGTEXT NOT NULL,
  `rollback_plan` LONGTEXT NOT NULL,
  `active` BOOLEAN NOT NULL DEFAULT true,
  `created_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UNIQUE KEY `change_templates_name_key` (`name`),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `change_ticket_links` (
  `change_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `linked_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  PRIMARY KEY (`change_id`, `ticket_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `change_updates` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `change_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `author_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `update_type` LONGTEXT NOT NULL,
  `body` LONGTEXT NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `changes` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `change_number` VARCHAR(255) NOT NULL,
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `site_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `department_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `title` LONGTEXT NOT NULL,
  `description` LONGTEXT NOT NULL,
  `category` LONGTEXT NOT NULL,
  `change_type` LONGTEXT NOT NULL,
  `risk` LONGTEXT NOT NULL,
  `status` VARCHAR(255) NOT NULL DEFAULT 'DRAFT',
  `impact` LONGTEXT NOT NULL,
  `outage_expected` BOOLEAN NOT NULL DEFAULT false,
  `downtime_minutes` INT NOT NULL DEFAULT 0,
  `reason` LONGTEXT NOT NULL,
  `business_justification` LONGTEXT NOT NULL,
  `implementation_plan` LONGTEXT NOT NULL,
  `test_plan` LONGTEXT NOT NULL,
  `rollback_plan` LONGTEXT NOT NULL,
  `communication_plan` LONGTEXT NOT NULL DEFAULT (''),
  `client_notification_enabled` BOOLEAN NOT NULL DEFAULT false,
  `owner_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `requested_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `planned_start` DATETIME(6),
  `planned_end` DATETIME(6),
  `actual_start` DATETIME(6),
  `actual_end` DATETIME(6),
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `revision` INT NOT NULL DEFAULT 1,
  `cab_group_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `cab_quorum` INT NOT NULL DEFAULT 0,
  `affected_resources` JSON NOT NULL DEFAULT (JSON_ARRAY()),
  UNIQUE KEY `changes_change_number_unique` (`change_number`),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `client_email_domains` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `domain` VARCHAR(255) NOT NULL,
  `site_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `active` BOOLEAN NOT NULL DEFAULT true,
  `revision` INT NOT NULL DEFAULT 1,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UNIQUE KEY `client_email_domains_domain_key` (`domain`),
  UNIQUE KEY `client_email_domains_id_client_id_key` (`id`, `client_id`),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `client_ticket_sequences` (
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `next_number` BIGINT NOT NULL DEFAULT '1',
  PRIMARY KEY (`client_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `client_users` (
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`client_id`, `user_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `clients` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `name` VARCHAR(255) NOT NULL,
  `status` LONGTEXT NOT NULL DEFAULT ('ACTIVE'),
  `primary_email` LONGTEXT,
  `primary_phone` LONGTEXT,
  `notes` LONGTEXT,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `organization_type` LONGTEXT NOT NULL DEFAULT ('CLIENT'),
  `ticket_prefix` VARCHAR(255) NOT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `clients_ticket_prefix_unique` (`ticket_prefix`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `contacts` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `name` LONGTEXT NOT NULL,
  `email` VARCHAR(255),
  `phone` LONGTEXT,
  `active` BOOLEAN NOT NULL DEFAULT true,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `site_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  UNIQUE KEY `contacts_id_client_uq` (`id`, `client_id`),
  PRIMARY KEY (`id`),
  UNIQUE KEY `contacts_user_id_unique` (`user_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `customer_group_members` (
  `group_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `contact_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  PRIMARY KEY (`group_id`, `contact_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `department_users` (
  `department_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `expertise` LONGTEXT,
  `is_lead` BOOLEAN NOT NULL DEFAULT false,
  PRIMARY KEY (`department_id`, `user_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `departments` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `name` VARCHAR(255) NOT NULL,
  `description` LONGTEXT,
  `active` BOOLEAN NOT NULL DEFAULT true,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UNIQUE KEY `departments_name_unique` (`name`),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `help_requests` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `requested_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `department_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `requested_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `expertise_needed` LONGTEXT NOT NULL,
  `status` LONGTEXT NOT NULL DEFAULT ('PENDING'),
  `responded_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `response_note` LONGTEXT,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `responded_at` DATETIME(6),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `knowledge_articles` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `title` LONGTEXT NOT NULL,
  `body` LONGTEXT NOT NULL,
  `category_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `visibility` LONGTEXT NOT NULL,
  `status` LONGTEXT NOT NULL,
  `author_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `revision` INT NOT NULL DEFAULT 1,
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `mail_attachments` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `message_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `file_name` LONGTEXT NOT NULL,
  `content_type` LONGTEXT NOT NULL,
  `content` LONGBLOB NOT NULL,
  `size_bytes` INT NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `mail_messages` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `direction` VARCHAR(255) NOT NULL,
  `channel` LONGTEXT NOT NULL,
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `change_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `comment_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `communication_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `mailbox` VARCHAR(255) NOT NULL,
  `message_id` VARCHAR(255) NOT NULL,
  `in_reply_to` LONGTEXT,
  `reference_ids` JSON NOT NULL DEFAULT (JSON_ARRAY()),
  `sender_address` LONGTEXT NOT NULL,
  `recipient_addresses` JSON NOT NULL,
  `subject` LONGTEXT NOT NULL,
  `body` LONGTEXT NOT NULL,
  `state` VARCHAR(255) NOT NULL,
  `attempts` INT NOT NULL DEFAULT 0,
  `next_attempt_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `lease_until` DATETIME(6),
  `last_error_code` LONGTEXT,
  `processed_at` DATETIME(6),
  `created_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `contacted_address` LONGTEXT,
  UNIQUE KEY `mail_messages_mailbox_message_id_key` (`mailbox`, `message_id`),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `mail_settings` (
  `id` SMALLINT NOT NULL,
  `enabled` BOOLEAN NOT NULL DEFAULT false,
  `smtp_host` LONGTEXT NOT NULL DEFAULT ('mail.smtp2go.com'),
  `smtp_port` INT NOT NULL DEFAULT 587,
  `tls_mode` LONGTEXT NOT NULL DEFAULT ('STARTTLS'),
  `smtp_username` LONGTEXT NOT NULL DEFAULT (''),
  `password_ciphertext` LONGTEXT,
  `ticket_address` LONGTEXT NOT NULL DEFAULT ('it@amnikontechnologies.com'),
  `change_address` LONGTEXT NOT NULL DEFAULT ('change@amnikontechnologies.com'),
  `inbound_provider` LONGTEXT NOT NULL DEFAULT ('DISABLED'),
  `inbound_since` DATETIME(6),
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `mail_sync_state` (
  `mailbox` VARCHAR(255) NOT NULL,
  `cursor` LONGTEXT,
  `last_sync_at` DATETIME(6),
  `last_error_code` LONGTEXT,
  PRIMARY KEY (`mailbox`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `notification_preferences` (
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `ticket_assigned` BOOLEAN NOT NULL DEFAULT true,
  `ticket_transferred` BOOLEAN NOT NULL DEFAULT true,
  `public_reply` BOOLEAN NOT NULL DEFAULT true,
  `sla_at_risk` BOOLEAN NOT NULL DEFAULT true,
  `sla_breached` BOOLEAN NOT NULL DEFAULT true,
  `ticket_unattended` BOOLEAN NOT NULL DEFAULT true,
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`user_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `notifications` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `type` LONGTEXT NOT NULL,
  `title` LONGTEXT NOT NULL,
  `body` LONGTEXT NOT NULL,
  `event_key` VARCHAR(255) NOT NULL,
  `read_at` DATETIME(6),
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `change_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  UNIQUE KEY `notifications_event_key_unique` (`event_key`),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `permissions` (
  `code` VARCHAR(255) NOT NULL,
  `name` LONGTEXT NOT NULL,
  `description` LONGTEXT NOT NULL,
  PRIMARY KEY (`code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `portal_ticket_submissions` (
  `request_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `domain_rule_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `requester_email` LONGTEXT NOT NULL,
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`request_id`),
  UNIQUE KEY `portal_ticket_submissions_ticket_id_key` (`ticket_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `problem_history` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `problem_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `actor_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `action` LONGTEXT NOT NULL,
  `details` JSON NOT NULL DEFAULT (JSON_OBJECT()),
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `problem_tickets` (
  `problem_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `linked_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`problem_id`, `ticket_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `problems` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `number` BIGINT NOT NULL AUTO_INCREMENT,
  `problem_number` VARCHAR(255),
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `site_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `title` LONGTEXT NOT NULL,
  `description` LONGTEXT NOT NULL,
  `priority` LONGTEXT NOT NULL DEFAULT ('P3_MEDIUM'),
  `status` VARCHAR(255) NOT NULL DEFAULT 'OPEN',
  `root_cause` LONGTEXT NOT NULL DEFAULT (''),
  `workaround` LONGTEXT NOT NULL DEFAULT (''),
  `resolution` LONGTEXT NOT NULL DEFAULT (''),
  `owner_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `created_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `revision` INT NOT NULL DEFAULT 1,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UNIQUE KEY `problems_id_client_id_key` (`id`, `client_id`),
  UNIQUE KEY `problems_number_key` (`number`),
  PRIMARY KEY (`id`),
  UNIQUE KEY `problems_problem_number_key` (`problem_number`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `role_access_policies` (
  `role_code` VARCHAR(255) NOT NULL,
  `permissions` JSON NOT NULL DEFAULT (JSON_ARRAY()),
  `revision` INT NOT NULL DEFAULT 1,
  `updated_by` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`role_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `roles` (
  `code` VARCHAR(255) NOT NULL,
  `name` LONGTEXT NOT NULL,
  `description` LONGTEXT NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `saved_ticket_views` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `name` VARCHAR(255) NOT NULL,
  `query` JSON NOT NULL DEFAULT (JSON_OBJECT()),
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `service_templates` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `name` LONGTEXT NOT NULL,
  `description` LONGTEXT NOT NULL,
  `category_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `active` BOOLEAN NOT NULL DEFAULT true,
  `instructions` LONGTEXT NOT NULL,
  `created_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `site_ticket_sequences` (
  `site_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `next_number` BIGINT NOT NULL DEFAULT 1,
  PRIMARY KEY (`site_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `sites` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `name` VARCHAR(255) NOT NULL,
  `address` LONGTEXT,
  `active` BOOLEAN NOT NULL DEFAULT true,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `ticket_prefix` VARCHAR(255) NOT NULL,
  UNIQUE KEY `sites_client_id_name_unique` (`client_id`, `name`),
  UNIQUE KEY `sites_id_client_id_unique` (`id`, `client_id`),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `sla_calendars` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `name` VARCHAR(255) NOT NULL,
  `timezone` LONGTEXT NOT NULL,
  `working_days` JSON NOT NULL,
  `work_start` TIME(6) NOT NULL,
  `work_end` TIME(6) NOT NULL,
  `active` BOOLEAN NOT NULL DEFAULT true,
  `updated_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  UNIQUE KEY `sla_calendars_name_unique` (`name`),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `sla_holidays` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `calendar_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `holiday_date` DATE NOT NULL,
  `name` LONGTEXT NOT NULL,
  UNIQUE KEY `sla_holidays_calendar_id_holiday_date_unique` (`calendar_id`, `holiday_date`),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `sla_policies` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `priority` VARCHAR(255) NOT NULL,
  `response_minutes` INT NOT NULL,
  `resolution_minutes` INT NOT NULL,
  `warning_percent` INT NOT NULL DEFAULT 75,
  `active` BOOLEAN NOT NULL DEFAULT true,
  `updated_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `calendar_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  PRIMARY KEY (`id`),
  UNIQUE KEY `sla_policies_priority_unique` (`priority`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `staff_group_members` (
  `group_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  PRIMARY KEY (`group_id`, `user_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `tags` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `name` VARCHAR(255) NOT NULL,
  `description` LONGTEXT,
  `active` BOOLEAN NOT NULL DEFAULT true,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `ticket_approvals` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `requested_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `decided_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `status` VARCHAR(255) NOT NULL DEFAULT 'PENDING',
  `reason` LONGTEXT NOT NULL,
  `decision_note` LONGTEXT,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `decided_at` DATETIME(6),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `ticket_assignments` (
  `id` BIGINT NOT NULL AUTO_INCREMENT,
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `previous_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `assigned_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `actor_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `reason` LONGTEXT,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `ticket_attachments` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `uploaded_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `file_name` LONGTEXT NOT NULL,
  `content_type` LONGTEXT NOT NULL,
  `size_bytes` BIGINT NOT NULL,
  `storage_key` VARCHAR(255) NOT NULL,
  `sha256` LONGTEXT NOT NULL,
  `content` LONGBLOB NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `visibility` LONGTEXT NOT NULL DEFAULT ('INTERNAL'),
  PRIMARY KEY (`id`),
  UNIQUE KEY `ticket_attachments_storage_key_unique` (`storage_key`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `ticket_collaborators` (
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `added_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `reason` LONGTEXT NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`ticket_id`, `user_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `ticket_comments` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `author_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `comment_type` LONGTEXT NOT NULL,
  `body` LONGTEXT NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `ticket_history` (
  `id` BIGINT NOT NULL AUTO_INCREMENT,
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `actor_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `action` LONGTEXT NOT NULL,
  `previous_value` JSON,
  `new_value` JSON,
  `reason` LONGTEXT,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `ticket_knowledge_links` (
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `article_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `linked_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`ticket_id`, `article_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `ticket_prefix_registry` (
  `prefix` VARCHAR(255) NOT NULL,
  `owner_kind` LONGTEXT NOT NULL,
  `owner_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  PRIMARY KEY (`prefix`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `ticket_sla` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `policy_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `target_priority` LONGTEXT NOT NULL,
  `response_warning_at` DATETIME(6) NOT NULL,
  `response_due_at` DATETIME(6) NOT NULL,
  `resolution_warning_at` DATETIME(6) NOT NULL,
  `resolution_due_at` DATETIME(6) NOT NULL,
  `response_completed_at` DATETIME(6),
  `resolution_completed_at` DATETIME(6),
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `calendar_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `paused_at` DATETIME(6),
  `response_warning_remaining_minutes` INT,
  `response_remaining_minutes` INT,
  `resolution_warning_remaining_minutes` INT,
  `resolution_remaining_minutes` INT,
  PRIMARY KEY (`id`),
  UNIQUE KEY `ticket_sla_ticket_id_unique` (`ticket_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `ticket_sources` (
  `code` VARCHAR(255) NOT NULL,
  `name` LONGTEXT NOT NULL,
  `active` BOOLEAN NOT NULL DEFAULT true,
  `sort_order` INT NOT NULL DEFAULT 100,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `ticket_tags` (
  `ticket_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `tag_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `added_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`ticket_id`, `tag_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `tickets` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `ticket_number` VARCHAR(255) NOT NULL,
  `client_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `requester_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `site_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `category_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `assigned_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `created_by_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `subject` LONGTEXT NOT NULL,
  `description` LONGTEXT NOT NULL,
  `source` VARCHAR(255) NOT NULL,
  `source_reference_id` LONGTEXT,
  `source_conversation_id` LONGTEXT,
  `source_message_id` LONGTEXT,
  `source_url` LONGTEXT,
  `external_thread_id` LONGTEXT,
  `status` VARCHAR(255) NOT NULL DEFAULT 'NEW',
  `priority` VARCHAR(255) NOT NULL DEFAULT 'P3_MEDIUM',
  `approval_status` LONGTEXT NOT NULL DEFAULT ('NOT_REQUIRED'),
  `first_response_at` DATETIME(6),
  `resolved_at` DATETIME(6),
  `closed_at` DATETIME(6),
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `department_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `location_text` LONGTEXT,
  `email_reply_address` LONGTEXT,
  UNIQUE KEY `tickets_id_client_id_unique` (`id`, `client_id`),
  PRIMARY KEY (`id`),
  UNIQUE KEY `tickets_ticket_number_unique` (`ticket_number`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `user_access_policies` (
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `allow_permissions` JSON NOT NULL DEFAULT (JSON_ARRAY()),
  `deny_permissions` JSON NOT NULL DEFAULT (JSON_ARRAY()),
  `revision` INT NOT NULL DEFAULT 1,
  `updated_by` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`user_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `user_permissions` (
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `permission_code` VARCHAR(255) NOT NULL,
  `granted_by` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`user_id`, `permission_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `user_roles` (
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `role_code` VARCHAR(255) NOT NULL,
  `granted_by` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`user_id`, `role_code`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `user_sites` (
  `user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `site_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  PRIMARY KEY (`user_id`, `site_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE TABLE `users` (
  `id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT (UUID()),
  `entra_object_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `entra_tenant_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `email` VARCHAR(255) NOT NULL,
  `display_name` LONGTEXT NOT NULL,
  `active` BOOLEAN NOT NULL DEFAULT true,
  `last_login_at` DATETIME(6),
  `created_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `updated_at` DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
  `session_version` INT NOT NULL DEFAULT 0,
  `work_scope` LONGTEXT NOT NULL DEFAULT ('CLIENT'),
  `change_access` LONGTEXT NOT NULL DEFAULT ('NONE'),
  `supervisor_user_id` CHAR(36) CHARACTER SET ascii COLLATE ascii_bin,
  `access_expires_at` DATETIME(6),
  UNIQUE KEY `users_entra_tenant_id_entra_object_id_unique` (`entra_tenant_id`, `entra_object_id`),
  PRIMARY KEY (`id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_as_cs;

CREATE UNIQUE INDEX `access_groups_name_uq` ON `access_groups` (`kind`, (COALESCE(`client_id`, '00000000-0000-0000-0000-000000000000')), (lower(TRIM(`name`))));

CREATE INDEX `assets_client_idx` ON `assets` (`client_id`);

CREATE UNIQUE INDEX `assets_serial_ci_uq` ON `assets` ((CASE WHEN (`serial_number` IS NOT NULL) THEN lower(`serial_number`) ELSE NULL END));

CREATE INDEX `audit_actor_idx` ON `audit_logs` (`actor_user_id`, `created_at` DESC);

CREATE INDEX `audit_entity_idx` ON `audit_logs` (`entity_type`, `entity_id`, `created_at` DESC);

CREATE UNIQUE INDEX `categories_child_name_ci_uq` ON `categories` ((CASE WHEN (`parent_id` IS NOT NULL) THEN `parent_id` ELSE NULL END), (CASE WHEN (`parent_id` IS NOT NULL) THEN lower(`name`) ELSE NULL END));

CREATE UNIQUE INDEX `categories_root_name_ci_uq` ON `categories` ((CASE WHEN (`parent_id` IS NULL) THEN lower(`name`) ELSE NULL END));

CREATE UNIQUE INDEX `change_cab_vote_unique` ON `change_approvals` ((CASE WHEN (`cab_group_id` IS NOT NULL) THEN `change_id` ELSE NULL END), (CASE WHEN (`cab_group_id` IS NOT NULL) THEN `revision` ELSE NULL END), (CASE WHEN (`cab_group_id` IS NOT NULL) THEN `approver_user_id` ELSE NULL END));

CREATE UNIQUE INDEX `change_stakeholders_contact_unique` ON `change_stakeholders` ((CASE WHEN (`contact_id` IS NOT NULL) THEN `change_id` ELSE NULL END), (CASE WHEN (`contact_id` IS NOT NULL) THEN `contact_id` ELSE NULL END));

CREATE UNIQUE INDEX `change_stakeholders_email_unique` ON `change_stakeholders` ((CASE WHEN (`external_email` IS NOT NULL) THEN `change_id` ELSE NULL END), (CASE WHEN (`external_email` IS NOT NULL) THEN lower(`external_email`) ELSE NULL END));

CREATE UNIQUE INDEX `change_stakeholders_user_unique` ON `change_stakeholders` ((CASE WHEN (`user_id` IS NOT NULL) THEN `change_id` ELSE NULL END), (CASE WHEN (`user_id` IS NOT NULL) THEN `user_id` ELSE NULL END));

CREATE INDEX `change_window_idx` ON `changes` (`client_id`, `planned_start`, `planned_end`);

CREATE INDEX `changes_scope_idx` ON `changes` (`client_id`, `status`, `planned_start`);

CREATE INDEX `client_email_domains_client_idx` ON `client_email_domains` (`client_id`);

CREATE UNIQUE INDEX `clients_name_ci_uq` ON `clients` ((lower(`name`)));

CREATE INDEX `contacts_client_idx` ON `contacts` (`client_id`);

CREATE INDEX `contacts_email_ci_idx` ON `contacts` ((lower(`email`)));

CREATE UNIQUE INDEX `contacts_group_client_uq` ON `contacts` (`id`, `client_id`);

CREATE INDEX `contacts_portal_email_idx` ON `contacts` (`client_id`, (lower(`email`)));

CREATE INDEX `contacts_site_idx` ON `contacts` (`site_id`);

CREATE INDEX `customer_group_members_contact_idx` ON `customer_group_members` (`contact_id`);

CREATE INDEX `mail_change_idx` ON `mail_messages` (`change_id`, `created_at`);

CREATE UNIQUE INDEX `mail_comment_once` ON `mail_messages` ((CASE WHEN ((`direction` = 'OUTBOUND') AND (`comment_id` IS NOT NULL)) THEN `comment_id` ELSE NULL END));

CREATE UNIQUE INDEX `mail_communication_once` ON `mail_messages` ((CASE WHEN ((`direction` = 'OUTBOUND') AND (`communication_id` IS NOT NULL)) THEN `communication_id` ELSE NULL END));

CREATE INDEX `mail_outbox_idx` ON `mail_messages` (`state`, `next_attempt_at`);

CREATE INDEX `mail_ticket_idx` ON `mail_messages` (`ticket_id`, `created_at`);

CREATE INDEX `notifications_user_unread_idx` ON `notifications` (`user_id`, `read_at`, `created_at` DESC);

CREATE INDEX `problem_history_timeline` ON `problem_history` (`problem_id`, `created_at`, `id`);

CREATE INDEX `problem_tickets_ticket` ON `problem_tickets` (`ticket_id`);

CREATE INDEX `problems_client_status_updated` ON `problems` (`client_id`, `status`, `updated_at` DESC, `id`);

CREATE INDEX `problems_owner_updated` ON `problems` (`owner_user_id`, `updated_at` DESC, `id`);

CREATE UNIQUE INDEX `saved_ticket_views_user_name_uq` ON `saved_ticket_views` (`user_id`, (lower(`name`)));

CREATE UNIQUE INDEX `sites_id_client_portal_uq` ON `sites` (`id`, `client_id`);

CREATE UNIQUE INDEX `sites_ticket_prefix_uq` ON `sites` (`ticket_prefix`);

CREATE INDEX `staff_group_members_user_idx` ON `staff_group_members` (`user_id`);

CREATE UNIQUE INDEX `tags_name_ci_uq` ON `tags` ((lower(`name`)));

CREATE UNIQUE INDEX `ticket_approvals_pending_uq` ON `ticket_approvals` ((CASE WHEN (`status` = 'PENDING') THEN `ticket_id` ELSE NULL END));

CREATE INDEX `ticket_assignments_ticket_idx` ON `ticket_assignments` (`ticket_id`, `created_at`);

CREATE INDEX `ticket_attachments_ticket_idx` ON `ticket_attachments` (`ticket_id`, `created_at`);

CREATE INDEX `ticket_comments_ticket_idx` ON `ticket_comments` (`ticket_id`, `created_at`);

CREATE INDEX `ticket_history_ticket_idx` ON `ticket_history` (`ticket_id`, `created_at`, `id`);

CREATE INDEX `ticket_sla_due_idx` ON `ticket_sla` (`response_due_at`, `resolution_due_at`);

CREATE INDEX `tickets_assignee_status_idx` ON `tickets` (`assigned_user_id`, `status`, `updated_at` DESC);

CREATE INDEX `tickets_client_created_idx` ON `tickets` (`client_id`, `created_at` DESC);

CREATE INDEX `tickets_status_priority_idx` ON `tickets` (`status`, `priority`, `updated_at` DESC);

CREATE UNIQUE INDEX `users_email_ci_uq` ON `users` ((lower(`email`)));

ALTER TABLE `access_groups` ADD CONSTRAINT `access_groups_client_id_fkey` FOREIGN KEY (`client_id`) REFERENCES `clients` (`id`) ON DELETE RESTRICT;

ALTER TABLE `assets` ADD CONSTRAINT `assets_client_id_foreign` FOREIGN KEY (`client_id`) REFERENCES `clients` (`id`) ON DELETE RESTRICT;

ALTER TABLE `assets` ADD CONSTRAINT `assets_site_client_fk` FOREIGN KEY (`site_id`, `client_id`) REFERENCES `sites` (`id`, `client_id`) ON DELETE RESTRICT;

ALTER TABLE `assets` ADD CONSTRAINT `assets_site_id_foreign` FOREIGN KEY (`site_id`) REFERENCES `sites` (`id`) ON DELETE SET NULL;

ALTER TABLE `audit_logs` ADD CONSTRAINT `audit_logs_actor_user_id_foreign` FOREIGN KEY (`actor_user_id`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `cab_members` ADD CONSTRAINT `cab_members_group_id_fkey` FOREIGN KEY (`group_id`) REFERENCES `cab_groups` (`id`) ON DELETE CASCADE;

ALTER TABLE `cab_members` ADD CONSTRAINT `cab_members_user_id_fkey` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`) ON DELETE RESTRICT;

ALTER TABLE `categories` ADD CONSTRAINT `categories_parent_id_foreign` FOREIGN KEY (`parent_id`) REFERENCES `categories` (`id`) ON DELETE RESTRICT;

ALTER TABLE `change_approvals` ADD CONSTRAINT `change_approvals_approver_user_id_foreign` FOREIGN KEY (`approver_user_id`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `change_approvals` ADD CONSTRAINT `change_approvals_cab_group_id_fkey` FOREIGN KEY (`cab_group_id`) REFERENCES `cab_groups` (`id`) ON DELETE RESTRICT;

ALTER TABLE `change_approvals` ADD CONSTRAINT `change_approvals_change_id_foreign` FOREIGN KEY (`change_id`) REFERENCES `changes` (`id`) ON DELETE CASCADE;

ALTER TABLE `change_approvals` ADD CONSTRAINT `change_approvals_decision_by_user_id_foreign` FOREIGN KEY (`decision_by_user_id`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `change_approvals` ADD CONSTRAINT `change_approvals_department_id_foreign` FOREIGN KEY (`department_id`) REFERENCES `departments` (`id`) ON DELETE SET NULL;

ALTER TABLE `change_attachments` ADD CONSTRAINT `change_attachments_change_id_foreign` FOREIGN KEY (`change_id`) REFERENCES `changes` (`id`) ON DELETE CASCADE;

ALTER TABLE `change_attachments` ADD CONSTRAINT `change_attachments_uploaded_by_user_id_foreign` FOREIGN KEY (`uploaded_by_user_id`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `change_blackouts` ADD CONSTRAINT `change_blackouts_client_id_fkey` FOREIGN KEY (`client_id`) REFERENCES `clients` (`id`) ON DELETE RESTRICT;

ALTER TABLE `change_blackouts` ADD CONSTRAINT `change_blackouts_created_by_user_id_fkey` FOREIGN KEY (`created_by_user_id`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `change_blackouts` ADD CONSTRAINT `change_blackouts_site_id_client_id_fkey` FOREIGN KEY (`site_id`, `client_id`) REFERENCES `sites` (`id`, `client_id`);

ALTER TABLE `change_blackouts` ADD CONSTRAINT `change_blackouts_site_id_fkey` FOREIGN KEY (`site_id`) REFERENCES `sites` (`id`) ON DELETE RESTRICT;

ALTER TABLE `change_communications` ADD CONSTRAINT `change_communications_change_id_foreign` FOREIGN KEY (`change_id`) REFERENCES `changes` (`id`) ON DELETE CASCADE;

ALTER TABLE `change_communications` ADD CONSTRAINT `change_communications_created_by_user_id_foreign` FOREIGN KEY (`created_by_user_id`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `change_history` ADD CONSTRAINT `change_history_actor_user_id_foreign` FOREIGN KEY (`actor_user_id`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `change_history` ADD CONSTRAINT `change_history_change_id_foreign` FOREIGN KEY (`change_id`) REFERENCES `changes` (`id`) ON DELETE CASCADE;

ALTER TABLE `change_reviews` ADD CONSTRAINT `change_reviews_actor_user_id_fkey` FOREIGN KEY (`actor_user_id`) REFERENCES `users` (`id`) ON DELETE RESTRICT;

ALTER TABLE `change_reviews` ADD CONSTRAINT `change_reviews_change_id_fkey` FOREIGN KEY (`change_id`) REFERENCES `changes` (`id`) ON DELETE RESTRICT;

ALTER TABLE `change_stakeholders` ADD CONSTRAINT `change_stakeholders_added_by_user_id_foreign` FOREIGN KEY (`added_by_user_id`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `change_stakeholders` ADD CONSTRAINT `change_stakeholders_change_id_foreign` FOREIGN KEY (`change_id`) REFERENCES `changes` (`id`) ON DELETE CASCADE;

ALTER TABLE `change_stakeholders` ADD CONSTRAINT `change_stakeholders_contact_id_foreign` FOREIGN KEY (`contact_id`) REFERENCES `contacts` (`id`) ON DELETE CASCADE;

ALTER TABLE `change_stakeholders` ADD CONSTRAINT `change_stakeholders_user_id_foreign` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`) ON DELETE CASCADE;

ALTER TABLE `change_tasks` ADD CONSTRAINT `change_tasks_assigned_user_id_foreign` FOREIGN KEY (`assigned_user_id`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `change_tasks` ADD CONSTRAINT `change_tasks_change_id_foreign` FOREIGN KEY (`change_id`) REFERENCES `changes` (`id`) ON DELETE CASCADE;

ALTER TABLE `change_tasks` ADD CONSTRAINT `change_tasks_department_id_foreign` FOREIGN KEY (`department_id`) REFERENCES `departments` (`id`) ON DELETE SET NULL;

ALTER TABLE `change_teams` ADD CONSTRAINT `change_teams_change_id_foreign` FOREIGN KEY (`change_id`) REFERENCES `changes` (`id`) ON DELETE CASCADE;

ALTER TABLE `change_teams` ADD CONSTRAINT `change_teams_department_id_foreign` FOREIGN KEY (`department_id`) REFERENCES `departments` (`id`) ON DELETE RESTRICT;

ALTER TABLE `change_templates` ADD CONSTRAINT `change_templates_created_by_user_id_fkey` FOREIGN KEY (`created_by_user_id`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `change_ticket_links` ADD CONSTRAINT `change_ticket_links_change_id_foreign` FOREIGN KEY (`change_id`) REFERENCES `changes` (`id`) ON DELETE CASCADE;

ALTER TABLE `change_ticket_links` ADD CONSTRAINT `change_ticket_links_linked_by_user_id_foreign` FOREIGN KEY (`linked_by_user_id`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `change_ticket_links` ADD CONSTRAINT `change_ticket_links_ticket_id_foreign` FOREIGN KEY (`ticket_id`) REFERENCES `tickets` (`id`) ON DELETE CASCADE;

ALTER TABLE `change_updates` ADD CONSTRAINT `change_updates_author_user_id_foreign` FOREIGN KEY (`author_user_id`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `change_updates` ADD CONSTRAINT `change_updates_change_id_foreign` FOREIGN KEY (`change_id`) REFERENCES `changes` (`id`) ON DELETE CASCADE;

ALTER TABLE `changes` ADD CONSTRAINT `changes_cab_group_id_fkey` FOREIGN KEY (`cab_group_id`) REFERENCES `cab_groups` (`id`) ON DELETE RESTRICT;

ALTER TABLE `changes` ADD CONSTRAINT `changes_client_id_foreign` FOREIGN KEY (`client_id`) REFERENCES `clients` (`id`) ON DELETE RESTRICT;

ALTER TABLE `changes` ADD CONSTRAINT `changes_department_id_foreign` FOREIGN KEY (`department_id`) REFERENCES `departments` (`id`) ON DELETE SET NULL;

ALTER TABLE `changes` ADD CONSTRAINT `changes_owner_user_id_foreign` FOREIGN KEY (`owner_user_id`) REFERENCES `users` (`id`) ON DELETE RESTRICT;

ALTER TABLE `changes` ADD CONSTRAINT `changes_requested_by_user_id_foreign` FOREIGN KEY (`requested_by_user_id`) REFERENCES `users` (`id`) ON DELETE RESTRICT;

ALTER TABLE `changes` ADD CONSTRAINT `changes_site_id_foreign` FOREIGN KEY (`site_id`) REFERENCES `sites` (`id`) ON DELETE SET NULL;

ALTER TABLE `client_email_domains` ADD CONSTRAINT `client_email_domains_client_id_fkey` FOREIGN KEY (`client_id`) REFERENCES `clients` (`id`) ON DELETE RESTRICT;

ALTER TABLE `client_email_domains` ADD CONSTRAINT `client_email_domains_site_id_client_id_fkey` FOREIGN KEY (`site_id`, `client_id`) REFERENCES `sites` (`id`, `client_id`) ON DELETE RESTRICT;

ALTER TABLE `client_ticket_sequences` ADD CONSTRAINT `client_ticket_sequences_client_id_foreign` FOREIGN KEY (`client_id`) REFERENCES `clients` (`id`) ON DELETE CASCADE;

ALTER TABLE `client_users` ADD CONSTRAINT `client_users_client_id_foreign` FOREIGN KEY (`client_id`) REFERENCES `clients` (`id`) ON DELETE CASCADE;

ALTER TABLE `client_users` ADD CONSTRAINT `client_users_user_id_foreign` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`) ON DELETE CASCADE;

ALTER TABLE `contacts` ADD CONSTRAINT `contacts_client_id_foreign` FOREIGN KEY (`client_id`) REFERENCES `clients` (`id`) ON DELETE RESTRICT;

ALTER TABLE `contacts` ADD CONSTRAINT `contacts_site_client_fk` FOREIGN KEY (`site_id`, `client_id`) REFERENCES `sites` (`id`, `client_id`) ON DELETE RESTRICT;

ALTER TABLE `contacts` ADD CONSTRAINT `contacts_site_id_foreign` FOREIGN KEY (`site_id`) REFERENCES `sites` (`id`) ON DELETE SET NULL;

ALTER TABLE `contacts` ADD CONSTRAINT `contacts_user_id_foreign` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `customer_group_members` ADD CONSTRAINT `customer_group_members_contact_id_client_id_fkey` FOREIGN KEY (`contact_id`, `client_id`) REFERENCES `contacts` (`id`, `client_id`) ON DELETE RESTRICT;

ALTER TABLE `customer_group_members` ADD CONSTRAINT `customer_group_members_group_id_client_id_fkey` FOREIGN KEY (`group_id`, `client_id`) REFERENCES `access_groups` (`id`, `client_id`) ON DELETE CASCADE;

ALTER TABLE `department_users` ADD CONSTRAINT `department_users_department_id_foreign` FOREIGN KEY (`department_id`) REFERENCES `departments` (`id`) ON DELETE CASCADE;

ALTER TABLE `department_users` ADD CONSTRAINT `department_users_user_id_foreign` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`) ON DELETE CASCADE;

ALTER TABLE `help_requests` ADD CONSTRAINT `help_requests_department_id_foreign` FOREIGN KEY (`department_id`) REFERENCES `departments` (`id`) ON DELETE SET NULL;

ALTER TABLE `help_requests` ADD CONSTRAINT `help_requests_requested_by_user_id_foreign` FOREIGN KEY (`requested_by_user_id`) REFERENCES `users` (`id`) ON DELETE RESTRICT;

ALTER TABLE `help_requests` ADD CONSTRAINT `help_requests_requested_user_id_foreign` FOREIGN KEY (`requested_user_id`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `help_requests` ADD CONSTRAINT `help_requests_responded_by_user_id_foreign` FOREIGN KEY (`responded_by_user_id`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `help_requests` ADD CONSTRAINT `help_requests_ticket_id_foreign` FOREIGN KEY (`ticket_id`) REFERENCES `tickets` (`id`) ON DELETE CASCADE;

ALTER TABLE `knowledge_articles` ADD CONSTRAINT `knowledge_articles_author_user_id_fkey` FOREIGN KEY (`author_user_id`) REFERENCES `users` (`id`) ON DELETE RESTRICT;

ALTER TABLE `knowledge_articles` ADD CONSTRAINT `knowledge_articles_category_id_fkey` FOREIGN KEY (`category_id`) REFERENCES `categories` (`id`) ON DELETE SET NULL;

ALTER TABLE `knowledge_articles` ADD CONSTRAINT `knowledge_articles_client_id_fkey` FOREIGN KEY (`client_id`) REFERENCES `clients` (`id`) ON DELETE RESTRICT;

ALTER TABLE `mail_attachments` ADD CONSTRAINT `mail_attachments_message_id_fkey` FOREIGN KEY (`message_id`) REFERENCES `mail_messages` (`id`) ON DELETE RESTRICT;

ALTER TABLE `mail_messages` ADD CONSTRAINT `mail_messages_change_id_fkey` FOREIGN KEY (`change_id`) REFERENCES `changes` (`id`) ON DELETE RESTRICT;

ALTER TABLE `mail_messages` ADD CONSTRAINT `mail_messages_comment_id_fkey` FOREIGN KEY (`comment_id`) REFERENCES `ticket_comments` (`id`) ON DELETE RESTRICT;

ALTER TABLE `mail_messages` ADD CONSTRAINT `mail_messages_communication_id_fkey` FOREIGN KEY (`communication_id`) REFERENCES `change_communications` (`id`) ON DELETE RESTRICT;

ALTER TABLE `mail_messages` ADD CONSTRAINT `mail_messages_created_by_user_id_fkey` FOREIGN KEY (`created_by_user_id`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `mail_messages` ADD CONSTRAINT `mail_messages_ticket_id_fkey` FOREIGN KEY (`ticket_id`) REFERENCES `tickets` (`id`) ON DELETE RESTRICT;

ALTER TABLE `notification_preferences` ADD CONSTRAINT `notification_preferences_user_id_foreign` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`) ON DELETE CASCADE;

ALTER TABLE `notifications` ADD CONSTRAINT `notifications_change_id_foreign` FOREIGN KEY (`change_id`) REFERENCES `changes` (`id`) ON DELETE CASCADE;

ALTER TABLE `notifications` ADD CONSTRAINT `notifications_ticket_id_foreign` FOREIGN KEY (`ticket_id`) REFERENCES `tickets` (`id`) ON DELETE CASCADE;

ALTER TABLE `notifications` ADD CONSTRAINT `notifications_user_id_foreign` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`) ON DELETE CASCADE;

ALTER TABLE `portal_ticket_submissions` ADD CONSTRAINT `portal_ticket_submissions_domain_rule_id_client_id_fkey` FOREIGN KEY (`domain_rule_id`, `client_id`) REFERENCES `client_email_domains` (`id`, `client_id`);

ALTER TABLE `portal_ticket_submissions` ADD CONSTRAINT `portal_ticket_submissions_ticket_id_client_id_fkey` FOREIGN KEY (`ticket_id`, `client_id`) REFERENCES `tickets` (`id`, `client_id`);

ALTER TABLE `problem_history` ADD CONSTRAINT `problem_history_actor_user_id_fkey` FOREIGN KEY (`actor_user_id`) REFERENCES `users` (`id`);

ALTER TABLE `problem_history` ADD CONSTRAINT `problem_history_problem_id_fkey` FOREIGN KEY (`problem_id`) REFERENCES `problems` (`id`);

ALTER TABLE `problem_tickets` ADD CONSTRAINT `problem_tickets_linked_by_user_id_fkey` FOREIGN KEY (`linked_by_user_id`) REFERENCES `users` (`id`);

ALTER TABLE `problem_tickets` ADD CONSTRAINT `problem_tickets_problem_id_client_id_fkey` FOREIGN KEY (`problem_id`, `client_id`) REFERENCES `problems` (`id`, `client_id`) ON DELETE RESTRICT;

ALTER TABLE `problem_tickets` ADD CONSTRAINT `problem_tickets_ticket_id_client_id_fkey` FOREIGN KEY (`ticket_id`, `client_id`) REFERENCES `tickets` (`id`, `client_id`) ON DELETE RESTRICT;

ALTER TABLE `problems` ADD CONSTRAINT `problems_client_id_fkey` FOREIGN KEY (`client_id`) REFERENCES `clients` (`id`) ON DELETE RESTRICT;

ALTER TABLE `problems` ADD CONSTRAINT `problems_created_by_user_id_fkey` FOREIGN KEY (`created_by_user_id`) REFERENCES `users` (`id`) ON DELETE RESTRICT;

ALTER TABLE `problems` ADD CONSTRAINT `problems_owner_user_id_fkey` FOREIGN KEY (`owner_user_id`) REFERENCES `users` (`id`) ON DELETE RESTRICT;

ALTER TABLE `problems` ADD CONSTRAINT `problems_site_id_fkey` FOREIGN KEY (`site_id`) REFERENCES `sites` (`id`) ON DELETE RESTRICT;

ALTER TABLE `role_access_policies` ADD CONSTRAINT `role_access_policies_updated_by_fkey` FOREIGN KEY (`updated_by`) REFERENCES `users` (`id`);

ALTER TABLE `saved_ticket_views` ADD CONSTRAINT `saved_ticket_views_user_id_foreign` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`) ON DELETE CASCADE;

ALTER TABLE `service_templates` ADD CONSTRAINT `service_templates_category_id_fkey` FOREIGN KEY (`category_id`) REFERENCES `categories` (`id`) ON DELETE RESTRICT;

ALTER TABLE `service_templates` ADD CONSTRAINT `service_templates_client_id_fkey` FOREIGN KEY (`client_id`) REFERENCES `clients` (`id`) ON DELETE RESTRICT;

ALTER TABLE `service_templates` ADD CONSTRAINT `service_templates_created_by_user_id_fkey` FOREIGN KEY (`created_by_user_id`) REFERENCES `users` (`id`) ON DELETE RESTRICT;

ALTER TABLE `site_ticket_sequences` ADD CONSTRAINT `site_ticket_sequences_site_id_fkey` FOREIGN KEY (`site_id`) REFERENCES `sites` (`id`) ON DELETE RESTRICT;

ALTER TABLE `sites` ADD CONSTRAINT `sites_client_id_foreign` FOREIGN KEY (`client_id`) REFERENCES `clients` (`id`) ON DELETE RESTRICT;

ALTER TABLE `sla_calendars` ADD CONSTRAINT `sla_calendars_updated_by_user_id_foreign` FOREIGN KEY (`updated_by_user_id`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `sla_holidays` ADD CONSTRAINT `sla_holidays_calendar_id_foreign` FOREIGN KEY (`calendar_id`) REFERENCES `sla_calendars` (`id`) ON DELETE CASCADE;

ALTER TABLE `sla_policies` ADD CONSTRAINT `sla_policies_calendar_id_foreign` FOREIGN KEY (`calendar_id`) REFERENCES `sla_calendars` (`id`) ON DELETE RESTRICT;

ALTER TABLE `sla_policies` ADD CONSTRAINT `sla_policies_updated_by_user_id_foreign` FOREIGN KEY (`updated_by_user_id`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `staff_group_members` ADD CONSTRAINT `staff_group_members_group_id_fkey` FOREIGN KEY (`group_id`) REFERENCES `access_groups` (`id`) ON DELETE CASCADE;

ALTER TABLE `staff_group_members` ADD CONSTRAINT `staff_group_members_user_id_fkey` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`) ON DELETE CASCADE;

ALTER TABLE `ticket_approvals` ADD CONSTRAINT `ticket_approvals_decided_by_user_id_foreign` FOREIGN KEY (`decided_by_user_id`) REFERENCES `users` (`id`) ON DELETE RESTRICT;

ALTER TABLE `ticket_approvals` ADD CONSTRAINT `ticket_approvals_requested_by_user_id_foreign` FOREIGN KEY (`requested_by_user_id`) REFERENCES `users` (`id`) ON DELETE RESTRICT;

ALTER TABLE `ticket_approvals` ADD CONSTRAINT `ticket_approvals_ticket_id_foreign` FOREIGN KEY (`ticket_id`) REFERENCES `tickets` (`id`) ON DELETE CASCADE;

ALTER TABLE `ticket_assignments` ADD CONSTRAINT `ticket_assignments_actor_user_id_foreign` FOREIGN KEY (`actor_user_id`) REFERENCES `users` (`id`) ON DELETE RESTRICT;

ALTER TABLE `ticket_assignments` ADD CONSTRAINT `ticket_assignments_assigned_user_id_foreign` FOREIGN KEY (`assigned_user_id`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `ticket_assignments` ADD CONSTRAINT `ticket_assignments_previous_user_id_foreign` FOREIGN KEY (`previous_user_id`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `ticket_assignments` ADD CONSTRAINT `ticket_assignments_ticket_id_foreign` FOREIGN KEY (`ticket_id`) REFERENCES `tickets` (`id`) ON DELETE CASCADE;

ALTER TABLE `ticket_attachments` ADD CONSTRAINT `ticket_attachments_ticket_id_foreign` FOREIGN KEY (`ticket_id`) REFERENCES `tickets` (`id`) ON DELETE CASCADE;

ALTER TABLE `ticket_attachments` ADD CONSTRAINT `ticket_attachments_uploaded_by_user_id_foreign` FOREIGN KEY (`uploaded_by_user_id`) REFERENCES `users` (`id`) ON DELETE RESTRICT;

ALTER TABLE `ticket_collaborators` ADD CONSTRAINT `ticket_collaborators_added_by_user_id_foreign` FOREIGN KEY (`added_by_user_id`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `ticket_collaborators` ADD CONSTRAINT `ticket_collaborators_ticket_id_foreign` FOREIGN KEY (`ticket_id`) REFERENCES `tickets` (`id`) ON DELETE CASCADE;

ALTER TABLE `ticket_collaborators` ADD CONSTRAINT `ticket_collaborators_user_id_foreign` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`) ON DELETE CASCADE;

ALTER TABLE `ticket_comments` ADD CONSTRAINT `ticket_comments_author_user_id_foreign` FOREIGN KEY (`author_user_id`) REFERENCES `users` (`id`) ON DELETE RESTRICT;

ALTER TABLE `ticket_comments` ADD CONSTRAINT `ticket_comments_ticket_id_foreign` FOREIGN KEY (`ticket_id`) REFERENCES `tickets` (`id`) ON DELETE CASCADE;

ALTER TABLE `ticket_history` ADD CONSTRAINT `ticket_history_actor_user_id_foreign` FOREIGN KEY (`actor_user_id`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `ticket_history` ADD CONSTRAINT `ticket_history_ticket_id_foreign` FOREIGN KEY (`ticket_id`) REFERENCES `tickets` (`id`) ON DELETE CASCADE;

ALTER TABLE `ticket_knowledge_links` ADD CONSTRAINT `ticket_knowledge_links_article_id_fkey` FOREIGN KEY (`article_id`) REFERENCES `knowledge_articles` (`id`) ON DELETE RESTRICT;

ALTER TABLE `ticket_knowledge_links` ADD CONSTRAINT `ticket_knowledge_links_linked_by_user_id_fkey` FOREIGN KEY (`linked_by_user_id`) REFERENCES `users` (`id`) ON DELETE RESTRICT;

ALTER TABLE `ticket_knowledge_links` ADD CONSTRAINT `ticket_knowledge_links_ticket_id_fkey` FOREIGN KEY (`ticket_id`) REFERENCES `tickets` (`id`) ON DELETE RESTRICT;

ALTER TABLE `ticket_sla` ADD CONSTRAINT `ticket_sla_calendar_id_foreign` FOREIGN KEY (`calendar_id`) REFERENCES `sla_calendars` (`id`) ON DELETE RESTRICT;

ALTER TABLE `ticket_sla` ADD CONSTRAINT `ticket_sla_policy_id_foreign` FOREIGN KEY (`policy_id`) REFERENCES `sla_policies` (`id`) ON DELETE RESTRICT;

ALTER TABLE `ticket_sla` ADD CONSTRAINT `ticket_sla_ticket_id_foreign` FOREIGN KEY (`ticket_id`) REFERENCES `tickets` (`id`) ON DELETE CASCADE;

ALTER TABLE `ticket_tags` ADD CONSTRAINT `ticket_tags_added_by_user_id_foreign` FOREIGN KEY (`added_by_user_id`) REFERENCES `users` (`id`) ON DELETE RESTRICT;

ALTER TABLE `ticket_tags` ADD CONSTRAINT `ticket_tags_tag_id_foreign` FOREIGN KEY (`tag_id`) REFERENCES `tags` (`id`) ON DELETE RESTRICT;

ALTER TABLE `ticket_tags` ADD CONSTRAINT `ticket_tags_ticket_id_foreign` FOREIGN KEY (`ticket_id`) REFERENCES `tickets` (`id`) ON DELETE CASCADE;

ALTER TABLE `tickets` ADD CONSTRAINT `tickets_assigned_user_id_foreign` FOREIGN KEY (`assigned_user_id`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `tickets` ADD CONSTRAINT `tickets_category_id_foreign` FOREIGN KEY (`category_id`) REFERENCES `categories` (`id`) ON DELETE RESTRICT;

ALTER TABLE `tickets` ADD CONSTRAINT `tickets_client_id_foreign` FOREIGN KEY (`client_id`) REFERENCES `clients` (`id`) ON DELETE RESTRICT;

ALTER TABLE `tickets` ADD CONSTRAINT `tickets_created_by_user_id_foreign` FOREIGN KEY (`created_by_user_id`) REFERENCES `users` (`id`) ON DELETE RESTRICT;

ALTER TABLE `tickets` ADD CONSTRAINT `tickets_department_id_foreign` FOREIGN KEY (`department_id`) REFERENCES `departments` (`id`) ON DELETE SET NULL;

ALTER TABLE `tickets` ADD CONSTRAINT `tickets_requester_client_fk` FOREIGN KEY (`requester_id`, `client_id`) REFERENCES `contacts` (`id`, `client_id`) ON DELETE RESTRICT;

ALTER TABLE `tickets` ADD CONSTRAINT `tickets_requester_id_foreign` FOREIGN KEY (`requester_id`) REFERENCES `contacts` (`id`) ON DELETE RESTRICT;

ALTER TABLE `tickets` ADD CONSTRAINT `tickets_site_client_fk` FOREIGN KEY (`site_id`, `client_id`) REFERENCES `sites` (`id`, `client_id`) ON DELETE RESTRICT;

ALTER TABLE `tickets` ADD CONSTRAINT `tickets_site_id_foreign` FOREIGN KEY (`site_id`) REFERENCES `sites` (`id`) ON DELETE SET NULL;

ALTER TABLE `tickets` ADD CONSTRAINT `tickets_source_fk` FOREIGN KEY (`source`) REFERENCES `ticket_sources` (`code`) ON UPDATE CASCADE ON DELETE RESTRICT;

ALTER TABLE `user_access_policies` ADD CONSTRAINT `user_access_policies_updated_by_fkey` FOREIGN KEY (`updated_by`) REFERENCES `users` (`id`);

ALTER TABLE `user_access_policies` ADD CONSTRAINT `user_access_policies_user_id_fkey` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`) ON DELETE CASCADE;

ALTER TABLE `user_permissions` ADD CONSTRAINT `user_permissions_granted_by_foreign` FOREIGN KEY (`granted_by`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `user_permissions` ADD CONSTRAINT `user_permissions_permission_code_foreign` FOREIGN KEY (`permission_code`) REFERENCES `permissions` (`code`) ON DELETE CASCADE;

ALTER TABLE `user_permissions` ADD CONSTRAINT `user_permissions_user_id_foreign` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`) ON DELETE CASCADE;

ALTER TABLE `user_roles` ADD CONSTRAINT `user_roles_granted_by_foreign` FOREIGN KEY (`granted_by`) REFERENCES `users` (`id`) ON DELETE SET NULL;

ALTER TABLE `user_roles` ADD CONSTRAINT `user_roles_role_code_foreign` FOREIGN KEY (`role_code`) REFERENCES `roles` (`code`);

ALTER TABLE `user_roles` ADD CONSTRAINT `user_roles_user_id_foreign` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`) ON DELETE CASCADE;

ALTER TABLE `user_sites` ADD CONSTRAINT `user_sites_site_id_foreign` FOREIGN KEY (`site_id`) REFERENCES `sites` (`id`) ON DELETE CASCADE;

ALTER TABLE `user_sites` ADD CONSTRAINT `user_sites_user_id_foreign` FOREIGN KEY (`user_id`) REFERENCES `users` (`id`) ON DELETE CASCADE;

ALTER TABLE `users` ADD CONSTRAINT `users_supervisor_user_id_foreign` FOREIGN KEY (`supervisor_user_id`) REFERENCES `users` (`id`) ON DELETE SET NULL;

CREATE FULLTEXT INDEX `astra_clients_search` ON `clients` (`name`);

CREATE FULLTEXT INDEX `astra_contacts_search` ON `contacts` (`name`);

CREATE FULLTEXT INDEX `astra_knowledge_articles_search` ON `knowledge_articles` (`title`, `body`);

CREATE FULLTEXT INDEX `astra_ticket_comments_search` ON `ticket_comments` (`body`);

CREATE FULLTEXT INDEX `astra_tickets_search` ON `tickets` (`ticket_number`, `subject`, `description`, `source`);

DELIMITER $$

CREATE TRIGGER `astra_access_groups_insert_checks` BEFORE INSERT ON `access_groups` FOR EACH ROW BEGIN
  IF NOT (JSON_TYPE(NEW.`permissions`) = 'ARRAY') THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: access_groups_permissions_json_array'; END IF;
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: access_groups_active_boolean'; END IF;
  IF NOT (((((NEW.`kind` = 'CUSTOMER') AND (NEW.`client_id` IS NOT NULL) AND (JSON_CONTAINS(JSON_ARRAY('PORTAL_ORGANIZATION_VIEW', 'PORTAL_ORGANIZATION_REPLY'), NEW.`permissions`))) OR ((NEW.`kind` = 'STAFF') AND (NEW.`client_id` IS NULL) AND (JSON_CONTAINS(JSON_ARRAY('TICKETS_TEAM_VIEW', 'TICKETS_ASSIGN', 'TICKETS_APPROVE', 'CATEGORY_MANAGE', 'SLA_MANAGE', 'REPORTS_VIEW', 'TAGS_MANAGE'), NEW.`permissions`)))))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: access_groups_check'; END IF;
  IF NOT (((CHAR_LENGTH(NEW.`description`) <= 500))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: access_groups_description_check'; END IF;
  IF NOT (((NEW.`kind` IN ('CUSTOMER', 'STAFF')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: access_groups_kind_check'; END IF;
  IF NOT ((((CHAR_LENGTH(TRIM(NEW.`name`)) >= 2) AND (CHAR_LENGTH(TRIM(NEW.`name`)) <= 120)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: access_groups_name_check'; END IF;
  IF NOT ((((NOT (JSON_CONTAINS(NEW.`permissions`, JSON_QUOTE('PORTAL_ORGANIZATION_REPLY')))) OR (JSON_CONTAINS(NEW.`permissions`, JSON_QUOTE('PORTAL_ORGANIZATION_VIEW')))))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: access_groups_permissions_check'; END IF;
END$$

CREATE TRIGGER `astra_access_groups_update_checks` BEFORE UPDATE ON `access_groups` FOR EACH ROW BEGIN
  IF NOT (JSON_TYPE(NEW.`permissions`) = 'ARRAY') THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: access_groups_permissions_json_array'; END IF;
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: access_groups_active_boolean'; END IF;
  IF NOT (((((NEW.`kind` = 'CUSTOMER') AND (NEW.`client_id` IS NOT NULL) AND (JSON_CONTAINS(JSON_ARRAY('PORTAL_ORGANIZATION_VIEW', 'PORTAL_ORGANIZATION_REPLY'), NEW.`permissions`))) OR ((NEW.`kind` = 'STAFF') AND (NEW.`client_id` IS NULL) AND (JSON_CONTAINS(JSON_ARRAY('TICKETS_TEAM_VIEW', 'TICKETS_ASSIGN', 'TICKETS_APPROVE', 'CATEGORY_MANAGE', 'SLA_MANAGE', 'REPORTS_VIEW', 'TAGS_MANAGE'), NEW.`permissions`)))))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: access_groups_check'; END IF;
  IF NOT (((CHAR_LENGTH(NEW.`description`) <= 500))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: access_groups_description_check'; END IF;
  IF NOT (((NEW.`kind` IN ('CUSTOMER', 'STAFF')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: access_groups_kind_check'; END IF;
  IF NOT ((((CHAR_LENGTH(TRIM(NEW.`name`)) >= 2) AND (CHAR_LENGTH(TRIM(NEW.`name`)) <= 120)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: access_groups_name_check'; END IF;
  IF NOT ((((NOT (JSON_CONTAINS(NEW.`permissions`, JSON_QUOTE('PORTAL_ORGANIZATION_REPLY')))) OR (JSON_CONTAINS(NEW.`permissions`, JSON_QUOTE('PORTAL_ORGANIZATION_VIEW')))))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: access_groups_permissions_check'; END IF;
END$$

CREATE TRIGGER `astra_app_schema_state_insert_checks` BEFORE INSERT ON `app_schema_state` FOR EACH ROW BEGIN
  IF NOT (((NEW.`id` = 1))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: app_schema_state_id_check'; END IF;
  IF NOT (((NEW.`version` > 0))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: app_schema_state_version_check'; END IF;
END$$

CREATE TRIGGER `astra_app_schema_state_update_checks` BEFORE UPDATE ON `app_schema_state` FOR EACH ROW BEGIN
  IF NOT (((NEW.`id` = 1))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: app_schema_state_id_check'; END IF;
  IF NOT (((NEW.`version` > 0))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: app_schema_state_version_check'; END IF;
END$$

CREATE TRIGGER `astra_assets_insert_checks` BEFORE INSERT ON `assets` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: assets_active_boolean'; END IF;
END$$

CREATE TRIGGER `astra_assets_update_checks` BEFORE UPDATE ON `assets` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: assets_active_boolean'; END IF;
END$$

CREATE TRIGGER `astra_cab_groups_insert_checks` BEFORE INSERT ON `cab_groups` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: cab_groups_active_boolean'; END IF;
  IF NOT ((((NEW.`quorum` > 0) AND (NEW.`quorum` <= 50)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: cab_groups_quorum_check'; END IF;
END$$

CREATE TRIGGER `astra_cab_groups_update_checks` BEFORE UPDATE ON `cab_groups` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: cab_groups_active_boolean'; END IF;
  IF NOT ((((NEW.`quorum` > 0) AND (NEW.`quorum` <= 50)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: cab_groups_quorum_check'; END IF;
END$$

CREATE TRIGGER `astra_categories_insert_checks` BEFORE INSERT ON `categories` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: categories_active_boolean'; END IF;
END$$

CREATE TRIGGER `astra_categories_update_checks` BEFORE UPDATE ON `categories` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: categories_active_boolean'; END IF;
END$$

CREATE TRIGGER `astra_change_approvals_insert_checks` BEFORE INSERT ON `change_approvals` FOR EACH ROW BEGIN
  IF NOT (NEW.`required` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_approvals_required_boolean'; END IF;
  IF NOT ((((NEW.`approver_user_id` IS NOT NULL) OR (NEW.`department_id` IS NOT NULL)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_approvals_check'; END IF;
  IF NOT (((NEW.`status` IN ('PENDING', 'APPROVED', 'REJECTED', 'CHANGES_REQUESTED')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_approvals_status_check'; END IF;
END$$

CREATE TRIGGER `astra_change_approvals_update_checks` BEFORE UPDATE ON `change_approvals` FOR EACH ROW BEGIN
  IF NOT (NEW.`required` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_approvals_required_boolean'; END IF;
  IF NOT ((((NEW.`approver_user_id` IS NOT NULL) OR (NEW.`department_id` IS NOT NULL)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_approvals_check'; END IF;
  IF NOT (((NEW.`status` IN ('PENDING', 'APPROVED', 'REJECTED', 'CHANGES_REQUESTED')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_approvals_status_check'; END IF;
END$$

CREATE TRIGGER `astra_change_attachments_insert_checks` BEFORE INSERT ON `change_attachments` FOR EACH ROW BEGIN
  IF NOT ((((NEW.`size_bytes` > 0) AND (NEW.`size_bytes` <= 5242880)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_attachments_size_bytes_check'; END IF;
END$$

CREATE TRIGGER `astra_change_attachments_update_checks` BEFORE UPDATE ON `change_attachments` FOR EACH ROW BEGIN
  IF NOT ((((NEW.`size_bytes` > 0) AND (NEW.`size_bytes` <= 5242880)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_attachments_size_bytes_check'; END IF;
END$$

CREATE TRIGGER `astra_change_blackouts_insert_checks` BEFORE INSERT ON `change_blackouts` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_blackouts_active_boolean'; END IF;
  IF NOT (((NEW.`ends_at` > NEW.`starts_at`))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_blackouts_check'; END IF;
  IF NOT ((((NEW.`site_id` IS NULL) OR (NEW.`client_id` IS NOT NULL)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_blackouts_check1'; END IF;
END$$

CREATE TRIGGER `astra_change_blackouts_update_checks` BEFORE UPDATE ON `change_blackouts` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_blackouts_active_boolean'; END IF;
  IF NOT (((NEW.`ends_at` > NEW.`starts_at`))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_blackouts_check'; END IF;
  IF NOT ((((NEW.`site_id` IS NULL) OR (NEW.`client_id` IS NOT NULL)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_blackouts_check1'; END IF;
END$$

CREATE TRIGGER `astra_change_communications_insert_checks` BEFORE INSERT ON `change_communications` FOR EACH ROW BEGIN
  IF NOT (JSON_TYPE(NEW.`recipient_emails`) = 'ARRAY') THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_communications_recipient_emails_json_array'; END IF;
  IF NOT (NEW.`include_client` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_communications_include_client_boolean'; END IF;
  IF NOT (((NEW.`status` IN ('DRAFT', 'QUEUED', 'SENT', 'FAILED')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_communications_status_check'; END IF;
END$$

CREATE TRIGGER `astra_change_communications_update_checks` BEFORE UPDATE ON `change_communications` FOR EACH ROW BEGIN
  IF NOT (JSON_TYPE(NEW.`recipient_emails`) = 'ARRAY') THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_communications_recipient_emails_json_array'; END IF;
  IF NOT (NEW.`include_client` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_communications_include_client_boolean'; END IF;
  IF NOT (((NEW.`status` IN ('DRAFT', 'QUEUED', 'SENT', 'FAILED')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_communications_status_check'; END IF;
END$$

CREATE TRIGGER `astra_change_governance_policy_insert_checks` BEFORE INSERT ON `change_governance_policy` FOR EACH ROW BEGIN
  IF NOT (NEW.`high_risk_cab` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_governance_policy_high_risk_cab_boolean'; END IF;
  IF NOT (NEW.`review_required` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_governance_policy_review_required_boolean'; END IF;
  IF NOT (((NEW.`id` = 1))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_governance_policy_id_check'; END IF;
  IF NOT ((((NEW.`minimum_approvals` >= 1) AND (NEW.`minimum_approvals` <= 10)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_governance_policy_minimum_approvals_check'; END IF;
END$$

CREATE TRIGGER `astra_change_governance_policy_update_checks` BEFORE UPDATE ON `change_governance_policy` FOR EACH ROW BEGIN
  IF NOT (NEW.`high_risk_cab` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_governance_policy_high_risk_cab_boolean'; END IF;
  IF NOT (NEW.`review_required` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_governance_policy_review_required_boolean'; END IF;
  IF NOT (((NEW.`id` = 1))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_governance_policy_id_check'; END IF;
  IF NOT ((((NEW.`minimum_approvals` >= 1) AND (NEW.`minimum_approvals` <= 10)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_governance_policy_minimum_approvals_check'; END IF;
END$$

CREATE TRIGGER `astra_change_reviews_insert_checks` BEFORE INSERT ON `change_reviews` FOR EACH ROW BEGIN
  IF NOT (((NEW.`outcome` IN ('SUCCESS', 'PARTIAL', 'FAILED', 'ROLLED_BACK')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_reviews_outcome_check'; END IF;
END$$

CREATE TRIGGER `astra_change_reviews_update_checks` BEFORE UPDATE ON `change_reviews` FOR EACH ROW BEGIN
  IF NOT (((NEW.`outcome` IN ('SUCCESS', 'PARTIAL', 'FAILED', 'ROLLED_BACK')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_reviews_outcome_check'; END IF;
END$$

CREATE TRIGGER `astra_change_stakeholders_insert_checks` BEFORE INSERT ON `change_stakeholders` FOR EACH ROW BEGIN
  IF NOT (((((NEW.`contact_id` IS NOT NULL) AND (NEW.`user_id` IS NULL) AND (NEW.`external_email` IS NULL)) OR ((NEW.`contact_id` IS NULL) AND (NEW.`user_id` IS NOT NULL) AND (NEW.`external_email` IS NULL)) OR ((NEW.`contact_id` IS NULL) AND (NEW.`user_id` IS NULL) AND (NEW.`external_email` IS NOT NULL))))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_stakeholders_identity_check'; END IF;
  IF NOT (((NEW.`stakeholder_role` IN ('STAKEHOLDER', 'BUSINESS_CONTACT', 'TECHNICAL_CONTACT', 'OBSERVER')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_stakeholders_stakeholder_role_check'; END IF;
END$$

CREATE TRIGGER `astra_change_stakeholders_update_checks` BEFORE UPDATE ON `change_stakeholders` FOR EACH ROW BEGIN
  IF NOT (((((NEW.`contact_id` IS NOT NULL) AND (NEW.`user_id` IS NULL) AND (NEW.`external_email` IS NULL)) OR ((NEW.`contact_id` IS NULL) AND (NEW.`user_id` IS NOT NULL) AND (NEW.`external_email` IS NULL)) OR ((NEW.`contact_id` IS NULL) AND (NEW.`user_id` IS NULL) AND (NEW.`external_email` IS NOT NULL))))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_stakeholders_identity_check'; END IF;
  IF NOT (((NEW.`stakeholder_role` IN ('STAKEHOLDER', 'BUSINESS_CONTACT', 'TECHNICAL_CONTACT', 'OBSERVER')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_stakeholders_stakeholder_role_check'; END IF;
END$$

CREATE TRIGGER `astra_change_tasks_insert_checks` BEFORE INSERT ON `change_tasks` FOR EACH ROW BEGIN
  IF NOT (((NEW.`status` IN ('TODO', 'IN_PROGRESS', 'BLOCKED', 'COMPLETED', 'WAIVED')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_tasks_status_check'; END IF;
END$$

CREATE TRIGGER `astra_change_tasks_update_checks` BEFORE UPDATE ON `change_tasks` FOR EACH ROW BEGIN
  IF NOT (((NEW.`status` IN ('TODO', 'IN_PROGRESS', 'BLOCKED', 'COMPLETED', 'WAIVED')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_tasks_status_check'; END IF;
END$$

CREATE TRIGGER `astra_change_teams_insert_checks` BEFORE INSERT ON `change_teams` FOR EACH ROW BEGIN
  IF NOT (((NEW.`role` IN ('LEAD', 'IMPLEMENTER', 'REVIEWER')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_teams_role_check'; END IF;
  IF NOT (((NEW.`status` IN ('PENDING', 'ACTIVE', 'COMPLETED')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_teams_status_check'; END IF;
END$$

CREATE TRIGGER `astra_change_teams_update_checks` BEFORE UPDATE ON `change_teams` FOR EACH ROW BEGIN
  IF NOT (((NEW.`role` IN ('LEAD', 'IMPLEMENTER', 'REVIEWER')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_teams_role_check'; END IF;
  IF NOT (((NEW.`status` IN ('PENDING', 'ACTIVE', 'COMPLETED')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_teams_status_check'; END IF;
END$$

CREATE TRIGGER `astra_change_templates_insert_checks` BEFORE INSERT ON `change_templates` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_templates_active_boolean'; END IF;
  IF NOT (((NEW.`category` IN ('SERVER', 'CLOUD', 'MICROSOFT_365', 'NETWORK', 'SECURITY', 'APPLICATION', 'OTHER')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_templates_category_check'; END IF;
END$$

CREATE TRIGGER `astra_change_templates_update_checks` BEFORE UPDATE ON `change_templates` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_templates_active_boolean'; END IF;
  IF NOT (((NEW.`category` IN ('SERVER', 'CLOUD', 'MICROSOFT_365', 'NETWORK', 'SECURITY', 'APPLICATION', 'OTHER')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_templates_category_check'; END IF;
END$$

CREATE TRIGGER `astra_change_updates_insert_checks` BEFORE INSERT ON `change_updates` FOR EACH ROW BEGIN
  IF NOT (((NEW.`update_type` IN ('INTERNAL', 'IMPLEMENTATION', 'VALIDATION', 'CLIENT_COMMUNICATION')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_updates_update_type_check'; END IF;
END$$

CREATE TRIGGER `astra_change_updates_update_checks` BEFORE UPDATE ON `change_updates` FOR EACH ROW BEGIN
  IF NOT (((NEW.`update_type` IN ('INTERNAL', 'IMPLEMENTATION', 'VALIDATION', 'CLIENT_COMMUNICATION')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: change_updates_update_type_check'; END IF;
END$$

CREATE TRIGGER `astra_changes_insert_checks` BEFORE INSERT ON `changes` FOR EACH ROW BEGIN
  IF NOT (NEW.`outage_expected` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: changes_outage_expected_boolean'; END IF;
  IF NOT (NEW.`client_notification_enabled` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: changes_client_notification_enabled_boolean'; END IF;
  IF NOT (JSON_TYPE(NEW.`affected_resources`) = 'ARRAY') THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: changes_affected_resources_json_array'; END IF;
  IF NOT (((NEW.`cab_quorum` >= 0))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: changes_cab_quorum_check'; END IF;
  IF NOT (((NEW.`category` IN ('SERVER', 'CLOUD', 'MICROSOFT_365', 'NETWORK', 'SECURITY', 'APPLICATION', 'OTHER')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: changes_category_check'; END IF;
  IF NOT (((NEW.`change_type` IN ('STANDARD', 'NORMAL', 'EMERGENCY')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: changes_change_type_check'; END IF;
  IF NOT ((((NEW.`planned_end` IS NULL) OR (NEW.`planned_start` IS NULL) OR (NEW.`planned_end` > NEW.`planned_start`)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: changes_check'; END IF;
  IF NOT (((NEW.`downtime_minutes` >= 0))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: changes_downtime_minutes_check'; END IF;
  IF NOT (((NEW.`revision` > 0))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: changes_revision_check'; END IF;
  IF NOT (((NEW.`risk` IN ('LOW', 'MEDIUM', 'HIGH', 'CRITICAL')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: changes_risk_check'; END IF;
  IF NOT (((NEW.`status` IN ('DRAFT', 'SUBMITTED', 'ASSESSMENT', 'AWAITING_APPROVAL', 'APPROVED', 'SCHEDULED', 'IN_PROGRESS', 'VALIDATION', 'COMPLETED', 'CLOSED', 'REJECTED', 'CANCELLED', 'FAILED', 'ROLLED_BACK')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: changes_status_check'; END IF;
END$$

CREATE TRIGGER `astra_changes_update_checks` BEFORE UPDATE ON `changes` FOR EACH ROW BEGIN
  IF NOT (NEW.`outage_expected` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: changes_outage_expected_boolean'; END IF;
  IF NOT (NEW.`client_notification_enabled` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: changes_client_notification_enabled_boolean'; END IF;
  IF NOT (JSON_TYPE(NEW.`affected_resources`) = 'ARRAY') THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: changes_affected_resources_json_array'; END IF;
  IF NOT (((NEW.`cab_quorum` >= 0))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: changes_cab_quorum_check'; END IF;
  IF NOT (((NEW.`category` IN ('SERVER', 'CLOUD', 'MICROSOFT_365', 'NETWORK', 'SECURITY', 'APPLICATION', 'OTHER')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: changes_category_check'; END IF;
  IF NOT (((NEW.`change_type` IN ('STANDARD', 'NORMAL', 'EMERGENCY')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: changes_change_type_check'; END IF;
  IF NOT ((((NEW.`planned_end` IS NULL) OR (NEW.`planned_start` IS NULL) OR (NEW.`planned_end` > NEW.`planned_start`)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: changes_check'; END IF;
  IF NOT (((NEW.`downtime_minutes` >= 0))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: changes_downtime_minutes_check'; END IF;
  IF NOT (((NEW.`revision` > 0))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: changes_revision_check'; END IF;
  IF NOT (((NEW.`risk` IN ('LOW', 'MEDIUM', 'HIGH', 'CRITICAL')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: changes_risk_check'; END IF;
  IF NOT (((NEW.`status` IN ('DRAFT', 'SUBMITTED', 'ASSESSMENT', 'AWAITING_APPROVAL', 'APPROVED', 'SCHEDULED', 'IN_PROGRESS', 'VALIDATION', 'COMPLETED', 'CLOSED', 'REJECTED', 'CANCELLED', 'FAILED', 'ROLLED_BACK')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: changes_status_check'; END IF;
END$$

CREATE TRIGGER `astra_client_email_domains_insert_checks` BEFORE INSERT ON `client_email_domains` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: client_email_domains_active_boolean'; END IF;
  IF NOT ((((NEW.`domain` = lower(TRIM(NEW.`domain`))) AND (REGEXP_LIKE(NEW.`domain`, '^[a-z0-9][a-z0-9.-]+[.][a-z0-9-]+$', 'c'))))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: client_email_domains_domain_check'; END IF;
END$$

CREATE TRIGGER `astra_client_email_domains_update_checks` BEFORE UPDATE ON `client_email_domains` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: client_email_domains_active_boolean'; END IF;
  IF NOT ((((NEW.`domain` = lower(TRIM(NEW.`domain`))) AND (REGEXP_LIKE(NEW.`domain`, '^[a-z0-9][a-z0-9.-]+[.][a-z0-9-]+$', 'c'))))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: client_email_domains_domain_check'; END IF;
END$$

CREATE TRIGGER `astra_clients_insert_checks` BEFORE INSERT ON `clients` FOR EACH ROW BEGIN
  IF NOT (((NEW.`organization_type` IN ('CLIENT', 'INTERNAL')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: clients_organization_type_check'; END IF;
  IF NOT (((NEW.`status` IN ('ACTIVE', 'INACTIVE')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: clients_status_check'; END IF;
  IF NOT (((REGEXP_LIKE(NEW.`ticket_prefix`, '^[A-Z][A-Z0-9]{1,7}$', 'c')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: clients_ticket_prefix_format'; END IF;
END$$

CREATE TRIGGER `astra_clients_update_checks` BEFORE UPDATE ON `clients` FOR EACH ROW BEGIN
  IF NOT (((NEW.`organization_type` IN ('CLIENT', 'INTERNAL')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: clients_organization_type_check'; END IF;
  IF NOT (((NEW.`status` IN ('ACTIVE', 'INACTIVE')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: clients_status_check'; END IF;
  IF NOT (((REGEXP_LIKE(NEW.`ticket_prefix`, '^[A-Z][A-Z0-9]{1,7}$', 'c')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: clients_ticket_prefix_format'; END IF;
END$$

CREATE TRIGGER `astra_contacts_insert_checks` BEFORE INSERT ON `contacts` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: contacts_active_boolean'; END IF;
END$$

CREATE TRIGGER `astra_contacts_update_checks` BEFORE UPDATE ON `contacts` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: contacts_active_boolean'; END IF;
END$$

CREATE TRIGGER `astra_department_users_insert_checks` BEFORE INSERT ON `department_users` FOR EACH ROW BEGIN
  IF NOT (NEW.`is_lead` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: department_users_is_lead_boolean'; END IF;
END$$

CREATE TRIGGER `astra_department_users_update_checks` BEFORE UPDATE ON `department_users` FOR EACH ROW BEGIN
  IF NOT (NEW.`is_lead` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: department_users_is_lead_boolean'; END IF;
END$$

CREATE TRIGGER `astra_departments_insert_checks` BEFORE INSERT ON `departments` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: departments_active_boolean'; END IF;
END$$

CREATE TRIGGER `astra_departments_update_checks` BEFORE UPDATE ON `departments` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: departments_active_boolean'; END IF;
END$$

CREATE TRIGGER `astra_help_requests_insert_checks` BEFORE INSERT ON `help_requests` FOR EACH ROW BEGIN
  IF NOT ((((NEW.`department_id` IS NOT NULL) OR (NEW.`requested_user_id` IS NOT NULL)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: help_requests_check'; END IF;
  IF NOT (((NEW.`status` IN ('PENDING', 'ACCEPTED', 'DECLINED', 'COMPLETED')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: help_requests_status_check'; END IF;
END$$

CREATE TRIGGER `astra_help_requests_update_checks` BEFORE UPDATE ON `help_requests` FOR EACH ROW BEGIN
  IF NOT ((((NEW.`department_id` IS NOT NULL) OR (NEW.`requested_user_id` IS NOT NULL)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: help_requests_check'; END IF;
  IF NOT (((NEW.`status` IN ('PENDING', 'ACCEPTED', 'DECLINED', 'COMPLETED')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: help_requests_status_check'; END IF;
END$$

CREATE TRIGGER `astra_knowledge_articles_insert_checks` BEFORE INSERT ON `knowledge_articles` FOR EACH ROW BEGIN
  IF NOT (((NEW.`status` IN ('DRAFT', 'PUBLISHED', 'ARCHIVED')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: knowledge_articles_status_check'; END IF;
  IF NOT (((NEW.`visibility` IN ('STAFF', 'CLIENT')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: knowledge_articles_visibility_check'; END IF;
END$$

CREATE TRIGGER `astra_knowledge_articles_update_checks` BEFORE UPDATE ON `knowledge_articles` FOR EACH ROW BEGIN
  IF NOT (((NEW.`status` IN ('DRAFT', 'PUBLISHED', 'ARCHIVED')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: knowledge_articles_status_check'; END IF;
  IF NOT (((NEW.`visibility` IN ('STAFF', 'CLIENT')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: knowledge_articles_visibility_check'; END IF;
END$$

CREATE TRIGGER `astra_mail_attachments_insert_checks` BEFORE INSERT ON `mail_attachments` FOR EACH ROW BEGIN
  IF NOT ((((NEW.`size_bytes` > 0) AND (NEW.`size_bytes` <= 5242880)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: mail_attachments_size_bytes_check'; END IF;
END$$

CREATE TRIGGER `astra_mail_attachments_update_checks` BEFORE UPDATE ON `mail_attachments` FOR EACH ROW BEGIN
  IF NOT ((((NEW.`size_bytes` > 0) AND (NEW.`size_bytes` <= 5242880)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: mail_attachments_size_bytes_check'; END IF;
END$$

CREATE TRIGGER `astra_mail_messages_insert_checks` BEFORE INSERT ON `mail_messages` FOR EACH ROW BEGIN
  IF NOT (JSON_TYPE(NEW.`reference_ids`) = 'ARRAY') THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: mail_messages_reference_ids_json_array'; END IF;
  IF NOT (JSON_TYPE(NEW.`recipient_addresses`) = 'ARRAY') THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: mail_messages_recipient_addresses_json_array'; END IF;
  IF NOT (((NEW.`channel` IN ('TICKET', 'CHANGE', 'TEST')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: mail_messages_channel_check'; END IF;
  IF NOT (((NOT ((NEW.`ticket_id` IS NOT NULL) AND (NEW.`change_id` IS NOT NULL))))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: mail_messages_check'; END IF;
  IF NOT (((NEW.`direction` IN ('INBOUND', 'OUTBOUND')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: mail_messages_direction_check'; END IF;
  IF NOT (((NEW.`state` IN ('QUEUED', 'SENDING', 'SENT', 'FAILED', 'UNKNOWN', 'RECEIVED', 'REVIEW', 'IGNORED')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: mail_messages_state_check'; END IF;
END$$

CREATE TRIGGER `astra_mail_messages_update_checks` BEFORE UPDATE ON `mail_messages` FOR EACH ROW BEGIN
  IF NOT (JSON_TYPE(NEW.`reference_ids`) = 'ARRAY') THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: mail_messages_reference_ids_json_array'; END IF;
  IF NOT (JSON_TYPE(NEW.`recipient_addresses`) = 'ARRAY') THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: mail_messages_recipient_addresses_json_array'; END IF;
  IF NOT (((NEW.`channel` IN ('TICKET', 'CHANGE', 'TEST')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: mail_messages_channel_check'; END IF;
  IF NOT (((NOT ((NEW.`ticket_id` IS NOT NULL) AND (NEW.`change_id` IS NOT NULL))))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: mail_messages_check'; END IF;
  IF NOT (((NEW.`direction` IN ('INBOUND', 'OUTBOUND')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: mail_messages_direction_check'; END IF;
  IF NOT (((NEW.`state` IN ('QUEUED', 'SENDING', 'SENT', 'FAILED', 'UNKNOWN', 'RECEIVED', 'REVIEW', 'IGNORED')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: mail_messages_state_check'; END IF;
END$$

CREATE TRIGGER `astra_mail_settings_insert_checks` BEFORE INSERT ON `mail_settings` FOR EACH ROW BEGIN
  IF NOT (NEW.`enabled` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: mail_settings_enabled_boolean'; END IF;
  IF NOT (((NEW.`id` = 1))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: mail_settings_id_check'; END IF;
  IF NOT (((NEW.`inbound_provider` IN ('DISABLED', 'MICROSOFT_365')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: mail_settings_inbound_provider_check'; END IF;
  IF NOT (((NEW.`tls_mode` IN ('STARTTLS', 'TLS')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: mail_settings_tls_mode_check'; END IF;
END$$

CREATE TRIGGER `astra_mail_settings_update_checks` BEFORE UPDATE ON `mail_settings` FOR EACH ROW BEGIN
  IF NOT (NEW.`enabled` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: mail_settings_enabled_boolean'; END IF;
  IF NOT (((NEW.`id` = 1))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: mail_settings_id_check'; END IF;
  IF NOT (((NEW.`inbound_provider` IN ('DISABLED', 'MICROSOFT_365')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: mail_settings_inbound_provider_check'; END IF;
  IF NOT (((NEW.`tls_mode` IN ('STARTTLS', 'TLS')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: mail_settings_tls_mode_check'; END IF;
END$$

CREATE TRIGGER `astra_notification_preferences_insert_checks` BEFORE INSERT ON `notification_preferences` FOR EACH ROW BEGIN
  IF NOT (NEW.`ticket_assigned` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: notification_preferences_ticket_assigned_boolean'; END IF;
  IF NOT (NEW.`ticket_transferred` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: notification_preferences_ticket_transferred_boolean'; END IF;
  IF NOT (NEW.`public_reply` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: notification_preferences_public_reply_boolean'; END IF;
  IF NOT (NEW.`sla_at_risk` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: notification_preferences_sla_at_risk_boolean'; END IF;
  IF NOT (NEW.`sla_breached` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: notification_preferences_sla_breached_boolean'; END IF;
  IF NOT (NEW.`ticket_unattended` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: notification_preferences_ticket_unattended_boolean'; END IF;
END$$

CREATE TRIGGER `astra_notification_preferences_update_checks` BEFORE UPDATE ON `notification_preferences` FOR EACH ROW BEGIN
  IF NOT (NEW.`ticket_assigned` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: notification_preferences_ticket_assigned_boolean'; END IF;
  IF NOT (NEW.`ticket_transferred` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: notification_preferences_ticket_transferred_boolean'; END IF;
  IF NOT (NEW.`public_reply` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: notification_preferences_public_reply_boolean'; END IF;
  IF NOT (NEW.`sla_at_risk` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: notification_preferences_sla_at_risk_boolean'; END IF;
  IF NOT (NEW.`sla_breached` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: notification_preferences_sla_breached_boolean'; END IF;
  IF NOT (NEW.`ticket_unattended` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: notification_preferences_ticket_unattended_boolean'; END IF;
END$$

CREATE TRIGGER `astra_notifications_insert_checks` BEFORE INSERT ON `notifications` FOR EACH ROW BEGIN
  IF NOT (((NEW.`type` IN ('TICKET_ASSIGNED', 'TICKET_TRANSFERRED', 'PUBLIC_REPLY', 'SLA_AT_RISK', 'SLA_BREACHED', 'TICKET_UNATTENDED', 'CHANGE_APPROVAL_REQUESTED', 'CHANGE_TASK_ASSIGNED', 'CHANGE_STATUS_UPDATED', 'HELP_REQUESTED', 'HELP_ACCEPTED')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: notifications_type_check'; END IF;
END$$

CREATE TRIGGER `astra_notifications_update_checks` BEFORE UPDATE ON `notifications` FOR EACH ROW BEGIN
  IF NOT (((NEW.`type` IN ('TICKET_ASSIGNED', 'TICKET_TRANSFERRED', 'PUBLIC_REPLY', 'SLA_AT_RISK', 'SLA_BREACHED', 'TICKET_UNATTENDED', 'CHANGE_APPROVAL_REQUESTED', 'CHANGE_TASK_ASSIGNED', 'CHANGE_STATUS_UPDATED', 'HELP_REQUESTED', 'HELP_ACCEPTED')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: notifications_type_check'; END IF;
END$$

CREATE TRIGGER `astra_portal_ticket_submissions_insert_checks` BEFORE INSERT ON `portal_ticket_submissions` FOR EACH ROW BEGIN
  IF NOT (((NEW.`requester_email` = lower(TRIM(NEW.`requester_email`))))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: portal_ticket_submissions_requester_email_check'; END IF;
END$$

CREATE TRIGGER `astra_portal_ticket_submissions_update_checks` BEFORE UPDATE ON `portal_ticket_submissions` FOR EACH ROW BEGIN
  IF NOT (((NEW.`requester_email` = lower(TRIM(NEW.`requester_email`))))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: portal_ticket_submissions_requester_email_check'; END IF;
END$$

CREATE TRIGGER `astra_problems_insert_checks` BEFORE INSERT ON `problems` FOR EACH ROW BEGIN
  IF NOT ((((NEW.`status` NOT IN ('KNOWN_ERROR', 'RESOLVED', 'CLOSED')) OR (CHAR_LENGTH(TRIM(NEW.`root_cause`)) >= 3)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: problems_check'; END IF;
  IF NOT ((((NEW.`status` <> 'KNOWN_ERROR') OR (CHAR_LENGTH(TRIM(NEW.`workaround`)) >= 3)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: problems_check1'; END IF;
  IF NOT ((((NEW.`status` NOT IN ('RESOLVED', 'CLOSED')) OR (CHAR_LENGTH(TRIM(NEW.`resolution`)) >= 3)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: problems_check2'; END IF;
  IF NOT ((((CHAR_LENGTH(TRIM(NEW.`description`)) >= 3) AND (CHAR_LENGTH(TRIM(NEW.`description`)) <= 10000)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: problems_description_check'; END IF;
  IF NOT (((NEW.`priority` IN ('P1_CRITICAL', 'P2_HIGH', 'P3_MEDIUM', 'P4_LOW')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: problems_priority_check'; END IF;
  IF NOT (((NEW.`status` IN ('OPEN', 'INVESTIGATING', 'KNOWN_ERROR', 'RESOLVED', 'CLOSED')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: problems_status_check'; END IF;
  IF NOT ((((CHAR_LENGTH(TRIM(NEW.`title`)) >= 3) AND (CHAR_LENGTH(TRIM(NEW.`title`)) <= 200)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: problems_title_check'; END IF;
END$$

CREATE TRIGGER `astra_problems_update_checks` BEFORE UPDATE ON `problems` FOR EACH ROW BEGIN
  IF NOT ((((NEW.`status` NOT IN ('KNOWN_ERROR', 'RESOLVED', 'CLOSED')) OR (CHAR_LENGTH(TRIM(NEW.`root_cause`)) >= 3)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: problems_check'; END IF;
  IF NOT ((((NEW.`status` <> 'KNOWN_ERROR') OR (CHAR_LENGTH(TRIM(NEW.`workaround`)) >= 3)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: problems_check1'; END IF;
  IF NOT ((((NEW.`status` NOT IN ('RESOLVED', 'CLOSED')) OR (CHAR_LENGTH(TRIM(NEW.`resolution`)) >= 3)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: problems_check2'; END IF;
  IF NOT ((((CHAR_LENGTH(TRIM(NEW.`description`)) >= 3) AND (CHAR_LENGTH(TRIM(NEW.`description`)) <= 10000)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: problems_description_check'; END IF;
  IF NOT (((NEW.`priority` IN ('P1_CRITICAL', 'P2_HIGH', 'P3_MEDIUM', 'P4_LOW')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: problems_priority_check'; END IF;
  IF NOT (((NEW.`status` IN ('OPEN', 'INVESTIGATING', 'KNOWN_ERROR', 'RESOLVED', 'CLOSED')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: problems_status_check'; END IF;
  IF NOT ((((CHAR_LENGTH(TRIM(NEW.`title`)) >= 3) AND (CHAR_LENGTH(TRIM(NEW.`title`)) <= 200)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: problems_title_check'; END IF;
END$$

CREATE TRIGGER `astra_role_access_policies_insert_checks` BEFORE INSERT ON `role_access_policies` FOR EACH ROW BEGIN
  IF NOT (JSON_TYPE(NEW.`permissions`) = 'ARRAY') THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: role_access_policies_permissions_json_array'; END IF;
  IF NOT (((JSON_CONTAINS(JSON_ARRAY('TICKETS_TEAM_VIEW', 'TICKETS_ASSIGN', 'TICKETS_APPROVE', 'CATEGORY_MANAGE', 'SLA_MANAGE', 'REPORTS_VIEW', 'TAGS_MANAGE', 'AUDIT_VIEW'), NEW.`permissions`)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: role_access_policies_permissions_check'; END IF;
  IF NOT (((NEW.`revision` > 0))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: role_access_policies_revision_check'; END IF;
  IF NOT (((NEW.`role_code` IN ('TECHNICIAN', 'TEAM_LEAD', 'MANAGER')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: role_access_policies_role_code_check'; END IF;
END$$

CREATE TRIGGER `astra_role_access_policies_update_checks` BEFORE UPDATE ON `role_access_policies` FOR EACH ROW BEGIN
  IF NOT (JSON_TYPE(NEW.`permissions`) = 'ARRAY') THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: role_access_policies_permissions_json_array'; END IF;
  IF NOT (((JSON_CONTAINS(JSON_ARRAY('TICKETS_TEAM_VIEW', 'TICKETS_ASSIGN', 'TICKETS_APPROVE', 'CATEGORY_MANAGE', 'SLA_MANAGE', 'REPORTS_VIEW', 'TAGS_MANAGE', 'AUDIT_VIEW'), NEW.`permissions`)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: role_access_policies_permissions_check'; END IF;
  IF NOT (((NEW.`revision` > 0))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: role_access_policies_revision_check'; END IF;
  IF NOT (((NEW.`role_code` IN ('TECHNICIAN', 'TEAM_LEAD', 'MANAGER')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: role_access_policies_role_code_check'; END IF;
END$$

CREATE TRIGGER `astra_service_templates_insert_checks` BEFORE INSERT ON `service_templates` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: service_templates_active_boolean'; END IF;
END$$

CREATE TRIGGER `astra_service_templates_update_checks` BEFORE UPDATE ON `service_templates` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: service_templates_active_boolean'; END IF;
END$$

CREATE TRIGGER `astra_site_ticket_sequences_insert_checks` BEFORE INSERT ON `site_ticket_sequences` FOR EACH ROW BEGIN
  IF NOT (((NEW.`next_number` > 0))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: site_ticket_sequences_next_number_check'; END IF;
END$$

CREATE TRIGGER `astra_site_ticket_sequences_update_checks` BEFORE UPDATE ON `site_ticket_sequences` FOR EACH ROW BEGIN
  IF NOT (((NEW.`next_number` > 0))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: site_ticket_sequences_next_number_check'; END IF;
END$$

CREATE TRIGGER `astra_sites_insert_checks` BEFORE INSERT ON `sites` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: sites_active_boolean'; END IF;
  IF NOT (((REGEXP_LIKE(NEW.`ticket_prefix`, '^[A-Z][A-Z0-9]{1,15}$', 'c')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: sites_ticket_prefix_format'; END IF;
END$$

CREATE TRIGGER `astra_sites_update_checks` BEFORE UPDATE ON `sites` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: sites_active_boolean'; END IF;
  IF NOT (((REGEXP_LIKE(NEW.`ticket_prefix`, '^[A-Z][A-Z0-9]{1,15}$', 'c')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: sites_ticket_prefix_format'; END IF;
END$$

CREATE TRIGGER `astra_sla_calendars_insert_checks` BEFORE INSERT ON `sla_calendars` FOR EACH ROW BEGIN
  IF NOT (JSON_TYPE(NEW.`working_days`) = 'ARRAY') THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: sla_calendars_working_days_json_array'; END IF;
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: sla_calendars_active_boolean'; END IF;
  IF NOT (((NEW.`work_end` > NEW.`work_start`))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: sla_calendars_check'; END IF;
  IF NOT ((((JSON_LENGTH(NEW.`working_days`) >= 1) AND (JSON_LENGTH(NEW.`working_days`) <= 7)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: sla_calendars_working_days_check'; END IF;
END$$

CREATE TRIGGER `astra_sla_calendars_update_checks` BEFORE UPDATE ON `sla_calendars` FOR EACH ROW BEGIN
  IF NOT (JSON_TYPE(NEW.`working_days`) = 'ARRAY') THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: sla_calendars_working_days_json_array'; END IF;
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: sla_calendars_active_boolean'; END IF;
  IF NOT (((NEW.`work_end` > NEW.`work_start`))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: sla_calendars_check'; END IF;
  IF NOT ((((JSON_LENGTH(NEW.`working_days`) >= 1) AND (JSON_LENGTH(NEW.`working_days`) <= 7)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: sla_calendars_working_days_check'; END IF;
END$$

CREATE TRIGGER `astra_sla_policies_insert_checks` BEFORE INSERT ON `sla_policies` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: sla_policies_active_boolean'; END IF;
  IF NOT (((NEW.`resolution_minutes` >= NEW.`response_minutes`))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: sla_policies_check'; END IF;
  IF NOT (((NEW.`priority` IN ('P1_CRITICAL', 'P2_HIGH', 'P3_MEDIUM', 'P4_LOW')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: sla_policies_priority_check'; END IF;
  IF NOT ((((NEW.`resolution_minutes` >= 1) AND (NEW.`resolution_minutes` <= 525600)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: sla_policies_resolution_minutes_check'; END IF;
  IF NOT ((((NEW.`response_minutes` >= 1) AND (NEW.`response_minutes` <= 525600)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: sla_policies_response_minutes_check'; END IF;
  IF NOT ((((NEW.`warning_percent` >= 1) AND (NEW.`warning_percent` <= 99)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: sla_policies_warning_percent_check'; END IF;
END$$

CREATE TRIGGER `astra_sla_policies_update_checks` BEFORE UPDATE ON `sla_policies` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: sla_policies_active_boolean'; END IF;
  IF NOT (((NEW.`resolution_minutes` >= NEW.`response_minutes`))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: sla_policies_check'; END IF;
  IF NOT (((NEW.`priority` IN ('P1_CRITICAL', 'P2_HIGH', 'P3_MEDIUM', 'P4_LOW')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: sla_policies_priority_check'; END IF;
  IF NOT ((((NEW.`resolution_minutes` >= 1) AND (NEW.`resolution_minutes` <= 525600)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: sla_policies_resolution_minutes_check'; END IF;
  IF NOT ((((NEW.`response_minutes` >= 1) AND (NEW.`response_minutes` <= 525600)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: sla_policies_response_minutes_check'; END IF;
  IF NOT ((((NEW.`warning_percent` >= 1) AND (NEW.`warning_percent` <= 99)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: sla_policies_warning_percent_check'; END IF;
END$$

CREATE TRIGGER `astra_tags_insert_checks` BEFORE INSERT ON `tags` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: tags_active_boolean'; END IF;
END$$

CREATE TRIGGER `astra_tags_update_checks` BEFORE UPDATE ON `tags` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: tags_active_boolean'; END IF;
END$$

CREATE TRIGGER `astra_ticket_approvals_insert_checks` BEFORE INSERT ON `ticket_approvals` FOR EACH ROW BEGIN
  IF NOT (((NEW.`status` IN ('PENDING', 'APPROVED', 'REJECTED')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: ticket_approvals_status_check'; END IF;
END$$

CREATE TRIGGER `astra_ticket_approvals_update_checks` BEFORE UPDATE ON `ticket_approvals` FOR EACH ROW BEGIN
  IF NOT (((NEW.`status` IN ('PENDING', 'APPROVED', 'REJECTED')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: ticket_approvals_status_check'; END IF;
END$$

CREATE TRIGGER `astra_ticket_attachments_insert_checks` BEFORE INSERT ON `ticket_attachments` FOR EACH ROW BEGIN
  IF NOT ((((NEW.`size_bytes` > 0) AND (NEW.`size_bytes` <= 10485760)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: ticket_attachments_size_bytes_check'; END IF;
  IF NOT (((NEW.`visibility` IN ('INTERNAL', 'PUBLIC')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: ticket_attachments_visibility_check'; END IF;
END$$

CREATE TRIGGER `astra_ticket_attachments_update_checks` BEFORE UPDATE ON `ticket_attachments` FOR EACH ROW BEGIN
  IF NOT ((((NEW.`size_bytes` > 0) AND (NEW.`size_bytes` <= 10485760)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: ticket_attachments_size_bytes_check'; END IF;
  IF NOT (((NEW.`visibility` IN ('INTERNAL', 'PUBLIC')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: ticket_attachments_visibility_check'; END IF;
END$$

CREATE TRIGGER `astra_ticket_comments_insert_checks` BEFORE INSERT ON `ticket_comments` FOR EACH ROW BEGIN
  IF NOT (((NEW.`comment_type` IN ('PUBLIC', 'INTERNAL')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: ticket_comments_comment_type_check'; END IF;
END$$

CREATE TRIGGER `astra_ticket_comments_update_checks` BEFORE UPDATE ON `ticket_comments` FOR EACH ROW BEGIN
  IF NOT (((NEW.`comment_type` IN ('PUBLIC', 'INTERNAL')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: ticket_comments_comment_type_check'; END IF;
END$$

CREATE TRIGGER `astra_ticket_prefix_registry_insert_checks` BEFORE INSERT ON `ticket_prefix_registry` FOR EACH ROW BEGIN
  IF NOT ((((NEW.`owner_kind` = 'LEGACY') = (NEW.`owner_id` IS NULL)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: ticket_prefix_registry_check'; END IF;
  IF NOT (((NEW.`owner_kind` IN ('CLIENT', 'SITE', 'LEGACY')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: ticket_prefix_registry_owner_kind_check'; END IF;
  IF NOT (((REGEXP_LIKE(NEW.`prefix`, '^[A-Z][A-Z0-9]{1,15}$', 'c')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: ticket_prefix_registry_prefix_check'; END IF;
END$$

CREATE TRIGGER `astra_ticket_prefix_registry_update_checks` BEFORE UPDATE ON `ticket_prefix_registry` FOR EACH ROW BEGIN
  IF NOT ((((NEW.`owner_kind` = 'LEGACY') = (NEW.`owner_id` IS NULL)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: ticket_prefix_registry_check'; END IF;
  IF NOT (((NEW.`owner_kind` IN ('CLIENT', 'SITE', 'LEGACY')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: ticket_prefix_registry_owner_kind_check'; END IF;
  IF NOT (((REGEXP_LIKE(NEW.`prefix`, '^[A-Z][A-Z0-9]{1,15}$', 'c')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: ticket_prefix_registry_prefix_check'; END IF;
END$$

CREATE TRIGGER `astra_ticket_sources_insert_checks` BEFORE INSERT ON `ticket_sources` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: ticket_sources_active_boolean'; END IF;
END$$

CREATE TRIGGER `astra_ticket_sources_update_checks` BEFORE UPDATE ON `ticket_sources` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: ticket_sources_active_boolean'; END IF;
END$$

CREATE TRIGGER `astra_tickets_insert_checks` BEFORE INSERT ON `tickets` FOR EACH ROW BEGIN
  IF NOT (((NEW.`approval_status` IN ('NOT_REQUIRED', 'PENDING', 'APPROVED', 'REJECTED')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: tickets_approval_status_check'; END IF;
  IF NOT (((NEW.`priority` IN ('P1_CRITICAL', 'P2_HIGH', 'P3_MEDIUM', 'P4_LOW')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: tickets_priority_check'; END IF;
  IF NOT (((NEW.`status` IN ('NEW', 'OPEN', 'IN_PROGRESS', 'WAITING_FOR_CLIENT', 'WAITING_FOR_VENDOR', 'ON_HOLD', 'RESOLVED', 'CLOSED', 'CANCELLED')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: tickets_status_check'; END IF;
END$$

CREATE TRIGGER `astra_tickets_update_checks` BEFORE UPDATE ON `tickets` FOR EACH ROW BEGIN
  IF NOT (((NEW.`approval_status` IN ('NOT_REQUIRED', 'PENDING', 'APPROVED', 'REJECTED')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: tickets_approval_status_check'; END IF;
  IF NOT (((NEW.`priority` IN ('P1_CRITICAL', 'P2_HIGH', 'P3_MEDIUM', 'P4_LOW')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: tickets_priority_check'; END IF;
  IF NOT (((NEW.`status` IN ('NEW', 'OPEN', 'IN_PROGRESS', 'WAITING_FOR_CLIENT', 'WAITING_FOR_VENDOR', 'ON_HOLD', 'RESOLVED', 'CLOSED', 'CANCELLED')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: tickets_status_check'; END IF;
END$$

CREATE TRIGGER `astra_user_access_policies_insert_checks` BEFORE INSERT ON `user_access_policies` FOR EACH ROW BEGIN
  IF NOT (JSON_TYPE(NEW.`allow_permissions`) = 'ARRAY') THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: user_access_policies_allow_permissions_json_array'; END IF;
  IF NOT (JSON_TYPE(NEW.`deny_permissions`) = 'ARRAY') THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: user_access_policies_deny_permissions_json_array'; END IF;
  IF NOT (((JSON_CONTAINS(JSON_ARRAY('TICKETS_TEAM_VIEW', 'TICKETS_ASSIGN', 'TICKETS_APPROVE', 'CATEGORY_MANAGE', 'SLA_MANAGE', 'REPORTS_VIEW', 'TAGS_MANAGE', 'AUDIT_VIEW'), NEW.`allow_permissions`)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: user_access_policies_allow_permissions_check'; END IF;
  IF NOT (((NOT (JSON_OVERLAPS(NEW.`allow_permissions`, NEW.`deny_permissions`))))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: user_access_policies_check'; END IF;
  IF NOT (((JSON_CONTAINS(JSON_ARRAY('TICKETS_TEAM_VIEW', 'TICKETS_ASSIGN', 'TICKETS_APPROVE', 'CATEGORY_MANAGE', 'SLA_MANAGE', 'REPORTS_VIEW', 'TAGS_MANAGE', 'AUDIT_VIEW'), NEW.`deny_permissions`)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: user_access_policies_deny_permissions_check'; END IF;
  IF NOT (((NEW.`revision` > 0))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: user_access_policies_revision_check'; END IF;
END$$

CREATE TRIGGER `astra_user_access_policies_update_checks` BEFORE UPDATE ON `user_access_policies` FOR EACH ROW BEGIN
  IF NOT (JSON_TYPE(NEW.`allow_permissions`) = 'ARRAY') THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: user_access_policies_allow_permissions_json_array'; END IF;
  IF NOT (JSON_TYPE(NEW.`deny_permissions`) = 'ARRAY') THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: user_access_policies_deny_permissions_json_array'; END IF;
  IF NOT (((JSON_CONTAINS(JSON_ARRAY('TICKETS_TEAM_VIEW', 'TICKETS_ASSIGN', 'TICKETS_APPROVE', 'CATEGORY_MANAGE', 'SLA_MANAGE', 'REPORTS_VIEW', 'TAGS_MANAGE', 'AUDIT_VIEW'), NEW.`allow_permissions`)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: user_access_policies_allow_permissions_check'; END IF;
  IF NOT (((NOT (JSON_OVERLAPS(NEW.`allow_permissions`, NEW.`deny_permissions`))))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: user_access_policies_check'; END IF;
  IF NOT (((JSON_CONTAINS(JSON_ARRAY('TICKETS_TEAM_VIEW', 'TICKETS_ASSIGN', 'TICKETS_APPROVE', 'CATEGORY_MANAGE', 'SLA_MANAGE', 'REPORTS_VIEW', 'TAGS_MANAGE', 'AUDIT_VIEW'), NEW.`deny_permissions`)))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: user_access_policies_deny_permissions_check'; END IF;
  IF NOT (((NEW.`revision` > 0))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: user_access_policies_revision_check'; END IF;
END$$

CREATE TRIGGER `astra_users_insert_checks` BEFORE INSERT ON `users` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: users_active_boolean'; END IF;
  IF NOT (((NEW.`change_access` IN ('NONE', 'CONTRIBUTOR', 'COORDINATOR', 'APPROVER', 'CHANGE_MANAGER')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: users_change_access_check'; END IF;
  IF NOT (((NEW.`work_scope` IN ('INTERNAL', 'CLIENT', 'BOTH', 'ALL')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: users_work_scope_check'; END IF;
END$$

CREATE TRIGGER `astra_users_update_checks` BEFORE UPDATE ON `users` FOR EACH ROW BEGIN
  IF NOT (NEW.`active` IN (0, 1)) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: users_active_boolean'; END IF;
  IF NOT (((NEW.`change_access` IN ('NONE', 'CONTRIBUTOR', 'COORDINATOR', 'APPROVER', 'CHANGE_MANAGER')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: users_change_access_check'; END IF;
  IF NOT (((NEW.`work_scope` IN ('INTERNAL', 'CLIENT', 'BOTH', 'ALL')))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Constraint: users_work_scope_check'; END IF;
END$$

CREATE TRIGGER `astra_users_delete_checks` BEFORE DELETE ON `users` FOR EACH ROW BEGIN
  IF EXISTS (SELECT 1 FROM `change_approvals` AS child WHERE child.`approver_user_id` = OLD.`id` AND NOT ((((NULL IS NOT NULL) OR (child.`department_id` IS NOT NULL))))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Referenced constraint: change_approvals_check'; END IF;
  IF EXISTS (SELECT 1 FROM `help_requests` AS child WHERE child.`requested_user_id` = OLD.`id` AND NOT ((((child.`department_id` IS NOT NULL) OR (NULL IS NOT NULL))))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Referenced constraint: help_requests_check'; END IF;
END$$

CREATE TRIGGER `astra_departments_delete_checks` BEFORE DELETE ON `departments` FOR EACH ROW BEGIN
  IF EXISTS (SELECT 1 FROM `change_approvals` AS child WHERE child.`department_id` = OLD.`id` AND NOT ((((child.`approver_user_id` IS NOT NULL) OR (NULL IS NOT NULL))))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Referenced constraint: change_approvals_check'; END IF;
  IF EXISTS (SELECT 1 FROM `help_requests` AS child WHERE child.`department_id` = OLD.`id` AND NOT ((((NULL IS NOT NULL) OR (child.`requested_user_id` IS NOT NULL))))) THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Referenced constraint: help_requests_check'; END IF;
END$$

DELIMITER ;

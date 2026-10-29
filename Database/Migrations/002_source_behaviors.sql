-- Apply after restoring source rows, so reservations are copied without duplication.
-- These behaviors come from the four triggers captured in the source snapshot.
DELIMITER $$
CREATE TRIGGER astra_clients_reserve_prefix_insert BEFORE INSERT ON clients
FOR EACH ROW PRECEDES astra_clients_insert_checks
BEGIN
  SET NEW.ticket_prefix = UPPER(TRIM(NEW.ticket_prefix));
  INSERT INTO ticket_prefix_registry(prefix, owner_kind, owner_id)
    VALUES(NEW.ticket_prefix, 'CLIENT', NEW.id)
    ON DUPLICATE KEY UPDATE prefix = ticket_prefix_registry.prefix;
  IF NOT EXISTS(SELECT 1 FROM ticket_prefix_registry WHERE prefix = NEW.ticket_prefix AND owner_kind = 'CLIENT' AND owner_id = NEW.id) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Ticket prefix is already reserved';
  END IF;
END$$
CREATE TRIGGER astra_clients_reserve_prefix_update BEFORE UPDATE ON clients
FOR EACH ROW PRECEDES astra_clients_update_checks
BEGIN
  IF NOT (NEW.ticket_prefix <=> OLD.ticket_prefix) THEN
    SET NEW.ticket_prefix = UPPER(TRIM(NEW.ticket_prefix));
    INSERT INTO ticket_prefix_registry(prefix, owner_kind, owner_id)
      VALUES(NEW.ticket_prefix, 'CLIENT', NEW.id)
      ON DUPLICATE KEY UPDATE prefix = ticket_prefix_registry.prefix;
    IF NOT EXISTS(SELECT 1 FROM ticket_prefix_registry WHERE prefix = NEW.ticket_prefix AND owner_kind = 'CLIENT' AND owner_id = NEW.id) THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Ticket prefix is already reserved';
    END IF;
  END IF;
END$$
CREATE TRIGGER astra_sites_reserve_prefix_insert BEFORE INSERT ON sites
FOR EACH ROW PRECEDES astra_sites_insert_checks
BEGIN
  DECLARE base VARCHAR(255);
  DECLARE candidate VARCHAR(255);
  DECLARE suffix BIGINT DEFAULT 1;
  IF NEW.ticket_prefix IS NULL OR TRIM(NEW.ticket_prefix) = '' THEN
    -- The parent row lock serializes allocation for each customer until commit.
    SELECT ticket_prefix INTO base FROM clients WHERE id = NEW.client_id FOR UPDATE;
    IF base IS NULL THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Customer is required'; END IF;
    reserve_loop: LOOP
      SET candidate = CONCAT(base, 'S', CASE WHEN suffix < 1000 THEN LPAD(CAST(suffix AS CHAR), 3, '0') ELSE CAST(suffix AS CHAR) END);
      IF CHAR_LENGTH(candidate) > 16 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Choose a site ticket prefix'; END IF;
      INSERT INTO ticket_prefix_registry(prefix, owner_kind, owner_id)
        VALUES(candidate, 'SITE', NEW.id)
        ON DUPLICATE KEY UPDATE prefix = ticket_prefix_registry.prefix;
      IF EXISTS(SELECT 1 FROM ticket_prefix_registry WHERE prefix = candidate AND owner_kind = 'SITE' AND owner_id = NEW.id) THEN
        SET NEW.ticket_prefix = candidate;
        LEAVE reserve_loop;
      END IF;
      SET suffix = suffix + 1;
    END LOOP;
  END IF;
  SET NEW.ticket_prefix = UPPER(TRIM(NEW.ticket_prefix));
  INSERT INTO ticket_prefix_registry(prefix, owner_kind, owner_id)
    VALUES(NEW.ticket_prefix, 'SITE', NEW.id)
    ON DUPLICATE KEY UPDATE prefix = ticket_prefix_registry.prefix;
  IF NOT EXISTS(SELECT 1 FROM ticket_prefix_registry WHERE prefix = NEW.ticket_prefix AND owner_kind = 'SITE' AND owner_id = NEW.id) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Ticket prefix is already reserved';
  END IF;
END$$
CREATE TRIGGER astra_sites_reserve_prefix_update BEFORE UPDATE ON sites
FOR EACH ROW PRECEDES astra_sites_update_checks
BEGIN
  DECLARE base VARCHAR(255);
  DECLARE candidate VARCHAR(255);
  DECLARE suffix BIGINT DEFAULT 1;
  IF NOT (NEW.ticket_prefix <=> OLD.ticket_prefix) THEN
    IF NEW.ticket_prefix IS NULL OR TRIM(NEW.ticket_prefix) = '' THEN
      SELECT ticket_prefix INTO base FROM clients WHERE id = NEW.client_id FOR UPDATE;
      IF base IS NULL THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Customer is required'; END IF;
      reserve_loop: LOOP
        SET candidate = CONCAT(base, 'S', CASE WHEN suffix < 1000 THEN LPAD(CAST(suffix AS CHAR), 3, '0') ELSE CAST(suffix AS CHAR) END);
        IF CHAR_LENGTH(candidate) > 16 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Choose a site ticket prefix'; END IF;
        INSERT INTO ticket_prefix_registry(prefix, owner_kind, owner_id)
          VALUES(candidate, 'SITE', NEW.id)
          ON DUPLICATE KEY UPDATE prefix = ticket_prefix_registry.prefix;
        IF EXISTS(SELECT 1 FROM ticket_prefix_registry WHERE prefix = candidate AND owner_kind = 'SITE' AND owner_id = NEW.id) THEN
          SET NEW.ticket_prefix = candidate;
          LEAVE reserve_loop;
        END IF;
        SET suffix = suffix + 1;
      END LOOP;
    END IF;
    SET NEW.ticket_prefix = UPPER(TRIM(NEW.ticket_prefix));
    INSERT INTO ticket_prefix_registry(prefix, owner_kind, owner_id)
      VALUES(NEW.ticket_prefix, 'SITE', NEW.id)
      ON DUPLICATE KEY UPDATE prefix = ticket_prefix_registry.prefix;
    IF NOT EXISTS(SELECT 1 FROM ticket_prefix_registry WHERE prefix = NEW.ticket_prefix AND owner_kind = 'SITE' AND owner_id = NEW.id) THEN
      SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Ticket prefix is already reserved';
    END IF;
  END IF;
END$$
CREATE TRIGGER astra_change_history_immutable_update BEFORE UPDATE ON change_history
FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Change history is immutable'$$
CREATE TRIGGER astra_change_history_immutable_delete BEFORE DELETE ON change_history
FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Change history is immutable'$$
CREATE TRIGGER astra_change_reviews_immutable_update BEFORE UPDATE ON change_reviews
FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Change history is immutable'$$
CREATE TRIGGER astra_change_reviews_immutable_delete BEFORE DELETE ON change_reviews
FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Change history is immutable'$$
-- InnoDB cascades bypass child triggers; retain the source rejection on parents.
CREATE TRIGGER astra_changes_immutable_history_guard BEFORE DELETE ON changes
FOR EACH ROW
BEGIN
  IF EXISTS(SELECT 1 FROM change_history WHERE change_id = OLD.id) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Change history is immutable';
  END IF;
END$$
CREATE TRIGGER astra_users_immutable_history_guard BEFORE DELETE ON users
FOR EACH ROW
BEGIN
  IF EXISTS(SELECT 1 FROM change_history WHERE actor_user_id = OLD.id) THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Change history is immutable';
  END IF;
END$$
DELIMITER ;

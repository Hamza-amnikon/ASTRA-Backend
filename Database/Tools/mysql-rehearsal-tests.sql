SET NAMES utf8mb4 COLLATE utf8mb4_0900_as_cs;
SET SESSION time_zone = '+00:00';
DELIMITER $$
CREATE PROCEDURE astra_assert(IN test_name VARCHAR(200), IN actual BOOLEAN)
BEGIN
  IF actual IS NULL OR actual = FALSE THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = test_name;
  END IF;
END$$
CREATE PROCEDURE astra_expect_error(IN candidate TEXT, IN expected_error INT)
BEGIN
  DECLARE actual_error INT DEFAULT 0;
  SET @astra_test_sql = candidate;
  PREPARE astra_test_statement FROM @astra_test_sql;
  BEGIN
    DECLARE CONTINUE HANDLER FOR SQLEXCEPTION
      GET DIAGNOSTICS CONDITION 1 actual_error = MYSQL_ERRNO;
    EXECUTE astra_test_statement;
  END;
  DEALLOCATE PREPARE astra_test_statement;
  IF actual_error <> expected_error THEN
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT = 'Expected database constraint rejection did not occur';
  END IF;
END$$
DELIMITER ;
START TRANSACTION;
INSERT INTO clients(id, name, ticket_prefix) VALUES
 ('00000000-0000-0000-0000-000000000001', 'Rehearsal customer A', 'RA'),
 ('00000000-0000-0000-0000-000000000002', 'Rehearsal customer B', 'RB');
INSERT INTO users(id, entra_object_id, entra_tenant_id, email, display_name, created_at) VALUES
 ('00000000-0000-0000-0000-000000000011', '10000000-0000-0000-0000-000000000011', '20000000-0000-0000-0000-000000000001', 'rehearsal@example.invalid', 'परीक्षण 😀', '2026-10-28 10:00:00.123456'),
 ('00000000-0000-0000-0000-000000000012', '10000000-0000-0000-0000-000000000012', '20000000-0000-0000-0000-000000000001', 'approver@example.invalid', 'Synthetic approver', '2026-10-28 10:00:00.123456');
CALL astra_assert('Unicode text was changed', (SELECT display_name = 'परीक्षण 😀' FROM users WHERE id = '00000000-0000-0000-0000-000000000011'));
CALL astra_assert('Timestamp microseconds were lost', (SELECT DATE_FORMAT(created_at, '%f') = '123456' FROM users WHERE id = '00000000-0000-0000-0000-000000000011'));
CALL astra_expect_error('UPDATE users SET active = 2 WHERE id = ''00000000-0000-0000-0000-000000000011''', 1644);
CALL astra_expect_error('UPDATE users SET email = ''REHEARSAL@example.invalid'' WHERE id = ''00000000-0000-0000-0000-000000000012''', 1062);
INSERT INTO categories(id, name) VALUES ('00000000-0000-0000-0000-000000000021', 'Synthetic category');
CALL astra_expect_error('INSERT INTO categories(name) VALUES (''SYNTHETIC CATEGORY'')', 1062);
INSERT INTO contacts(id, client_id, name) VALUES ('00000000-0000-0000-0000-000000000031', '00000000-0000-0000-0000-000000000001', 'Synthetic requester');
INSERT INTO ticket_sources(code, name) VALUES ('MANUAL', 'Synthetic source');
INSERT INTO tickets(id, ticket_number, client_id, requester_id, category_id, subject, description, source)
VALUES ('00000000-0000-0000-0000-000000000041', 'REHEARSAL-1', '00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000031', '00000000-0000-0000-0000-000000000021', 'Synthetic ticket', 'Synthetic description', 'MANUAL');
CALL astra_expect_error('UPDATE tickets SET client_id = ''00000000-0000-0000-0000-000000000002'' WHERE id = ''00000000-0000-0000-0000-000000000041''', 1452);
CALL astra_expect_error('UPDATE tickets SET status = ''open'' WHERE id = ''00000000-0000-0000-0000-000000000041''', 1644);
CALL astra_expect_error('DELETE FROM categories WHERE id = ''00000000-0000-0000-0000-000000000021''', 1451);
INSERT INTO ticket_approvals(ticket_id, requested_by_user_id, reason) VALUES ('00000000-0000-0000-0000-000000000041', '00000000-0000-0000-0000-000000000011', 'Synthetic approval');
CALL astra_expect_error('INSERT INTO ticket_approvals(ticket_id, requested_by_user_id, reason) VALUES (''00000000-0000-0000-0000-000000000041'', ''00000000-0000-0000-0000-000000000011'', ''Second pending approval'')', 1062);
CALL astra_expect_error('INSERT INTO access_groups(name, kind, client_id, permissions) VALUES (''Invalid customer group'', ''CUSTOMER'', ''00000000-0000-0000-0000-000000000001'', JSON_ARRAY(''TICKETS_ASSIGN''))', 1644);
CALL astra_expect_error('INSERT INTO access_groups(name, kind, client_id, permissions) VALUES (''Invalid reply group'', ''CUSTOMER'', ''00000000-0000-0000-0000-000000000001'', JSON_ARRAY(''PORTAL_ORGANIZATION_REPLY''))', 1644);
INSERT INTO access_groups(name, kind, client_id, permissions) VALUES ('Valid synthetic group', 'CUSTOMER', '00000000-0000-0000-0000-000000000001', JSON_ARRAY('PORTAL_ORGANIZATION_VIEW', 'PORTAL_ORGANIZATION_REPLY'));
INSERT INTO roles(code, name, description) VALUES ('TECHNICIAN', 'Synthetic technician', 'Rehearsal only');
CALL astra_expect_error('INSERT INTO user_access_policies(user_id, allow_permissions, deny_permissions) VALUES (''00000000-0000-0000-0000-000000000011'', JSON_ARRAY(''TICKETS_ASSIGN''), JSON_ARRAY(''TICKETS_ASSIGN''))', 1644);
INSERT INTO ticket_attachments(ticket_id, uploaded_by_user_id, file_name, content_type, size_bytes, storage_key, sha256, content)
VALUES ('00000000-0000-0000-0000-000000000041', '00000000-0000-0000-0000-000000000011', 'synthetic.bin', 'application/octet-stream', 3, 'rehearsal/synthetic.bin', SHA2(UNHEX('0001ff'), 256), UNHEX('0001ff'));
CALL astra_assert('Binary attachment bytes changed', (SELECT HEX(content) = '0001FF' FROM ticket_attachments WHERE storage_key = 'rehearsal/synthetic.bin'));
INSERT INTO changes(id, change_number, client_id, title, description, category, change_type, risk, impact, reason, business_justification, implementation_plan, test_plan, rollback_plan, owner_user_id, requested_by_user_id)
VALUES ('00000000-0000-0000-0000-000000000051', 'REHEARSAL-CHG-1', '00000000-0000-0000-0000-000000000001', 'Synthetic change', 'Synthetic description', 'OTHER', 'NORMAL', 'LOW', 'Synthetic impact', 'Synthetic reason', 'Synthetic justification', 'Synthetic implementation', 'Synthetic validation', 'Synthetic rollback', '00000000-0000-0000-0000-000000000011', '00000000-0000-0000-0000-000000000011');
INSERT INTO change_approvals(change_id, approver_user_id) VALUES ('00000000-0000-0000-0000-000000000051', '00000000-0000-0000-0000-000000000012');
CALL astra_expect_error('DELETE FROM users WHERE id = ''00000000-0000-0000-0000-000000000012''', 1644);
CALL astra_assert('Customer prefix reservation is missing', (SELECT COUNT(*) = 1 FROM ticket_prefix_registry WHERE prefix = 'RA' AND owner_kind = 'CLIENT' AND owner_id = '00000000-0000-0000-0000-000000000001'));
INSERT INTO sites(id, client_id, name) VALUES
 ('00000000-0000-0000-0000-000000000061', '00000000-0000-0000-0000-000000000001', 'Synthetic site one'),
 ('00000000-0000-0000-0000-000000000062', '00000000-0000-0000-0000-000000000001', 'Synthetic site two');
CALL astra_assert('First site prefix was not allocated', (SELECT ticket_prefix = 'RAS001' FROM sites WHERE id = '00000000-0000-0000-0000-000000000061'));
CALL astra_assert('Site prefix allocation reused a number', (SELECT ticket_prefix = 'RAS002' FROM sites WHERE id = '00000000-0000-0000-0000-000000000062'));
CALL astra_expect_error('INSERT INTO sites(client_id, name, ticket_prefix) VALUES (''00000000-0000-0000-0000-000000000001'', ''Reserved-prefix site'', ''RA'')', 1644);
CALL astra_expect_error('UPDATE clients SET ticket_prefix = ''RB'' WHERE id = ''00000000-0000-0000-0000-000000000001''', 1644);
UPDATE clients SET ticket_prefix = 'rc' WHERE id = '00000000-0000-0000-0000-000000000001';
CALL astra_assert('Customer prefixes must be normalized', (SELECT ticket_prefix = 'RC' FROM clients WHERE id = '00000000-0000-0000-0000-000000000001'));
CALL astra_assert('Old customer prefixes must remain reserved', (SELECT COUNT(*) = 1 FROM ticket_prefix_registry WHERE prefix = 'RA'));
INSERT INTO change_history(change_id, actor_user_id, event) VALUES ('00000000-0000-0000-0000-000000000051', '00000000-0000-0000-0000-000000000012', 'SYNTHETIC.EVENT');
CALL astra_expect_error('UPDATE change_history SET event = ''ALTERED'' WHERE change_id = ''00000000-0000-0000-0000-000000000051''', 1644);
CALL astra_expect_error('DELETE FROM change_history WHERE change_id = ''00000000-0000-0000-0000-000000000051''', 1644);
CALL astra_expect_error('DELETE FROM changes WHERE id = ''00000000-0000-0000-0000-000000000051''', 1644);
CALL astra_expect_error('DELETE FROM users WHERE id = ''00000000-0000-0000-0000-000000000012''', 1644);
INSERT INTO change_reviews(change_id, revision, outcome, summary, follow_up, actor_user_id) VALUES ('00000000-0000-0000-0000-000000000051', 1, 'SUCCESS', 'Synthetic review', 'Synthetic follow-up', '00000000-0000-0000-0000-000000000011');
CALL astra_expect_error('UPDATE change_reviews SET summary = ''ALTERED'' WHERE change_id = ''00000000-0000-0000-0000-000000000051''', 1644);
CALL astra_expect_error('DELETE FROM change_reviews WHERE change_id = ''00000000-0000-0000-0000-000000000051''', 1644);
ROLLBACK;
SELECT 'Synthetic tests passed: Unicode, timestamp precision, boolean validation, case-insensitive uniqueness, client relationships, status validation, conditional uniqueness, permissions, attachments, and parent deletion guards.' AS result;

-- Run as the MySQL administrator after 001_mysql_schema.sql.
-- Named timezone tables must be loaded with mysql_tzinfo_to_sql first.
USE astra_ticketing;
DELIMITER $$

CREATE FUNCTION sla_business_minutes_between(p_calendar CHAR(36), p_start DATETIME(6), p_end DATETIME(6))
RETURNS BIGINT READS SQL DATA SQL SECURITY INVOKER
BEGIN
    DECLARE v_zone VARCHAR(80);
    DECLARE v_days JSON;
    DECLARE v_open TIME;
    DECLARE v_close TIME;
    DECLARE v_day DATE;
    DECLARE v_last DATE;
    DECLARE v_begin DATETIME(6);
    DECLARE v_finish DATETIME(6);
    DECLARE v_total BIGINT DEFAULT 0;
    DECLARE v_guard INT DEFAULT 0;
    IF p_start IS NULL OR p_end IS NULL THEN RETURN NULL; END IF;
    IF p_end <= p_start THEN RETURN 0; END IF;
    SELECT timezone,working_days,work_start,work_end INTO v_zone,v_days,v_open,v_close
        FROM sla_calendars WHERE id=p_calendar;
    IF v_zone IS NULL OR CONVERT_TZ(p_start,'+00:00',v_zone) IS NULL THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='SLA calendar/timezone is unavailable';
    END IF;
    SET v_day=DATE(CONVERT_TZ(p_start,'+00:00',v_zone));
    SET v_last=DATE(CONVERT_TZ(p_end,'+00:00',v_zone));
    WHILE v_day<=v_last DO
        IF v_guard>=3700 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='SLA interval is too large'; END IF;
        IF JSON_CONTAINS(v_days,CAST(WEEKDAY(v_day)+1 AS JSON))
            AND NOT EXISTS(SELECT 1 FROM sla_holidays WHERE calendar_id=p_calendar AND holiday_date=v_day) THEN
            SET v_begin=GREATEST(p_start,CONVERT_TZ(TIMESTAMP(v_day,v_open),v_zone,'+00:00'));
            SET v_finish=LEAST(p_end,CONVERT_TZ(TIMESTAMP(v_day,v_close),v_zone,'+00:00'));
            IF v_finish>v_begin THEN SET v_total=v_total+TIMESTAMPDIFF(MINUTE,v_begin,v_finish); END IF;
        END IF;
        SET v_day=DATE_ADD(v_day,INTERVAL 1 DAY);
        SET v_guard=v_guard+1;
    END WHILE;
    RETURN v_total;
END$$

CREATE FUNCTION sla_add_business_minutes(p_calendar CHAR(36), p_start DATETIME(6), p_minutes INT)
RETURNS DATETIME(6) READS SQL DATA SQL SECURITY INVOKER
BEGIN
    DECLARE v_zone VARCHAR(80);
    DECLARE v_days JSON;
    DECLARE v_open TIME;
    DECLARE v_close TIME;
    DECLARE v_day DATE;
    DECLARE v_cursor DATETIME(6);
    DECLARE v_begin DATETIME(6);
    DECLARE v_finish DATETIME(6);
    DECLARE v_available INT;
    DECLARE v_remaining INT;
    DECLARE v_guard INT DEFAULT 0;
    IF p_start IS NULL OR p_minutes IS NULL THEN RETURN NULL; END IF;
    IF p_minutes<=0 THEN RETURN p_start; END IF;
    SELECT timezone,working_days,work_start,work_end INTO v_zone,v_days,v_open,v_close
        FROM sla_calendars WHERE id=p_calendar;
    IF v_zone IS NULL OR CONVERT_TZ(p_start,'+00:00',v_zone) IS NULL THEN
        SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='SLA calendar/timezone is unavailable';
    END IF;
    SET v_cursor=p_start;
    SET v_remaining=p_minutes;
    WHILE v_guard<3700 DO
        SET v_day=DATE(CONVERT_TZ(v_cursor,'+00:00',v_zone));
        IF JSON_CONTAINS(v_days,CAST(WEEKDAY(v_day)+1 AS JSON))
            AND NOT EXISTS(SELECT 1 FROM sla_holidays WHERE calendar_id=p_calendar AND holiday_date=v_day) THEN
            SET v_begin=CONVERT_TZ(TIMESTAMP(v_day,v_open),v_zone,'+00:00');
            SET v_finish=CONVERT_TZ(TIMESTAMP(v_day,v_close),v_zone,'+00:00');
            SET v_cursor=GREATEST(v_cursor,v_begin);
            IF v_cursor<v_finish THEN
                SET v_available=TIMESTAMPDIFF(MINUTE,v_cursor,v_finish);
                IF v_remaining<=v_available THEN RETURN DATE_ADD(v_cursor,INTERVAL v_remaining MINUTE); END IF;
                SET v_remaining=v_remaining-v_available;
            END IF;
        END IF;
        SET v_cursor=CONVERT_TZ(TIMESTAMP(DATE_ADD(v_day,INTERVAL 1 DAY),'00:00:00'),v_zone,'+00:00');
        SET v_guard=v_guard+1;
    END WHILE;
    SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='SLA calendar cannot satisfy target';
END$$
DELIMITER ;

-- Keep routines callable without granting DDL to the application account.
GRANT EXECUTE ON FUNCTION astra_ticketing.sla_business_minutes_between TO 'astra_app'@'172.31.3.13';
GRANT EXECUTE ON FUNCTION astra_ticketing.sla_add_business_minutes TO 'astra_app'@'172.31.3.13';

UPDATE app_schema_state SET version=24 WHERE id=1;

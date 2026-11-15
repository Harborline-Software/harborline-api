BEGIN IMMEDIATE;
CREATE TABLE IF NOT EXISTS event_positions (
    position INTEGER PRIMARY KEY AUTOINCREMENT,
    event_id TEXT NOT NULL UNIQUE REFERENCES domain_events(event_id)
);
INSERT OR IGNORE INTO event_positions(event_id)
    SELECT event_id FROM domain_events ORDER BY event_id;
CREATE TRIGGER IF NOT EXISTS domain_events_assign_position AFTER INSERT ON domain_events
BEGIN
    INSERT INTO event_positions(event_id) VALUES (NEW.event_id);
END;
CREATE TABLE IF NOT EXISTS event_handler_positions (
    handler_id TEXT NOT NULL,
    tenant_id TEXT NOT NULL,
    position INTEGER NOT NULL,
    PRIMARY KEY (handler_id, tenant_id)
);
INSERT OR IGNORE INTO event_handler_positions(handler_id, tenant_id, position)
    SELECT c.handler_id, c.tenant_id, p.position
    FROM event_handler_cursors c JOIN event_positions p ON p.event_id = c.last_handled_event_id;
COMMIT;

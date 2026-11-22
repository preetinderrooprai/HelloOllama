PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS sites (
    site_id INTEGER PRIMARY KEY,
    site_name TEXT NOT NULL UNIQUE,
    city TEXT NOT NULL,
    timezone TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS cardholders (
    cardholder_id INTEGER PRIMARY KEY,
    employee_number TEXT NOT NULL UNIQUE,
    full_name TEXT NOT NULL,
    department TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('ACTIVE', 'INACTIVE'))
);

CREATE TABLE IF NOT EXISTS cards (
    card_id INTEGER PRIMARY KEY,
    cardholder_id INTEGER NOT NULL REFERENCES cardholders(cardholder_id),
    card_number TEXT NOT NULL UNIQUE,
    card_type TEXT NOT NULL,
    issued_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('ACTIVE', 'EXPIRED', 'DISABLED'))
);

CREATE TABLE IF NOT EXISTS doors (
    door_id INTEGER PRIMARY KEY,
    site_id INTEGER NOT NULL REFERENCES sites(site_id),
    door_name TEXT NOT NULL,
    door_type TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('ONLINE', 'OFFLINE'))
);

CREATE TABLE IF NOT EXISTS readers (
    reader_id INTEGER PRIMARY KEY,
    door_id INTEGER NOT NULL REFERENCES doors(door_id),
    reader_name TEXT NOT NULL,
    reader_type TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('ONLINE', 'OFFLINE'))
);

CREATE TABLE IF NOT EXISTS swipes (
    swipe_id INTEGER PRIMARY KEY,
    card_id INTEGER NOT NULL REFERENCES cards(card_id),
    reader_id INTEGER NOT NULL REFERENCES readers(reader_id),
    swipe_time_utc TEXT NOT NULL,
    access_result TEXT NOT NULL CHECK (access_result IN ('GRANTED', 'DENIED')),
    direction TEXT NOT NULL CHECK (direction IN ('IN', 'OUT', 'ACCESS')),
    denial_reason TEXT
);

CREATE TABLE IF NOT EXISTS door_activities (
    activity_id INTEGER PRIMARY KEY,
    door_id INTEGER NOT NULL REFERENCES doors(door_id),
    event_time_utc TEXT NOT NULL,
    event_type TEXT NOT NULL,
    severity TEXT NOT NULL CHECK (severity IN ('INFO', 'WARNING', 'ALARM')),
    details TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS cardholder_activities (
    activity_id INTEGER PRIMARY KEY,
    cardholder_id INTEGER NOT NULL REFERENCES cardholders(cardholder_id),
    event_time_utc TEXT NOT NULL,
    event_type TEXT NOT NULL,
    details TEXT NOT NULL,
    performed_by TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS attendance_records (
    attendance_id INTEGER PRIMARY KEY,
    cardholder_id INTEGER NOT NULL REFERENCES cardholders(cardholder_id),
    site_id INTEGER NOT NULL REFERENCES sites(site_id),
    attendance_date TEXT NOT NULL,
    first_entry_utc TEXT,
    last_exit_utc TEXT,
    minutes_on_site INTEGER NOT NULL CHECK (minutes_on_site >= 0),
    attendance_status TEXT NOT NULL CHECK (attendance_status IN ('PRESENT', 'PARTIAL', 'ABSENT')),
    UNIQUE (cardholder_id, site_id, attendance_date)
);

INSERT OR IGNORE INTO sites (site_id, site_name, city, timezone) VALUES
    (1, 'North Campus', 'Seattle', 'America/Los_Angeles'),
    (2, 'Downtown Office', 'Chicago', 'America/Chicago'),
    (3, 'Distribution Center', 'Denver', 'America/Denver');

INSERT OR IGNORE INTO cardholders (cardholder_id, employee_number, full_name, department, status) VALUES
    (1, 'E-1001', 'Avery Chen', 'Engineering', 'ACTIVE'),
    (2, 'E-1002', 'Jordan Reed', 'Operations', 'ACTIVE'),
    (3, 'E-1003', 'Riley Patel', 'Finance', 'ACTIVE'),
    (4, 'E-1004', 'Casey Morgan', 'Security', 'ACTIVE'),
    (5, 'E-1005', 'Taylor Diaz', 'Operations', 'INACTIVE');

INSERT OR IGNORE INTO cards (card_id, cardholder_id, card_number, card_type, issued_at, expires_at, status) VALUES
    (1, 1, 'AC-10001', 'EMPLOYEE', '2025-01-15', '2027-01-15', 'ACTIVE'),
    (2, 2, 'AC-10002', 'EMPLOYEE', '2025-02-01', '2027-02-01', 'ACTIVE'),
    (3, 3, 'AC-10003', 'EMPLOYEE', '2025-03-10', '2027-03-10', 'ACTIVE'),
    (4, 4, 'AC-10004', 'SECURITY', '2025-01-05', '2027-01-05', 'ACTIVE'),
    (5, 5, 'AC-10005', 'EMPLOYEE', '2024-01-01', '2025-01-01', 'EXPIRED');

INSERT OR IGNORE INTO doors (door_id, site_id, door_name, door_type, status) VALUES
    (1, 1, 'North Main Entrance', 'ENTRANCE', 'ONLINE'),
    (2, 1, 'Server Room', 'RESTRICTED', 'ONLINE'),
    (3, 2, 'Downtown Lobby', 'ENTRANCE', 'ONLINE'),
    (4, 3, 'Loading Bay', 'SERVICE', 'OFFLINE');

INSERT OR IGNORE INTO readers (reader_id, door_id, reader_name, reader_type, status) VALUES
    (1, 1, 'North Lobby In', 'CARD_READER', 'ONLINE'),
    (2, 1, 'North Lobby Out', 'CARD_READER', 'ONLINE'),
    (3, 2, 'Server Room Reader', 'CARD_READER', 'ONLINE'),
    (4, 3, 'Downtown Lobby Reader', 'CARD_READER', 'ONLINE'),
    (5, 4, 'Loading Bay Reader', 'CARD_READER', 'OFFLINE');

INSERT OR IGNORE INTO swipes
    (swipe_id, card_id, reader_id, swipe_time_utc, access_result, direction, denial_reason) VALUES
    (1, 1, 1, '2026-11-16T08:02:00Z', 'GRANTED', 'IN', NULL),
    (2, 1, 3, '2026-11-16T09:14:00Z', 'GRANTED', 'ACCESS', NULL),
    (3, 1, 2, '2026-11-16T17:08:00Z', 'GRANTED', 'OUT', NULL),
    (4, 2, 1, '2026-11-16T08:18:00Z', 'GRANTED', 'IN', NULL),
    (5, 2, 3, '2026-11-16T08:45:00Z', 'DENIED', 'ACCESS', 'INSUFFICIENT_PRIVILEGE'),
    (6, 2, 2, '2026-11-16T16:51:00Z', 'GRANTED', 'OUT', NULL),
    (7, 3, 4, '2026-11-16T08:40:00Z', 'GRANTED', 'IN', NULL),
    (8, 3, 4, '2026-11-16T17:10:00Z', 'GRANTED', 'OUT', NULL),
    (9, 4, 1, '2026-11-16T07:28:00Z', 'GRANTED', 'IN', NULL),
    (10, 4, 2, '2026-11-16T18:02:00Z', 'GRANTED', 'OUT', NULL),
    (11, 5, 1, '2026-11-16T08:11:00Z', 'DENIED', 'IN', 'CARD_EXPIRED'),
    (12, 1, 1, '2026-11-17T08:05:00Z', 'GRANTED', 'IN', NULL),
    (13, 1, 2, '2026-11-17T17:12:00Z', 'GRANTED', 'OUT', NULL),
    (14, 3, 4, '2026-11-17T08:39:00Z', 'GRANTED', 'IN', NULL),
    (15, 3, 4, '2026-11-17T17:02:00Z', 'GRANTED', 'OUT', NULL),
    (16, 2, 1, '2026-11-18T08:12:00Z', 'GRANTED', 'IN', NULL),
    (17, 2, 2, '2026-11-18T16:48:00Z', 'GRANTED', 'OUT', NULL),
    (18, 4, 3, '2026-11-18T09:00:00Z', 'GRANTED', 'ACCESS', NULL);

INSERT OR IGNORE INTO door_activities
    (activity_id, door_id, event_time_utc, event_type, severity, details) VALUES
    (1, 2, '2026-11-16T12:15:00Z', 'DOOR_HELD_OPEN', 'WARNING', 'Door held open for longer than the configured interval'),
    (2, 4, '2026-11-17T06:30:00Z', 'READER_OFFLINE', 'ALARM', 'Reader stopped reporting to the access system'),
    (3, 1, '2026-11-18T07:00:00Z', 'DOOR_NORMAL', 'INFO', 'Scheduled status check completed');

INSERT OR IGNORE INTO cardholder_activities
    (activity_id, cardholder_id, event_time_utc, event_type, details, performed_by) VALUES
    (1, 1, '2026-11-10T15:00:00Z', 'CARD_REPLACED', 'Replacement badge issued', 'Facilities Admin'),
    (2, 2, '2026-11-12T10:20:00Z', 'ACCESS_UPDATED', 'Server room access removed', 'Security Admin'),
    (3, 3, '2026-11-14T09:45:00Z', 'PROFILE_UPDATED', 'Department information confirmed', 'HR Admin'),
    (4, 5, '2026-11-15T17:30:00Z', 'CARD_DISABLED', 'Employment status changed to inactive', 'HR Admin');

INSERT OR IGNORE INTO attendance_records
    (attendance_id, cardholder_id, site_id, attendance_date, first_entry_utc, last_exit_utc, minutes_on_site, attendance_status) VALUES
    (1, 1, 1, '2026-11-16', '2026-11-16T08:02:00Z', '2026-11-16T17:08:00Z', 546, 'PRESENT'),
    (2, 2, 1, '2026-11-16', '2026-11-16T08:18:00Z', '2026-11-16T16:51:00Z', 513, 'PRESENT'),
    (3, 3, 2, '2026-11-16', '2026-11-16T08:40:00Z', '2026-11-16T17:10:00Z', 510, 'PRESENT'),
    (4, 4, 1, '2026-11-16', '2026-11-16T07:28:00Z', '2026-11-16T18:02:00Z', 634, 'PRESENT'),
    (5, 1, 1, '2026-11-17', '2026-11-17T08:05:00Z', '2026-11-17T17:12:00Z', 547, 'PRESENT'),
    (6, 3, 2, '2026-11-17', '2026-11-17T08:39:00Z', '2026-11-17T17:02:00Z', 503, 'PRESENT'),
    (7, 2, 1, '2026-11-18', '2026-11-18T08:12:00Z', '2026-11-18T16:48:00Z', 516, 'PRESENT');

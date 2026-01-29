PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS things (
  thing_id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  kind INTEGER NOT NULL,
  config_json TEXT NOT NULL,
  created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS runs (
  run_id TEXT PRIMARY KEY,
  thing_id TEXT NOT NULL,
  started_at TEXT NOT NULL,
  ended_at TEXT NULL,
  status INTEGER NOT NULL,
  exit_code INTEGER NULL,
  summary TEXT NULL,
  FOREIGN KEY (thing_id) REFERENCES things(thing_id)
);

-- Event log backbone
CREATE TABLE IF NOT EXISTS run_events (
  seq INTEGER PRIMARY KEY AUTOINCREMENT,
  run_id TEXT NOT NULL,
  at TEXT NOT NULL,
  kind INTEGER NOT NULL,
  payload_json TEXT NOT NULL,
  FOREIGN KEY (run_id) REFERENCES runs(run_id)
);

CREATE INDEX IF NOT EXISTS idx_run_events_run_id_seq ON run_events(run_id, seq);
CREATE INDEX IF NOT EXISTS idx_runs_started_at ON runs(started_at DESC);

CREATE TABLE IF NOT EXISTS artifacts (
  artifact_id TEXT PRIMARY KEY,
  run_id TEXT NOT NULL,
  media_type TEXT NOT NULL,
  locator TEXT NOT NULL,
  sha256_hex TEXT NULL,
  created_at TEXT NOT NULL,
  FOREIGN KEY (run_id) REFERENCES runs(run_id)
);

PRAGMA journal_mode=WAL;
PRAGMA temp_store=MEMORY;
BEGIN IMMEDIATE;
CREATE TABLE IF NOT EXISTS volumes (
 id TEXT PRIMARY KEY, mount TEXT NOT NULL, label TEXT NOT NULL,
 online INTEGER NOT NULL, enabled INTEGER NOT NULL DEFAULT 0,
 hours REAL NOT NULL DEFAULT 24, attempted INTEGER, succeeded INTEGER,
 outcome TEXT NOT NULL DEFAULT '', issues TEXT NOT NULL DEFAULT '',
 host TEXT NOT NULL DEFAULT '', removable INTEGER NOT NULL DEFAULT 0,
 note TEXT NOT NULL DEFAULT '', freshness_days INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS files (
 id INTEGER PRIMARY KEY, volume TEXT NOT NULL, rel TEXT NOT NULL, name TEXT NOT NULL,
 name_key TEXT NOT NULL, parent_key TEXT NOT NULL, extension TEXT NOT NULL,
 bytes INTEGER NOT NULL, modified INTEGER NOT NULL, created INTEGER NOT NULL,
 attributes INTEGER NOT NULL, seen INTEGER NOT NULL, stale INTEGER NOT NULL DEFAULT 0,
 file_key TEXT, removed INTEGER, UNIQUE(volume, rel)
);
CREATE INDEX IF NOT EXISTS files_name ON files(name_key);
CREATE INDEX IF NOT EXISTS files_volume_identity ON files(volume,file_key);
CREATE INDEX IF NOT EXISTS files_extension ON files(extension);
CREATE INDEX IF NOT EXISTS files_size ON files(bytes);
CREATE INDEX IF NOT EXISTS files_modified ON files(modified);
CREATE INDEX IF NOT EXISTS files_copy_candidates ON files(bytes,modified,created);
CREATE TABLE IF NOT EXISTS folder_stats (
 volume TEXT NOT NULL, rel TEXT NOT NULL, subtree_bytes INTEGER NOT NULL,
 subtree_files INTEGER NOT NULL, subtree_dirs INTEGER NOT NULL, PRIMARY KEY(volume,rel)
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS folder_stats_size ON folder_stats(volume,subtree_bytes DESC);
CREATE TABLE IF NOT EXISTS file_annotations (
 volume TEXT NOT NULL, rel TEXT NOT NULL, tags TEXT NOT NULL DEFAULT '',
 note TEXT NOT NULL DEFAULT '', updated INTEGER NOT NULL, PRIMARY KEY(volume,rel)
) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS file_tags (
 tag TEXT NOT NULL, volume TEXT NOT NULL, rel TEXT NOT NULL, PRIMARY KEY(tag,volume,rel)
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS file_tags_path ON file_tags(volume,rel);
CREATE TABLE IF NOT EXISTS file_hashes (
 file_id INTEGER PRIMARY KEY, bytes INTEGER NOT NULL, modified INTEGER NOT NULL,
 quick_hash BLOB, full_hash BLOB, hashed_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS prefixes (
 token TEXT NOT NULL, file_id INTEGER NOT NULL, PRIMARY KEY(token,file_id)
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS prefixes_file ON prefixes(file_id);
CREATE VIRTUAL TABLE IF NOT EXISTS fragments USING fts5(
 name_key, parent_key, content='files', content_rowid='id', tokenize='trigram case_sensitive 1'
);
CREATE TRIGGER IF NOT EXISTS files_insert AFTER INSERT ON files BEGIN
 INSERT INTO fragments(rowid,name_key,parent_key) VALUES(new.id,new.name_key,new.parent_key);
END;
DROP TRIGGER IF EXISTS files_delete;
CREATE TRIGGER files_delete AFTER DELETE ON files BEGIN
 INSERT INTO fragments(fragments,rowid,name_key,parent_key) VALUES('delete',old.id,old.name_key,old.parent_key);
 DELETE FROM prefixes WHERE file_id=old.id;
 DELETE FROM file_hashes WHERE file_id=old.id;
END;
CREATE TABLE IF NOT EXISTS scans (
 id INTEGER PRIMARY KEY, volume TEXT NOT NULL, started INTEGER NOT NULL, finished INTEGER,
 outcome TEXT NOT NULL, items INTEGER NOT NULL DEFAULT 0, issues TEXT NOT NULL DEFAULT '',
 identity_ms INTEGER NOT NULL DEFAULT 0,
 added_count INTEGER NOT NULL DEFAULT 0, modified_count INTEGER NOT NULL DEFAULT 0,
 removed_count INTEGER NOT NULL DEFAULT 0, moved_count INTEGER NOT NULL DEFAULT 0,
 bytes_delta INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS scans_volume ON scans(volume,id DESC);
CREATE TABLE IF NOT EXISTS scan_changes (
 scan_id INTEGER NOT NULL, volume TEXT NOT NULL, rel TEXT NOT NULL, kind INTEGER NOT NULL,
 old_bytes INTEGER, new_bytes INTEGER, PRIMARY KEY(scan_id,kind,rel)
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS scan_changes_volume ON scan_changes(volume,scan_id DESC);
CREATE TABLE IF NOT EXISTS scan_coverage (
 scan_id INTEGER NOT NULL, directory TEXT NOT NULL, complete INTEGER NOT NULL,
 detail TEXT, PRIMARY KEY(scan_id,directory)
) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS moves (
 scan_id INTEGER NOT NULL, file_key TEXT NOT NULL, old_path TEXT NOT NULL, new_path TEXT NOT NULL,
 PRIMARY KEY(scan_id,file_key)
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS moves_scan_old_path ON moves(scan_id,old_path);

CREATE TABLE IF NOT EXISTS catalog_meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);

CREATE INDEX IF NOT EXISTS files_browse ON files(volume,parent_key,(attributes & 16) DESC,name_key,rel);
DROP INDEX IF EXISTS files_parent;
DROP TRIGGER IF EXISTS filename_insert;
DROP TRIGGER IF EXISTS filename_delete;
DROP TABLE IF EXISTS filename_fragments;
COMMIT;

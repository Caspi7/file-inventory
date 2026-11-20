PRAGMA journal_mode=WAL;
PRAGMA temp_store=MEMORY;
CREATE TABLE IF NOT EXISTS volumes (
 id TEXT PRIMARY KEY, mount TEXT NOT NULL, label TEXT NOT NULL,
 online INTEGER NOT NULL, enabled INTEGER NOT NULL DEFAULT 0,
 hours REAL NOT NULL DEFAULT 24, attempted INTEGER, succeeded INTEGER,
 outcome TEXT NOT NULL DEFAULT '', issues TEXT NOT NULL DEFAULT ''
);
CREATE TABLE IF NOT EXISTS files (
 id INTEGER PRIMARY KEY, volume TEXT NOT NULL, rel TEXT NOT NULL, name TEXT NOT NULL,
 name_key TEXT NOT NULL, parent_key TEXT NOT NULL, extension TEXT NOT NULL,
 bytes INTEGER NOT NULL, modified INTEGER NOT NULL, created INTEGER NOT NULL,
 attributes INTEGER NOT NULL, seen INTEGER NOT NULL, stale INTEGER NOT NULL DEFAULT 0,
 file_key TEXT, UNIQUE(volume, rel)
);
CREATE INDEX IF NOT EXISTS files_name ON files(name_key);
CREATE INDEX IF NOT EXISTS files_volume_identity ON files(volume,file_key);
CREATE INDEX IF NOT EXISTS files_extension ON files(extension);
CREATE INDEX IF NOT EXISTS files_size ON files(bytes);
CREATE INDEX IF NOT EXISTS files_modified ON files(modified);
CREATE INDEX IF NOT EXISTS files_copy_candidates ON files(bytes,modified,created);
CREATE TABLE IF NOT EXISTS prefixes (
 token TEXT NOT NULL, file_id INTEGER NOT NULL, PRIMARY KEY(token,file_id)
) WITHOUT ROWID;
CREATE INDEX IF NOT EXISTS prefixes_file ON prefixes(file_id);
CREATE VIRTUAL TABLE IF NOT EXISTS fragments USING fts5(
 name_key, parent_key, content='files', content_rowid='id', tokenize='trigram case_sensitive 1'
);
CREATE VIRTUAL TABLE IF NOT EXISTS filename_fragments USING fts5(
 name_key, content='files', content_rowid='id', tokenize='trigram case_sensitive 1'
);
CREATE TRIGGER IF NOT EXISTS filename_insert AFTER INSERT ON files BEGIN
 INSERT INTO filename_fragments(rowid,name_key) VALUES(new.id,new.name_key);
END;
CREATE TRIGGER IF NOT EXISTS filename_delete AFTER DELETE ON files BEGIN
 INSERT INTO filename_fragments(filename_fragments,rowid,name_key) VALUES('delete',old.id,old.name_key);
END;
CREATE TRIGGER IF NOT EXISTS files_insert AFTER INSERT ON files BEGIN
 INSERT INTO fragments(rowid,name_key,parent_key) VALUES(new.id,new.name_key,new.parent_key);
END;
CREATE TRIGGER IF NOT EXISTS files_delete AFTER DELETE ON files BEGIN
 INSERT INTO fragments(fragments,rowid,name_key,parent_key) VALUES('delete',old.id,old.name_key,old.parent_key);
 DELETE FROM prefixes WHERE file_id=old.id;
END;
CREATE TABLE IF NOT EXISTS scans (
 id INTEGER PRIMARY KEY, volume TEXT NOT NULL, started INTEGER NOT NULL, finished INTEGER,
 outcome TEXT NOT NULL, items INTEGER NOT NULL DEFAULT 0, issues TEXT NOT NULL DEFAULT '',
 identity_ms INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS scan_coverage (
 scan_id INTEGER NOT NULL, directory TEXT NOT NULL, complete INTEGER NOT NULL,
 detail TEXT, PRIMARY KEY(scan_id,directory)
) WITHOUT ROWID;
CREATE TABLE IF NOT EXISTS moves (
 scan_id INTEGER NOT NULL, file_key TEXT NOT NULL, old_path TEXT NOT NULL, new_path TEXT NOT NULL,
 PRIMARY KEY(scan_id,file_key)
) WITHOUT ROWID;

CREATE INDEX IF NOT EXISTS files_parent ON files(volume,parent_key);
CREATE TABLE IF NOT EXISTS catalog_meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);

CREATE INDEX IF NOT EXISTS files_browse ON files(volume,parent_key,(attributes & 16) DESC,name_key,rel);

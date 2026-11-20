-- Preserve observations in directories for which enumeration could not establish absence.
UPDATE files SET stale=1 WHERE volume=$volume AND id IN (
 SELECT f.id FROM pending.coverage c CROSS JOIN files f
 WHERE c.complete=0 AND c.directory<>'' AND f.volume=$volume
 AND f.rel>=c.directory||char(92) AND f.rel<c.directory||char(93)
 UNION ALL
 SELECT f.id FROM files f WHERE f.volume=$volume
 AND EXISTS(SELECT 1 FROM pending.coverage WHERE complete=0 AND directory='')
);
INSERT INTO scan_coverage SELECT $scan,directory,complete,detail FROM pending.coverage;

-- A move requires a single old and new path and equal creation timestamps.
-- Skip a move if the old path lies under unverified coverage or remains present.
INSERT INTO moves
WITH old_ids AS (
 SELECT file_key,min(rel) AS rel,min(created) AS created,min(stale) AS stale
 FROM files WHERE volume=$volume AND file_key IS NOT NULL
 GROUP BY file_key HAVING count(*)=1
), new_ids AS (
 SELECT file_key,min(rel) AS rel,min(created) AS created
 FROM pending.entries WHERE file_key IS NOT NULL
 GROUP BY file_key HAVING count(*)=1
)
SELECT $scan,o.file_key,o.rel,n.rel FROM old_ids o JOIN new_ids n USING(file_key)
WHERE o.rel<>n.rel AND o.created=n.created AND o.stale=0
  AND NOT EXISTS(SELECT 1 FROM pending.entries p WHERE p.rel=o.rel);

-- Refresh at existing paths without rebuilding FTS for unchanged rows.
UPDATE files AS f SET bytes=p.bytes,modified=p.modified,created=p.created,attributes=p.attributes,
 seen=$seen,stale=0,file_key=p.file_key,extension=p.extension
FROM pending.entries p WHERE f.volume=$volume AND f.rel=p.rel
AND (f.bytes,f.modified,f.created,f.attributes,f.file_key,f.extension)
 IS NOT (p.bytes,p.modified,p.created,p.attributes,p.file_key,p.extension);
UPDATE files AS f SET seen=$seen,stale=0
WHERE f.volume=$volume AND (f.seen<>$seen OR f.stale<>0)
AND EXISTS(SELECT 1 FROM pending.entries p WHERE p.rel=f.rel);
DELETE FROM files AS f WHERE volume=$volume AND NOT EXISTS(SELECT 1 FROM pending.entries p WHERE p.rel=f.rel) AND NOT EXISTS(
 SELECT 1 FROM pending.coverage c WHERE c.complete=0
 AND (c.directory='' OR substr(f.rel,1,length(c.directory)+1)=c.directory||char(92))
);
-- Only new paths need new prefix rows. Existing paths keep the same normalized names.
CREATE TEMP TABLE new_paths(rel TEXT PRIMARY KEY) WITHOUT ROWID;
INSERT INTO new_paths SELECT p.rel FROM pending.entries p
WHERE NOT EXISTS(SELECT 1 FROM files f WHERE f.volume=$volume AND f.rel=p.rel);
INSERT INTO files(volume,rel,name,name_key,parent_key,extension,bytes,modified,created,attributes,seen,file_key)
SELECT $volume,p.rel,p.name,p.name_key,p.parent_key,p.extension,p.bytes,p.modified,p.created,p.attributes,$seen,p.file_key
FROM new_paths n CROSS JOIN pending.entries p ON p.rel=n.rel;
INSERT OR IGNORE INTO prefixes(token,file_id)
SELECT x.token,f.id FROM new_paths n CROSS JOIN files f ON f.volume=$volume AND f.rel=n.rel
CROSS JOIN pending.prefixes x ON x.rel=n.rel;
DROP TABLE new_paths;

-- Preserve observations in directories for which enumeration could not establish absence.
UPDATE files SET stale=1 WHERE volume=$volume AND removed IS NULL AND id IN (
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
 FROM files WHERE volume=$volume AND removed IS NULL AND file_key IS NOT NULL
 GROUP BY file_key HAVING count(*)=1
), new_ids AS (
 SELECT file_key,min(rel) AS rel,min(created) AS created
 FROM pending.entries WHERE file_key IS NOT NULL
 GROUP BY file_key HAVING count(*)=1
)
SELECT $scan,o.file_key,o.rel,n.rel FROM old_ids o JOIN new_ids n USING(file_key)
WHERE o.rel<>n.rel AND o.created=n.created AND o.stale=0
  AND NOT EXISTS(SELECT 1 FROM pending.entries p WHERE p.rel=o.rel);

-- Classify changes against the previous snapshot, before updating the live rows.
INSERT INTO scan_changes(scan_id,volume,rel,kind,old_bytes,new_bytes)
SELECT $scan,$volume,m.new_path,4,
       CASE WHEN (f.attributes & 16)=0 THEN f.bytes ELSE 0 END,
       CASE WHEN (p.attributes & 16)=0 THEN p.bytes ELSE 0 END
FROM moves m JOIN files f ON f.volume=$volume AND f.rel=m.old_path
JOIN pending.entries p ON p.rel=m.new_path
WHERE $hasPriorScan=1 AND m.scan_id=$scan;

INSERT INTO scan_changes(scan_id,volume,rel,kind,old_bytes,new_bytes)
SELECT $scan,$volume,p.rel,1,NULL,CASE WHEN (p.attributes & 16)=0 THEN p.bytes ELSE 0 END
FROM pending.entries p
LEFT JOIN files f ON f.volume=$volume AND f.rel=p.rel AND f.removed IS NULL
WHERE $hasPriorScan=1 AND f.id IS NULL
AND NOT EXISTS(SELECT 1 FROM scan_changes c WHERE c.scan_id=$scan AND c.kind=4 AND c.rel=p.rel);

INSERT INTO scan_changes(scan_id,volume,rel,kind,old_bytes,new_bytes)
SELECT $scan,$volume,p.rel,2,
       CASE WHEN (f.attributes & 16)=0 THEN f.bytes ELSE 0 END,
       CASE WHEN (p.attributes & 16)=0 THEN p.bytes ELSE 0 END
FROM pending.entries p JOIN files f ON f.volume=$volume AND f.rel=p.rel
WHERE $hasPriorScan=1 AND f.removed IS NULL
AND ((f.bytes,f.modified,f.created,f.attributes) IS NOT (p.bytes,p.modified,p.created,p.attributes)
     OR (f.file_key IS NOT NULL AND p.file_key IS NOT NULL AND f.file_key<>p.file_key))
AND NOT EXISTS(SELECT 1 FROM scan_changes c WHERE c.scan_id=$scan AND c.kind=4 AND c.rel=p.rel);

INSERT INTO scan_changes(scan_id,volume,rel,kind,old_bytes,new_bytes)
SELECT $scan,$volume,f.rel,3,CASE WHEN (f.attributes & 16)=0 THEN f.bytes ELSE 0 END,NULL
FROM files f WHERE $hasPriorScan=1 AND f.volume=$volume AND f.removed IS NULL
AND (EXISTS(SELECT 1 FROM scan_changes c WHERE c.scan_id=$scan AND c.kind=4 AND c.rel=f.rel)
 OR (NOT EXISTS(SELECT 1 FROM pending.entries p WHERE p.rel=f.rel) AND NOT EXISTS(
     SELECT 1 FROM pending.coverage c WHERE c.complete=0
     AND (c.directory='' OR substr(f.rel,1,length(c.directory)+1)=c.directory||char(92))
 ))
)
AND NOT EXISTS(SELECT 1 FROM moves m WHERE m.scan_id=$scan AND m.old_path=f.rel);

UPDATE scans SET (added_count,modified_count,removed_count,moved_count,bytes_delta) = (
 SELECT count(*) FILTER (WHERE kind=1),count(*) FILTER (WHERE kind=2),
        count(*) FILTER (WHERE kind=3),count(*) FILTER (WHERE kind=4),
        coalesce(sum(coalesce(new_bytes,0)-coalesce(old_bytes,0)),0)
 FROM scan_changes WHERE scan_id=$scan
) WHERE id=$scan AND $hasPriorScan=1;

DELETE FROM file_hashes WHERE file_id IN (
 SELECT h.file_id FROM file_hashes h JOIN files f ON f.id=h.file_id
 LEFT JOIN pending.entries p ON p.rel=f.rel
 WHERE f.volume=$volume AND (p.rel IS NULL OR f.removed IS NOT NULL OR
     (f.bytes,f.modified,f.created,f.attributes,f.file_key)
     IS NOT (p.bytes,p.modified,p.created,p.attributes,p.file_key))
);

-- Refresh at existing paths without rebuilding FTS for unchanged rows.
UPDATE files AS f SET bytes=p.bytes,modified=p.modified,created=p.created,attributes=p.attributes,
 seen=$seen,stale=0,removed=NULL,file_key=p.file_key,extension=p.extension
FROM pending.entries p WHERE f.volume=$volume AND f.rel=p.rel
AND (f.bytes,f.modified,f.created,f.attributes,f.file_key,f.extension)
 IS NOT (p.bytes,p.modified,p.created,p.attributes,p.file_key,p.extension);
UPDATE files AS f SET seen=$seen,stale=0,removed=NULL
WHERE f.volume=$volume AND (f.seen<>$seen OR f.stale<>0 OR f.removed IS NOT NULL)
AND EXISTS(SELECT 1 FROM pending.entries p WHERE p.rel=f.rel);
UPDATE files AS f SET removed=$seen,stale=0
WHERE volume=$volume AND removed IS NULL AND NOT EXISTS(SELECT 1 FROM pending.entries p WHERE p.rel=f.rel) AND NOT EXISTS(
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
SELECT j.value,f.id
FROM new_paths n
CROSS JOIN files f ON f.volume=$volume AND f.rel=n.rel
CROSS JOIN pending.entries p ON p.rel=n.rel
CROSS JOIN json_each(p.tokens) j
ORDER BY j.value COLLATE BINARY,f.id;
DROP TABLE new_paths;

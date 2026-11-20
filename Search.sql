WITH candidates AS (
 SELECT id FROM files WHERE name_key=$query AND $query<>''
 UNION
 SELECT id FROM files WHERE name_key >= $query AND name_key < $end AND $end<>''
 UNION
 SELECT rowid FROM fragments WHERE $match IS NOT NULL AND fragments MATCH coalesce($match,'"zzz"')
 UNION
 SELECT file_id FROM prefixes WHERE token=$firstShort AND $match IS NULL
 UNION
 SELECT id FROM files WHERE $query=''
), ranked AS (
 SELECT f.*,v.mount,v.online,v.label,
 CASE
  WHEN name_key=$query THEN 0
  WHEN substr(name_key,1,length($query))=$query THEN 1
  WHEN $long=1 AND instr(name_key,$query)>0 THEN 2
  WHEN NOT EXISTS(SELECT 1 FROM json_each($terms) WHERE
   CASE WHEN length(value)<3 THEN substr(name_key,1,length(value))<>value
        ELSE instr(name_key,value)=0 END) THEN 2
  ELSE 3 END AS rank,
 (substr(name_key,1,length($query))<>$query AND EXISTS(SELECT 1 FROM json_each($terms) WHERE
   CASE WHEN length(value)<3 THEN NOT EXISTS(
    SELECT 1 FROM prefixes WHERE token=value AND file_id=f.id)
   ELSE instr(name_key,value)=0 AND instr(parent_key,value)=0 END)) AS contextual
 FROM candidates c JOIN files f ON f.id=c.id JOIN volumes v ON v.id=f.volume
 WHERE ($volume IS NULL OR f.volume=$volume)
 AND ($extension IS NULL OR extension=$extension)
 AND ($min IS NULL OR bytes >= $min) AND ($max IS NULL OR bytes <= $max)
 AND ($modifiedFrom IS NULL OR modified >= $modifiedFrom)
 AND ($modifiedThrough IS NULL OR modified < $modifiedThrough)
 AND ($online IS NULL OR v.online=$online)
 AND (substr(name_key,1,length($query))=$query OR NOT EXISTS(SELECT 1 FROM json_each($short) WHERE NOT EXISTS(
  SELECT 1 FROM prefixes WHERE token=value AND file_id=f.id)))
)
SELECT name,rel,mount,volume,label,online,stale,seen,bytes,modified,attributes,rank,contextual
FROM ranked ORDER BY contextual,rank, length(name), name_key, volume, rel LIMIT 50;

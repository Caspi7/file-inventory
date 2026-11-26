WITH candidates AS (
 SELECT rowid AS id FROM fragments
 WHERE $match IS NOT NULL AND fragments MATCH coalesce($match,'("zzz")')
 UNION ALL
 SELECT file_id AS id FROM prefixes WHERE token=$firstShort AND $match IS NULL
), ranked AS (
 SELECT f.*,v.mount,v.online,v.label,
 CASE WHEN name_key=$query THEN 0
      WHEN substr(name_key,1,length($query))=$query THEN 1
      WHEN matches_name_only(name_key,$query) THEN 2
      ELSE 3 END AS rank
 FROM candidates c CROSS JOIN files f ON f.id=c.id JOIN volumes v ON v.id=f.volume
 WHERE ($volume IS NULL OR f.volume=$volume)
 AND ($extension IS NULL OR extension=$extension)
 AND ($min IS NULL OR bytes >= $min) AND ($max IS NULL OR bytes <= $max)
 AND ($modifiedFrom IS NULL OR modified >= $modifiedFrom)
 AND ($modifiedThrough IS NULL OR modified < $modifiedThrough)
 AND ($online IS NULL OR v.online=$online)
 AND ($firstShort IS NULL OR matches_name_prefixes(name_key || ' ' || parent_key))
)
SELECT name,rel,mount,volume,label,online,stale,seen,bytes,modified,attributes,rank,0,removed
FROM ranked ORDER BY rank,length(name),name_key,volume,rel LIMIT 50;

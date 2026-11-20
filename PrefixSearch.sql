SELECT name,rel,mount,volume,label,online,stale,seen,bytes,modified,attributes,
       CASE WHEN name_key=$query THEN 0 ELSE 1 END,0
FROM files f JOIN volumes v ON v.id=f.volume
WHERE name_key >= $query AND name_key < $end
  AND ($volume IS NULL OR f.volume=$volume)
  AND ($extension IS NULL OR extension=$extension)
  AND ($min IS NULL OR bytes >= $min) AND ($max IS NULL OR bytes <= $max)
  AND ($modifiedFrom IS NULL OR modified >= $modifiedFrom)
  AND ($modifiedThrough IS NULL OR modified < $modifiedThrough)
  AND ($online IS NULL OR v.online=$online)
ORDER BY name_key LIMIT 50;

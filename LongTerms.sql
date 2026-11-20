SELECT name,rel,mount,volume,label,online,stale,seen,bytes,modified,attributes,$tier,$context
FROM fragments CROSS JOIN files f ON f.id=fragments.rowid
CROSS JOIN volumes v ON v.id=f.volume
WHERE fragments MATCH $clause AND substr(f.name_key,1,length($query))<>$query
  AND ($tier<>3 OR EXISTS (SELECT 1 FROM json_each($terms) WHERE instr(f.name_key,value)=0))
  AND ($tier<>3 OR $context = EXISTS (
    SELECT 1 FROM json_each($terms)
    WHERE instr(f.name_key,value)=0 AND instr(f.parent_key,value)=0
  ))
  AND ($volume IS NULL OR f.volume=$volume)
  AND ($extension IS NULL OR extension=$extension)
  AND ($min IS NULL OR bytes >= $min) AND ($max IS NULL OR bytes <= $max)
  AND ($modifiedFrom IS NULL OR modified >= $modifiedFrom)
  AND ($modifiedThrough IS NULL OR modified < $modifiedThrough)
  AND ($online IS NULL OR v.online=$online)
ORDER BY fragments.rowid LIMIT $limit;

"""Exercise the exact SQL shipped in the desktop app with Python's SQLite."""
import argparse
from contextlib import closing, nullcontext
import json
from pathlib import Path
import re
import sqlite3
import statistics
import tempfile
import time

ROOT = Path(__file__).resolve().parent
SCHEMA = (ROOT / "Catalog.sql").read_text()
SEARCH = (ROOT / "Search.sql").read_text()
PREFIX = (ROOT / "PrefixSearch.sql").read_text()
LONG = (ROOT / "LongTerms.sql").read_text()
INSERT = """INSERT INTO files(volume,rel,name,name_key,parent_key,extension,bytes,
modified,created,attributes,seen) VALUES(?,?,?,?,?,?,?,?,?,0,?)"""


def prefixes(name, parent):
    for segment in (*parent.split("\\"), name):
        for word in (segment, *re.findall(r"\w+", segment)):
            for limit in (1, 2):
                if len(word) >= limit:
                    yield word[:limit]


def add(db, volume, parent, name, size=123):
    key = name.lower()
    directory = parent.lower()
    cursor = db.execute(INSERT, (volume, f"{parent}\\{name}", name, key, directory,
                                 key.rsplit(".", 1)[-1], size, 1000, 1000, 2000))
    db.executemany("INSERT INTO prefixes VALUES(?,?)",
                   [(token, cursor.lastrowid) for token in set(prefixes(key, directory))])


def find(db, query, **filters):
    query = query.lower()
    terms = list(dict.fromkeys(query.split()))
    short = [term for term in terms if len(term) < 3]
    match = " AND ".join('"' + term.replace('"', '""') + '"'
                         if term != "hangar" else '("hangar" OR parent_key : ("hangaar"))'
                         for term in terms if term not in short)
    params = dict(query=query, end=query + chr(0x10ffff), long=len(query) >= 3,
                  match=match or None, firstShort=next(iter(short), None),
                  short=json.dumps(short), terms=json.dumps(terms),
                  **dict.fromkeys(("volume", "extension", "min", "max",
                                   "modifiedFrom", "modifiedThrough", "online")))
    params.update(filters)
    if terms and (not short or len(terms) == 1):
        leading = db.execute(PREFIX, params).fetchall()
        if len(leading) == 50:
            return leading
        if not short:
            literal = " AND ".join('"' + term.replace('"', '""') + '"' for term in terms)
            tiers = [(literal, 2, 0), (literal, 3, 0)]
            if "hangar" in terms:
                tiers.append((match, 3, 1))
            for clause, tier, context in tiers:
                if len(leading) == 50:
                    break
                if tier == 2:
                    clause = f"name_key : ({clause})"
                leading.extend(db.execute(LONG, dict(params, clause=clause, tier=tier,
                                                      context=context, limit=50-len(leading))))
            return leading
    return db.execute(SEARCH, params).fetchall()


def main(rows, rounds, output):
    location = nullcontext(None) if output else tempfile.TemporaryDirectory()
    with location as temp:
        path = output or Path(temp) / "catalog.sqlite3"
        if output:
            path.parent.mkdir(parents=True, exist_ok=True)
            path.touch(exist_ok=False)
        with closing(sqlite3.connect(path)) as db:
            db.executescript(SCHEMA)
            db.executemany("INSERT INTO volumes(id,mount,label,online) VALUES(?,?,?,?)",
                           [("online", "C:\\", "System", 1), ("offline", "F:\\", "Archive", 0)])
            start = time.perf_counter()
            for i in range(rows):
                add(db, "online", f"Groups\\Section_{i // 1000:04}\\Batch_{i // 100:05}",
                    f"Item-{i:07}.dat")
                if (i + 1) % 20000 == 0:
                    db.commit()
            add(db, "offline", "KLM\\Hangaar 14", "Reflectie week 4.docx", 205)
            add(db, "online", "KLM\\Hangar 14", "Reflectie week 5.docx", 206)
            add(db, "online", "Notes", "Café (Q4).pdf")
            add(db, "online", "Notes", "Reflectie.docx")
            db.commit()
            load_seconds = time.perf_counter() - start
            db.execute("PRAGMA wal_checkpoint(TRUNCATE);")
            assert find(db, "reflectie.docx")[0][0] == "Reflectie.docx"
            assert "Reflectie.docx" in [row[0] for row in find(db, "flectie")]
            assert [row[0] for row in find(db, "klm hangar reflectie")][:2] == [
                "Reflectie week 5.docx", "Reflectie week 4.docx"]
            assert find(db, "klm hangar reflectie", extension="docx", min=205, max=205,
                        modifiedFrom=1000, modifiedThrough=1001, online=0)[0][3] == "offline"
            assert find(db, "kl re", volume="offline")[0][0] == "Reflectie week 4.docx"
            assert find(db, "CAFÉ (Q4)")[0][0] == "Café (Q4).pdf"
            assert find(db, "kl non-existent") == []
            assert find(db, '"') == []
            assert [row[3] for row in find(db, "hangar")] == ["online", "offline"]
            print(f"SQLite {sqlite3.sqlite_version}. {rows + 4:,} records loaded in "
                  f"{load_seconds:.2f}s. Catalog {path.stat().st_size / 1048576:.1f} MiB.")
            with closing(sqlite3.connect(path)) as writer:
                writer.execute("BEGIN IMMEDIATE")
                add(writer, "online", "Staged", "Unpublished.docx")
                assert not find(db, "unpublished")
                for query in ("reflectie.docx", "flectie", "klm hangar reflectie",
                              "kl re", "café (q4)", "it", "item", "tem-", "section", "hangar", "section item"):
                    timings = []
                    for _ in range(rounds):
                        start = time.perf_counter()
                        results = find(db, query)
                        timings.append(1000 * (time.perf_counter() - start))
                    p95 = sorted(timings)[max(0, (95 * rounds + 99) // 100 - 1)]
                    print(f"{query!r}: {len(results)} results, "
                          f"median {statistics.median(timings):.2f} ms, p95 {p95:.2f} ms")
                writer.rollback()
            with closing(sqlite3.connect(path)) as fresh:
                start = time.perf_counter()
                find(fresh, "reflectie")
                print(f"Fresh connection first query: {(time.perf_counter()-start)*1000:.2f} ms "
                      "(OS cache warm).")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--rows", type=int, default=1000000)
    parser.add_argument("--rounds", type=int, default=8)
    parser.add_argument("--output", type=Path, help="Keep a new catalog at this path")
    args = parser.parse_args()
    main(args.rows, args.rounds, args.output)

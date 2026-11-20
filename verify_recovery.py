"""Test publication and interrupted-refresh behavior against the shipped SQL."""
import sqlite3
from contextlib import closing
from pathlib import Path
import tempfile

from verify_search import SCHEMA, add, find

PUBLISH = Path(__file__).with_name("Publish.sql").read_text()
VOLUME = "usb"
STAGE = r"""
CREATE TABLE pending.entries(rel TEXT PRIMARY KEY,name,name_key,parent_key,extension,
 bytes,modified,created,attributes,seen,file_key);
CREATE TABLE pending.coverage(directory TEXT PRIMARY KEY,complete,detail);
CREATE TABLE pending.prefixes(token,rel);
INSERT INTO pending.coverage VALUES('',1,NULL),('Blocked',0,'Access denied'),('Fresh',1,NULL);
INSERT INTO pending.entries VALUES
 ('Fresh\renamed.docx','renamed.docx','renamed.docx','fresh','docx',456,1001,1000,0,3000,'id-1'),
 ('Fresh\shared.docx','shared.docx','shared.docx','fresh','docx',123,1000,1000,0,3000,'id-2'),
 ('Fresh\shared-copy.docx','shared-copy.docx','shared-copy.docx','fresh','docx',123,1000,1000,0,3000,'id-2'),
 ('Fresh\reused.docx','reused.docx','reused.docx','fresh','docx',123,1000,1005,0,3000,'id-3');
INSERT INTO pending.prefixes VALUES('re','Fresh\renamed.docx');
"""


def main():
    with tempfile.TemporaryDirectory() as temp:
        path = Path(temp) / "verify.sqlite3"
        with closing(sqlite3.connect(path)) as writer, closing(sqlite3.connect(path)) as reader:
            writer.executescript(SCHEMA)
            writer.execute("INSERT INTO volumes(id,mount,label,online) VALUES(?,?,?,1)",
                           (VOLUME, "E:\\", "Archive"))
            for directory, name, key in [
                ("Blocked", "hidden.docx", None),
                ("BlockedSuffix", "delete.txt", None),
                ("Fresh", "old.docx", "id-1"),
                ("Fresh", "shared.docx", "id-2"),
                ("Fresh", "shared-copy.docx", "id-2"),
                ("Fresh", "obsolete.txt", "id-3"),
            ]:
                add(writer, VOLUME, directory, name)
                writer.execute("UPDATE files SET file_key=? WHERE name=?", (key, name))
            writer.commit()
            writer.execute("ATTACH DATABASE ':memory:' AS pending")
            writer.executescript(STAGE)

            def stage_publish():
                writer.execute("BEGIN IMMEDIATE")
                for statement in PUBLISH.split(";"):
                    if statement.strip():
                        writer.execute(statement, {"scan": 1, "volume": VOLUME, "seen": 3000})

            stage_publish()
            assert find(reader, "old.docx") and not find(reader, "renamed.docx")
            writer.rollback()
            assert find(reader, "old.docx") and not find(reader, "renamed.docx")
            stage_publish()
            writer.commit()
            assert not find(reader, "old.docx") and not find(reader, "obsolete.txt")
            assert not find(reader, "delete.txt")
            assert find(reader, "renamed.docx")[0][7] == 3000
            assert find(reader, "hidden.docx")[0][6] == 1
            assert len(find(reader, "shared", volume=VOLUME)) == 2
            assert reader.execute("SELECT old_path,new_path FROM moves").fetchall() == [
                ("Fresh\\old.docx", "Fresh\\renamed.docx")]
            assert reader.execute("SELECT complete,detail FROM scan_coverage WHERE directory='Blocked'").fetchone() == (
                0, "Access denied")
            writer.execute("UPDATE volumes SET online=0")
            writer.commit()
            assert find(reader, "renamed.docx", online=0)
            writer.execute("UPDATE volumes SET mount=?,online=1", ("G:\\",))
            writer.commit()
            assert find(reader, "renamed.docx", online=1)[0][2] == "G:\\"
        print("Publication, rollback, omission, FTS cleanup, identity, hard links and mount assertions passed.")


if __name__ == "__main__":
    main()

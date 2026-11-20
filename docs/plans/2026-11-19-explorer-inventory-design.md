# Explorer inventory design
Approved 2026-11-19.

Use a drive selector and Scan drive button above an Explorer-style split view. The left tree contains folders and files; the right list shows a folder's immediate contents when search is empty and all matching entries when searching. Clicking a search result expands its ancestors and selects the exact entry without clearing the results. Empty folders and offline inventories are included. Load tree branches and result pages on demand; display a total count and impose no 50-result cap. Preserve settings, existing catalogs, atomic scan publication, omissions, and read-only source access.

Directories will share the existing catalog entry table with their Directory attribute. Backfill ancestor folders for existing file rows once; a rescan discovers previously unrecorded empty folders. Search and browse pages are queried in a read transaction, ranked consistently, and rendered using a virtual grid. Tree navigation uses catalog paths, including offline paths. Failed navigation is shown in the UI without opening Explorer.

Verify folder publication and deletion, more than 50 matches, paging without duplicates, empty folders, offline persistence, and result-to-tree navigation on Windows. No Git repository is present, so the design is saved without a commit.

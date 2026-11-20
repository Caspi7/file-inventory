# Explorer Inventory Implementation Plan

**Goal:** Browse the saved filesystem and reach any matching file through its tree location.
**Architecture:** Keep the scanner's staged publication. Store directory entries alongside files. Add catalog paging and a lazy TreeView; use a virtual DataGridView for all results.
**Tech Stack:** .NET 8 WinForms, Microsoft.Data.Sqlite, existing SQLite schema and FTS5.
**Spec:** docs/plans/2026-11-19-explorer-inventory-design.md

## Global constraints
Preserve catalog/settings. Read source metadata only. Keep offline search. No silent result cap. Accessible ordinary files and folders only; report omissions.

## Review focus
- Old catalogs: infer missing ancestors once without deleting entries.
- Empty/protected folders: retain folder node and report inaccessible contents.
- Large matches: count all rows and retrieve bounded pages, stable order without duplicates.
- Rapid typing and scrolling: ignore obsolete query/page responses.
- Offline result navigation: expand saved ancestors and keep results visible.

### 1. Catalog and scanning
- [x] Add a parent index and one-time ancestor backfill in Catalog.cs/Catalog.sql.
- [x] Record directory rows in Scanner.cs before queuing traversal; directory identities remain null.
- [x] Add BrowsePage and SearchPage operations to Catalog.cs, bounded by limit/offset with full counts and deterministic ordering.
- [x] Verify against a disposable C# harness using the actual Catalog and Scanner classes: 125 matches, page boundaries, folders, empty folder, refresh deletion and offline status.

### 2. Explorer window
- [x] Change Program.cs to a partial window; put browser logic in InventoryBrowser.cs.
- [x] Place selector/scan controls above the tree/list split. Keep schedule/details in an expandable lower area.
- [x] Load tree children only on expansion. Show current path and folder contents when query is blank.
- [x] Render result pages with DataGridView.VirtualMode. Discard stale pages by generation.
- [x] On result click, expand ancestors asynchronously and select the exact file without changing the query or displayed results.

### 3. Verification and release
- [x] Build and run the supplied SQL recovery/search/related checks.
- [x] Exercise the Windows UI with a disposable fixture drive: scan, expand empty folder, search >50 matches, scroll to last result, click and inspect selected tree path, restart offline.
- [x] Publish the Windows executable and full source ZIP, with updated README and verification notes. Preserve the existing catalog.

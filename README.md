# File Inventory

A local Windows 10/11 desktop app for searching filenames and folders across mounted volumes and offline archives. This source build uses .NET 8 WinForms and SQLite. Search runs against the catalog, including while a scan stages a replacement snapshot.

## Build and run

On Windows, install the .NET 8 SDK, extract the archive, and run these commands from the directory containing `FileInventory.csproj`.

```powershell
dotnet build -c Release
dotnet run -c Release --no-build
```

To build a Windows x64 distribution that includes the runtime:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -o publish
```

Use `win-arm64` on Windows ARM. Start `FileInventory.exe` from the publish directory and keep the published files together.

## Use

- Select one or more volumes with Ctrl or Shift and click **Refresh selected**. Select a single volume to view its scan details or edit its schedule. Enable **Schedule refresh** and save it to include the volume in background refreshes. The interval starts at 24 hours and is measured from the last attempt. Minimize the window to keep it running in the notification area.
- Type any part of a filename or folder name. Put spaces between terms to find entries containing all terms across the filename and its ancestor folders. One- and two-character terms match indexed prefixes. Search is case-insensitive and preserves accent distinctions. Edit `aliases.json` under the data directory and restart to change the folder aliases.
- Use the volume, extension, size, modification date or connection filters as needed. Select a row to copy its path or reveal an eligible online entry in Explorer. **Find possible copies** uses size, creation time and modification time across volumes. Treat these results as unverified.
- The volume panel shows scan progress, omissions, the last attempt and the last refresh that published a snapshot. A scan with omissions can publish verified observations. Entries in unverified areas remain stale. Offline results show the last known mount path.

## Catalog behavior

The data directory is `%LOCALAPPDATA%\FileInventory`. It contains the SQLite catalog, temporary staging databases, the app instance lock and alias settings. The scanner excludes this directory, enumerates hidden entries, and records reparse and recall-risk entries without walking their subtrees. NTFS identity lookups use metadata-only handles with no-recall flags for ordinary files. The scanner records identity lookup cost and preserves separate hard-link paths. An ID match is recorded as a same-volume move candidate when both snapshots have a single path for the ID, creation times match and the old path can be verified absent.

Local mounted volumes use the Windows volume GUID where available. The fallback identity includes filesystem serial, filesystem type and mount point. Fallback identities can lose continuity when a drive letter changes. Mapped network shares are outside the current scanner scope. Connected and offline volumes share the same catalog.

Full file hashing, text extraction and NTFS journal refresh are deferred. The related-sibling ranking experiment runs as a standalone test.

## Verification

Python 3 with FTS5 trigram support can run the SQL checks on either platform.

```powershell
python verify_search.py
python verify_recovery.py
python verify_related.py
```

`verify_search.py` loads a million synthetic entries plus four fixtures. It reports warm p95, a fresh-connection query with the OS cache warm, catalog size and fixture load time. Timed queries run with a writer transaction open. `verify_recovery.py` exercises the same publication SQL as the app. See [WINDOWS_VERIFICATION.md](WINDOWS_VERIFICATION.md) for the platform acceptance run.

The provided source was validated at the SQL layer on Linux. This environment lacked the .NET SDK and a Windows runner, so the C# build and Windows behavior remain to be verified. The benchmark log measures SQL and fixture lookup only.


## Explorer-style inventory (Windows update)

Choose a drive at the top and click **Scan selected drive**. Scans record accessible files and folders, including empty folders. Protected folders and skipped reparse/recall-risk areas are reported as omissions.

Expand the saved **Folders and files** tree to browse. With the search box empty, the right list shows the selected folder's immediate contents. Type a filename or ancestor-folder term to search the selected drive; choose **All volumes** to search across catalogs. Every match is available in the scrollable list, with a total count. Results and tree branches load in pages rather than stopping at 50 entries.

Click a search result (or press Enter on it) to expand its ancestors and highlight the exact file in the tree. Search results stay visible. Double-click a folder in the list, or select a tree folder and click **Browse selected folder**, to leave search and browse its contents. Indexed offline drives retain their tree and search results; Explorer reveal stays disabled while offline.

Existing catalogs are preserved. Ancestor folders are reconstructed from previously indexed files on first launch. Scan a drive again to capture its empty folders, which older builds did not store.

Production C# integration checks are in `tests/ExplorerChecks`; see their README. The supplied Python SQL checks remain available and retain their original capped benchmark queries; they do not test the new paged UI.

## File details and scan speed update

Click a file in the tree (or press Enter on the selected file) to show its saved metadata and select it in Windows Explorer when available. The File details pane appears below the results and works for offline drives. Clicking a search result selects its tree node and shows its metadata in the pane.

The optimized scanner writes less temporary data and reuses search and metadata index entries for unchanged paths. See `WINDOWS_VERIFICATION.md` for measured timings.

## Install or uninstall

Run the Windows x64 setup executable to install File Inventory for the current Windows user. Use **Settings → Apps** to uninstall it. See [installer instructions](installer/README.md) to rebuild the setup package.

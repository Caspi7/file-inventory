# Windows acceptance checks

Record the Windows build, .NET SDK, SSD, CPU, memory, runtime SQLite version and sample size for each performance run. Repeat the workflow as a standard user on Windows 10 and 11. Run the app from a build directory outside any removable test drive.

## Search and render

1. Start with an empty benchmark subdirectory. Generate the synthetic catalog with `python verify_search.py --output "$env:LOCALAPPDATA\FileInventory\Benchmark\catalog.sqlite3"`.
2. Launch `dotnet run -c Release -- --benchmark`. This mode uses the benchmark subdirectory and disables real volume discovery, scanning and Explorer actions.
3. Exercise exact names, prefixes, broad substrings, broad multi-term searches, accented names, punctuation and mixed terms such as `kl re`. Test the volume, status, extension, size and modified date filters together. Check that a folder-name search returns the folder itself and excludes children whose own names do not match.
4. Collect at least 30 repetitions per query. Use the displayed final-input-to-grid-update milliseconds and record p95 for each query. Include cold launch and first query separately. The provisional warm target is p95 ≤ 200 ms. Compare a second run against an app catalog while a large real refresh is staging and again while it publishes. Record any UI pauses, chosen scan volume duration and catalog size.

## Volume discovery and read-only scanning

1. Prepare disposable fixtures on an NTFS volume and an exFAT removable volume with an archive file, a hidden file, a long nested path, a directory inaccessible to the test account and an ordinary deleted or moved file. On NTFS, include two hard links, a junction loop and a cloud provider placeholder set to online-only. Prepare fixtures separately from the scan session.
2. Start the app as the ordinary test account, select the test volumes and refresh each one. Confirm that hidden and reachable entries can be found, every hard-link path appears, the loop is not followed, and omissions appear in the volume panel. Check that paths under the app data directory are absent.
3. Trace the app process with Windows Process Monitor. For source volumes, inspect opens, requested access and file operations. Expect zero content reads, zero source writes, and no recalls or changes from online-only to hydrated. Ensure application-owned file writes are inside its dedicated data directory. During Explorer reveal, distinguish the app process from Explorer.
4. Move a regular NTFS file within the same volume and refresh. Verify its new path appears and the old path disappears. Verify `moves` has one observation for that unique ID and no inferred move for ambiguous hard-link paths. Record lookup count and duration reported by the scanner. Validate ordinary exFAT enumeration with identity lookup skipped.
5. Cancel another refresh after its counters advance. Verify old search results and last successful time persist. Repeat with disconnect during a scan, a previously accessible subdirectory made inaccessible and a normal directory deleted. Expect offline after disconnect, stale results under the inaccessible directory and no results under the confirmed deleted directory. Restart and check offline search persists.
6. Reconnect the archive with a different letter. On GUID-backed local mounts, confirm the old volume and schedule persist and search paths use the current letter. Verify copy path and Explorer reveal, including through a long path. Before exiting, confirm a scheduled volume refresh runs while the window is minimized and only one scan runs per volume.


## Explorer update verified locally, 2026-11-19

Windows build 26300.9974, x64; portable .NET SDK 8.0.428 / runtime 8.0.34. Existing catalog and aliases backed up before upgrading. Build and publish passed.

- Production C# Catalog/Scanner checks passed: 625 matches across three result pages with no duplicates; last result; empty folders; short ancestor/filename terms; accented punctuation; extension filtering; cancellation preserving the previous snapshot; deleted file/folder publication; offline reopen; old-catalog ancestor migration; SQLite integrity.
- Windows UI: scanned a disposable mapped R: folder, showing 629 entries including directory records. Search displayed all 625 matches. Clicking the first result expanded Projects/Reports and selected Needle-0000.txt. Scrolling to the last row and pressing Enter selected Needle-0624.txt while preserving all search results.
- Python verify_search.py (10,004 entries), verify_recovery.py, verify_related.py passed after the update. This smoke run is not an end-to-end million-file performance acceptance measurement.
- Physical USB disconnect/relettering, exFAT, cloud placeholder no-recall tracing, Process Monitor source-access audit, and Windows 10 remain outside this local smoke run.

- Final offline UI check: unmapped the disposable R: fixture, restarted the app, searched for Empty, double-clicked it, and verified the saved folder opened with 0 items and Explorer reveal disabled. The packaged C# checks were rebuilt without warnings and passed again on a fresh S: fixture. Both disposable mappings were removed.

## File details and scan speed update, 2026-11-20

The Windows UI selected the test file in its containing Explorer folder and showed the saved file size, creation time, and attributes in a separate window. The integration checks covered metadata accuracy and offline availability, deletion and cancellation, and short-prefix searches after rescan.

A mapped test directory contained 120,046 entries, including 120,000 files. Measured wall time includes staging, publication, and cleanup. The warm prior-build fresh scan took 12.385 s. Two optimized fresh scans took 7.544 s and 7.656 s. Prior-build rescans took 11.167 s and 10.567 s. Optimized rescans took 4.389 s and 4.648 s. Timings are specific to this fixture and workstation.

The inline details update was verified on Windows. Selecting a search result showed its metadata below the results, and clicking its file node opened the containing folder in Explorer while the metadata stayed in the main window.

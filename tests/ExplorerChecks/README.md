# Explorer inventory checks

Run on Windows with the .NET 8 SDK. These checks create and remove sample files and folders. Use an EMPTY disposable mapped drive and a NEW catalog directory, never a drive containing your own files.

    dotnet run --project tests/ExplorerChecks/ExplorerChecks.csproj -- R:\ C:\Temp\FileInventoryExplorerChecks

Tests use the production catalog, scanner, volume discovery, and SQL. They check all 625 matches across pages, empty folders, short terms, extension filters, deletion publication, cancellation, offline persistence, integrity, and old-catalog ancestor reconstruction. The UI click-to-tree behavior needs the Windows smoke check in WINDOWS_VERIFICATION.md.

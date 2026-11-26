pub(crate) struct Section {
    pub title: &'static str,
    pub subtitle: &'static str,
    pub body: &'static str,
}

pub(crate) const SECTIONS: &[Section] = &[
    Section {
        title: "Getting Started",
        subtitle: "Browse your drives even when they’re unplugged",
        body: "File Inventory catalogs files and folders on local drives, external drives, and drives scanned on other PCs. The catalog stays available when a drive is disconnected.

1. Choose Scan drives, select a connected drive, and start a scan.
2. Expand the folder tree and select a folder to browse its contents.
3. Open Drive Scope and choose Local or Imported to narrow the catalog. File Inventory saves your selected drives for the next session.",
    },
    Section {
        title: "Search & Filters",
        subtitle: "Find files by path, filename, metadata, or tag",
        body: "Type in the main search field to instantly filter across both folder paths and filenames together. For example, typing 2024 taxes pdf matches Taxes\\2024\\return.pdf even when the year and folder topic are in parent folders rather than the filename itself.

Use ext:pdf for PDFs, size:>10mb for large files, modified:7d for recent changes, or tag:work for tagged items. Combine filters freely with keywords.

Use the status filter to show online, offline, or removed items. Click any column header to sort and click it again to reverse the order.

Use ★ Saved Searches to save and reopen frequent queries. Select any file to edit its Tags & Notes inline.",
    },
    Section {
        title: "Deep Search",
        subtitle: "Search inside documents by keywords, filters, or meaning",
        body: "Run a content scan to index TXT and code files, Office documents (DOCX, XLSX, PPTX, ODT), and PDFs.

Smart Search understands natural-language filters in English and Dutch — try large pdf invoice from last month without draft or grote excel vorige maand. Inferred filters appear as clickable Active Filter chips (such as Type: PDF ×, Modified: Last 30d ×, or Size: > 10 MB ×); click any chip to remove that filter and immediately broaden your search.

Every result displays a sortable Match badge explaining why it matched: Exact (green) for direct text hits, Fuzzy or Close (amber) for typo-tolerant SymSpell or synonym matches, Filename or Facet (blue) for name and metadata matches, and Concept (violet) for matches by meaning. Excerpts highlight matched terms directly with «word» markers.

Use AND, OR, NOT, -exclude, \"exact phrase\", or (group) when you want strict Boolean control.

In the Linguistic Search row, select English Pack or Multilingual (EN + NL) and choose Download Pack. Advanced Linguistic Search indexes documents locally so you can find files by meaning. Choose Update Index to include any previously scanned files.

Choose Eco, Balanced, or Turbo to control scanning resource use, and enable Skip system folders to ignore common system and build directories.",
    },
    Section {
        title: "Drives & Cloud Sync",
        subtitle: "Manage drive catalogs and share snapshots",
        body: "Open Drives to manage location notes and review freshness alerts. Use the remove action on an offline, imported, or online drive to remove it from the catalog.

Export and import .fiinventory snapshots for file and folder catalogs and .ficontent snapshots for Deep Search indexes.

Choose a sync folder managed by Google Drive, OneDrive, Dropbox, or iCloud in Sync settings. Use Sync now to exchange snapshots in one click. Enable optional AES-256-GCM encryption and use the same password on each PC.",
    },
    Section {
        title: "Duplicates, Changes & Map",
        subtitle: "Find copies, review scans, and see what uses space",
        body: "Open Duplicates to find possible copies across drives. Use SHA-256 verification to check whether the files have identical contents.

Open Changes to compare scans in Scan History. Added files appear in green, modified files in amber, removed files in red, and moved files in violet.

Open the interactive Storage Map with Ctrl+M. Larger rectangles represent larger files or folders, so you can spot what takes up the most space.",
    },
    Section {
        title: "Shortcuts & Updates",
        subtitle: "Navigate with the keyboard and keep the app current",
        body: "Ctrl+K    Command Palette
Ctrl+Shift+F    Deep Search
Ctrl+M    Storage Map
F1    Help & User Guide
Esc    Close the current dialog

Use Theme to switch among Light, Dark, Dark Blue, and System.

File Inventory can automatically check for updates at startup. When an update is available, click Download & Install Update. The app verifies the download with SHA-256 before running the installer.",
    },
];

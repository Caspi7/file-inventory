pub(crate) struct Card {
    pub heading: &'static str,
    pub badge: &'static str,
    pub tone: &'static str,
    pub body: &'static str,
    pub example: &'static str,
}

pub(crate) struct Section {
    pub title: &'static str,
    pub subtitle: &'static str,
    pub cards: &'static [Card],
}

pub(crate) const SECTIONS: &[Section] = &[
    Section {
        title: "Getting Started",
        subtitle: "Browse your drives even when they are unplugged",
        cards: &[
            Card {
                heading: "Scan a drive",
                badge: "", tone: "",
                body: "Choose Scan drives, select a connected drive, and start a scan. File Inventory catalogs files and folders on local and external drives.",
                example: "",
            },
            Card {
                heading: "Browse folders",
                badge: "", tone: "",
                body: "Expand the folder tree and select a folder to see its contents. You can still browse the catalog after disconnecting a drive.",
                example: "",
            },
            Card {
                heading: "Choose which drives to show",
                badge: "", tone: "",
                body: "Open Drive Scope and select drives from the Local and Imported groups. File Inventory remembers your selection for the next session.",
                example: "Imported drives are catalogs scanned on other PCs.",
            },
        ],
    },
    Section {
        title: "Search & Filters",
        subtitle: "Find files by path, filename, metadata, or tag",
        cards: &[
            Card {
                heading: "Search names and paths",
                badge: "", tone: "",
                body: "The main search field matches keywords across folder paths and filenames together.",
                example: "Try 2024 taxes pdf to find Taxes\\2024\\return.pdf.",
            },
            Card {
                heading: "Combine keywords and filters",
                badge: "", tone: "",
                body: "Use ext:pdf for PDFs, size:>10mb for large files, modified:7d for recent changes, or tag:work for tagged items.",
                example: "Try invoice ext:pdf modified:7d.",
            },
            Card {
                heading: "Filter and sort results",
                badge: "", tone: "",
                body: "Use the status filter to show online, offline, or removed items. Click a column header to sort and click it again to reverse the order.",
                example: "",
            },
            Card {
                heading: "Save searches and annotate files",
                badge: "", tone: "",
                body: "Use Saved Searches to save and reopen frequent queries. Select a file to edit its Tags & Notes inline.",
                example: "",
            },
        ],
    },
    Section {
        title: "Deep Search",
        subtitle: "Search inside documents by keywords, filters, or meaning",
        cards: &[
            Card {
                heading: "Index document contents",
                badge: "Ctrl+Shift+F", tone: "accent",
                body: "Open Deep Search and run a content scan to index TXT and code files, Office documents (DOCX, XLSX, PPTX, ODT), and PDFs.",
                example: "Choose Eco, Balanced, or Turbo to control resource use. Enable Skip system folders to skip common system and build directories.",
            },
            Card {
                heading: "Describe what you are looking for",
                badge: "EN + NL", tone: "teal",
                body: "Smart Search understands English and Dutch. Click any Active Filter chip to remove that filter and broaden the search.",
                example: "Try large pdf invoice from last month without draft or grote excel vorige maand.",
            },
            Card {
                heading: "Understand why a result matched",
                badge: "", tone: "",
                body: "The sortable Match column shows Exact for direct hits, Fuzzy or Close for typos and synonyms, Filename or Facet for names and metadata, and Concept for meaning. Excerpts highlight matched terms.",
                example: "For strict Boolean searches, use AND, OR, NOT, -exclude, \"exact phrase\", or (group).",
            },
            Card {
                heading: "Set up search by meaning",
                badge: "", tone: "",
                body: "In the Linguistic Search row, choose English Pack or Multilingual (EN + NL) and select Download Pack. Advanced Linguistic Search indexes documents locally.",
                example: "Choose Update Index to include previously scanned files.",
            },
        ],
    },
    Section {
        title: "Drives & Cloud Sync",
        subtitle: "Manage drive catalogs and share snapshots",
        cards: &[
            Card {
                heading: "Manage drive inventories",
                badge: "", tone: "",
                body: "Open Drive Library to edit location notes and review freshness alerts. Use Remove drive in Drive Library or Drive Scope to remove a drive's indexed inventory.",
                example: "",
            },
            Card {
                heading: "Exchange snapshots",
                badge: "", tone: "",
                body: "Use .fiinventory snapshots to export and import file and folder catalogs. Use .ficontent snapshots for Deep Search indexes.",
                example: "",
            },
            Card {
                heading: "Sync across PCs",
                badge: "1-Click", tone: "accent",
                body: "In Sync settings, choose a folder managed by Google Drive, OneDrive, Dropbox, or iCloud. Select Sync now to exchange snapshots.",
                example: "For optional AES-256-GCM encryption, use the same password on each PC.",
            },
        ],
    },
    Section {
        title: "Duplicates, Changes & Map",
        subtitle: "Find copies, review scans, and see what uses space",
        cards: &[
            Card {
                heading: "Check for duplicates",
                badge: "SHA-256", tone: "teal",
                body: "Open Duplicates to find possible copies across drives. Use SHA-256 verification to check whether files have identical contents.",
                example: "",
            },
            Card {
                heading: "Compare scans",
                badge: "", tone: "",
                body: "Open Changes to compare scans in Scan History. Added files appear in green, modified files in amber, removed files in red, and moved files in violet.",
                example: "",
            },
            Card {
                heading: "Explore the Storage Map",
                badge: "Ctrl+M", tone: "accent",
                body: "Larger rectangles represent larger files or folders. Use the interactive map to spot what takes up the most space.",
                example: "",
            },
        ],
    },
    Section {
        title: "Shortcuts & Updates",
        subtitle: "Navigate with the keyboard and keep the app current",
        cards: &[
            Card {
                heading: "Keyboard shortcuts",
                badge: "", tone: "",
                body: "Ctrl+K  Command Palette\nCtrl+Shift+F  Deep Search\nCtrl+M  Storage Map\nF1  Help & User Guide\nEsc  Close the current dialog",
                example: "",
            },
            Card {
                heading: "Switch themes",
                badge: "", tone: "",
                body: "Use Theme to switch among Light, Dark, Dark Blue, and System.",
                example: "",
            },
            Card {
                heading: "Keep File Inventory up to date",
                badge: "", tone: "",
                body: "File Inventory can automatically check for updates at startup. When an update is available, choose Download & Install Update. The app verifies the download with SHA-256 before running the installer.",
                example: "",
            },
        ],
    },
];

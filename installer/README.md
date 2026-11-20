# Windows installer

Run `FileInventory_Setup_x64.exe` and follow the setup wizard. The installer puts the self-contained app in `%LOCALAPPDATA%\Programs\File Inventory` and adds **File Inventory** to the Start menu. The installer runs for the current user without administrator rights.

Uninstall via **Settings → Apps**, or run `unins000.exe` from the install directory. Close File Inventory first if you have a scan running. The uninstaller removes the installed app and shortcut. Your offline inventory, catalog, and settings remain in `%LOCALAPPDATA%\FileInventory` for future installations.

To build the installer, publish the Windows x64 app as described in the project README, then use Inno Setup 6.7.4 or later. From the project directory, run:

```powershell
ISCC.exe "/DPublishDir=C:\absolute\path\to\publish" .\installer\FileInventory.iss
```

The default output is `installer\output\FileInventory_Setup_x64.exe`. Pass `/DBuildVersion=1.0.1` (or the next release version) and retain the `AppId` to build an update. Pass `/DBuildOutput=C:\absolute\output\path` to choose a different output folder. The published app is unsigned unless Authenticode signing is configured separately.

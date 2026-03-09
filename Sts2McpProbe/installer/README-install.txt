Sts2Mcp one-click installer (Windows)

1) Extract package, then run:
   Install-Sts2Mcp.bat

2) Installer will:
   - Detect Steam directory
   - Detect Slay the Spire 2 directory via appmanifest_2868840.acf
   - Backup existing mods\Sts2Mcp to Sts2Mcp.backup.<timestamp>
   - Copy Sts2Mcp.dll and Sts2Mcp.pck

3) If auto-detection fails, pass game path manually:
   Install-Sts2Mcp.bat "D:\SteamLibrary\steamapps\common\Slay the Spire 2"

4) Uninstall:
   Uninstall-Sts2Mcp.bat

5) Uninstall and restore latest backup:
   Uninstall-Sts2Mcp.bat -RestoreLatestBackup

Note:
- Close the game before install/uninstall.
- Overlay toggle hotkey: F8

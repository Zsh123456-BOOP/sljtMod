Sts2Mcp one-click installer (Windows)

1) Extract package, then run:
   Install-Sts2Mcp.bat

2) Installer will:
   - Detect Steam directory
   - Detect Slay the Spire 2 directory via appmanifest_2868840.acf
   - Replace existing mods\Sts2Mcp directly (no backup)
   - Copy Sts2Mcp.dll and Sts2Mcp.pck
   - If game process is running, prompt whether to auto-close it (Y/N)

3) If auto-detection fails, pass game path manually:
   Install-Sts2Mcp.bat "D:\SteamLibrary\steamapps\common\Slay the Spire 2"

Auto-close game process without prompt:
   Install-Sts2Mcp.bat -AutoCloseGame

4) Uninstall:
   Uninstall-Sts2Mcp.bat

Uninstall with auto-close game process:
   Uninstall-Sts2Mcp.bat -AutoCloseGame

Note:
- Close the game before install/uninstall.
- Overlay toggle hotkey: F8

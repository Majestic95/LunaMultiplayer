Please, BEFORE asking any question check the wiki:
https://github.com/LunaMultiplayer/LunaMultiplayer/wiki

Installation:

---------------------

Client side:
- Copy the contents of the zip file to KSP folder. So in KSP/GameData folder you have both "LunaMultiPlayer" and "000_Harmony" folders

DO NOT put LMPServer in your GameData folder!!!

Server side:
- Copy the folder "LMPServer" to any folder of your choice EXCEPT the KSP folder. Put it preferably on C:/ or in your Desktop

PlayerUpdater (Windows only - fork-only tool, NOT in upstream):
- Two flavours are shipped alongside this release:
    LunaMultiplayer-PlayerUpdater-win-x64-Release.exe              (~5 MB, needs .NET 10 Desktop Runtime)
    LunaMultiplayer-PlayerUpdater-win-x64-selfcontained-Release.zip (~70 MB, bundles the runtime)
  Pick the .exe if you already have .NET 10 installed; pick the .zip if you don't or
  aren't sure. Double-click the .exe (or extract the .zip and double-click the .exe inside)
  to detect your KSP install, check for newer Majestic95/LunaMultiplayer releases, and
  install / roll back without manual zip-fiddling.

  The PlayerUpdater REPLACES the in-game "check for updates" prompt (the in-game UI
  was removed on this fork). If you happen to be running a much older LMP build that
  still has the in-game updater visible, do NOT use it - it points at upstream
  LunaMultiplayer and would downgrade you to a different build. Always update via the
  PlayerUpdater above.

--------------------

Remember: We cannot provide support for other mods in a multiplayer environment so if you have other mods besides LMP expect issues!

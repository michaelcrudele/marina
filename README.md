# Marina

*Formerly HarbourLauncher.* On first start Marina moves its data from
`%LOCALAPPDATA%\HarbourLauncher` to `%LOCALAPPDATA%\Marina`, renames each game's
`.harbourlauncher` folder to `.marina`, and still opens `.hlpack` / `.hlbackup`
files made before the rename.

Installs, updates and launches the HarbourMasters PC ports on Windows:
Ship of Harkinian, 2 Ship 2 Harkinian, Starship, SpaghettiKart, Ghostship,
Lighthouse and PaperBoat.

The launcher never downloads or bundles game ROMs. Each port still asks the
player for their own copy of the game, exactly as it does without the launcher.

## Download

1. Go to the [latest release](../../releases/latest) on this repo's
   **Releases** page.
2. Under **Assets**, download `Marina.exe`.
3. Put `Marina.exe` in any folder you like (for example a `Marina` folder in
   Documents) and double-click it. There's no installer, and you don't need to
   install .NET; everything is inside the one file.

Marina isn't code-signed, so the first time you run it Windows SmartScreen may
say it "protected your PC". Click **More info**, then **Run anyway**.

To check the download wasn't corrupted or tampered with, compare its hash with
the `Marina.exe.sha256` file from the same release:

```
certutil -hashfile Marina.exe SHA256
```

Marina keeps itself up to date. When a new version comes out, it asks when it
starts; click **Update and restart**. You can also check any time under
**Settings > Marina updates**. The update is checked against its SHA-256 before
it's installed, and your settings and games are stored separately, so nothing
is lost.

## Layout

```
src/HarbourLauncher.Core      Everything that matters, with no package dependencies
  Catalog/ports.json          The ports: repo, Windows asset pattern, exe, skip/keep rules
  GitHub/                     Latest-release lookup (ETag-cached), verified downloads
  Install/PortInstaller.cs    Staged, backed-up, roll-back-on-failure installs and updates
  Launch/GameLauncher.cs      Start a port; detect if it's running
  PortManager.cs              The API the UI calls: Check, InstallLatest, Launch
src/HarbourLauncher.Desktop   The launcher app (Avalonia, dark Fluent theme) over PortManager
src/HarbourLauncher.Cli       `marina` command-line front end over PortManager
tests/HarbourLauncher.Core.Tests   Dependency-free test runner (dotnet run)
```

## Build and try it

Requires the .NET 10 SDK.

Start the launcher app (the first build downloads Avalonia from NuGet):

```
dotnet run --project src/HarbourLauncher.Desktop
```

Or use the command line:

```
dotnet run --project tests/HarbourLauncher.Core.Tests
dotnet run --project src/HarbourLauncher.Cli -- check
dotnet run --project src/HarbourLauncher.Cli -- install soh
dotnet run --project src/HarbourLauncher.Cli -- launch soh
```

Games install to `%LOCALAPPDATA%\Marina\ports\<id>` by default. To use
another folder, open **Settings** (the gear in the app) or run
`marina folder "<your games folder>"`, using whatever folder you want your
games in (for example `marina folder "D:\Games\HarbourMasters"`). Each game gets its own
subfolder there (`soh`, `2ship`, ...), and games already installed can move
along with their saves and mods. The choice is saved in
`%LOCALAPPDATA%\Marina\settings.json`. GitHub allows 60
unauthenticated checks per hour per IP; set `GITHUB_TOKEN` to raise that.

## Games you installed yourself

When the launcher starts (or the games folder changes), it looks in the games
folder for games it didn't install: any subfolder with a port's exe at its top
level. If it finds some, it asks whether to add them to the library. Adding
one writes only a small record in `.marina`; saves, mods and settings
stay where they are. A folder named something other than the port id (say
`Ship of Harkinian`) is renamed to the id (`soh`). The version shows as
unknown until the first update. "Not now" is remembered; **Settings > Look for
games** (or `marina scan --add`) offers them again.

## Custom artwork

Each game can show your own icon or box art instead of its colour banner:
**Settings > Game artwork**, right-click a game's card, or
`marina artwork soh "C:\path\to\image.png"` (`none` to remove). PNG, JPEG, BMP
and WebP work. The image is copied to `%LOCALAPPDATA%\Marina\artwork`,
so the original can be moved or deleted. The launcher doesn't ship any artwork.

## Uninstalling

Right-click a game and choose **Uninstall…**. The dialog shows the folder and
its size and offers to save a `.marinabackup` first (ticked by default); if the
backup is cancelled or fails, nothing is uninstalled. Uninstalling deletes the
whole game folder (game, saves, settings, mods, ROM data). Custom artwork is
kept for a reinstall. Command line: `marina uninstall <port> --yes [--backup <file>]`.

## Backups for your own PCs (.marinabackup)

Right-click a game and choose **Back up…** to save the whole game to one
`.marinabackup` file, for example on a USB drive when travelling. On another PC,
open it with the import button (or "Open with" the launcher, or
`marina restore <file>`), and the game is restored ready to play.
`marina backup <port> [file]` does the same from the command line.

A backup holds everything a shared pack leaves out: the game data made from
your ROM (`pm64.o2r`, ...), plus the game, saves, settings, mods, artwork and
the launcher's install record. Logs, debug symbols and the launcher's own
update backups are skipped.

- The launcher checks the drive has room first, and warns if a FAT32 USB stick
  can't hold a backup of 4 GB or more (reformat as exFAT).
- Restoring never deletes a game that's already there: its folder is renamed
  to `<id> (before restore <date>)` and the backup goes in its place. A stopped
  or failed restore changes nothing.
- Backups and packs are deliberately different files: a `.marinapack` is safe to
  give to anyone, a `.marinabackup` is for your own PCs.

## Sharing a game (.marinapack)

Right-click an installed game and choose **Share game…** to pick what goes in
(the game, settings, each save folder, each mod, artwork) with sizes, then save
a `.marinapack` file. From the command line: `marina export <port> --list` shows
the choices; `marina export <port> [file] [--no-game] [--no-mods]` saves one.
Already-compressed files such as texture packs are stored as-is, so big packs
are quick to create. Someone else imports
it with the import button next to Settings (or `marina import <file>`, or by
starting the launcher with the file, e.g. "Open with").

A `.marinapack` is a zip with a `manifest.json`, the game's program files under
`program/` (exactly what its release installed), the player's files under
`game/` (saves, settings, mods, randomizer files), and their custom artwork.

- **Not installed on the importing PC**: the game is installed from the pack,
  same version as the sharer's, with no download. Updating it later is up to
  the player. If the pack has no program files (a game added from the games
  folder that was never updated through the launcher), it's downloaded instead.
- **Already installed**: the pack's program files are ignored and the
  installed version stays; only the player's files are added.
- **No game data, ever**: ROMs and the archive each port generates from one
  (`bk.o2r`, `pm64.o2r`, ...) are never packed. Whoever imports a pack uses
  their own ROM.
- **Programs only in the program part**: the player's part may not contain
  `.exe`, `.dll` or scripts, and the program part may only contain the files
  its manifest lists. Imports refuse packs that break either rule. Programs
  installed from a downloaded pack keep Windows' "downloaded from the
  internet" mark, so Windows still asks before running them the first time.

Files an import replaces are moved to `.marina\import-backup`, and a
failed import puts them back. The rules are in `Sharing/ShareRules.cs`.

## How updates keep player data safe

The installer only writes files that are in the release zip, and only removes
files that the previous release installed (it records that list in
`.marina/state.json`). Saves, the `.o2r` archive generated from the
player's ROM, mods and settings are never in a release zip, so they are never
touched.

Each port's own archive (`soh.o2r`, `2ship.o2r`, ...) *is* in the release and
is replaced on update, as it must be.

An update extracts to a staging folder first, moves every replaced or removed
file into `.marina/backup`, and rolls everything back if any step fails.
The backup holds the previous version until the next update.

For a release file players may edit, add it to that port's `keep` list in
`ports.json`; it is written on first install and left alone afterwards.

## Adding a port

Add an entry to `Catalog/ports.json`. `windowsAsset` must match exactly one
asset in each release; `CatalogTests.EachPatternPicksExactlyTheWindowsZipFromRealReleases`
holds real asset names to check patterns against.

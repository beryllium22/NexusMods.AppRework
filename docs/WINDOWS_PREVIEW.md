# App Rework — Windows development preview

Windows x64 is the primary development target. This is an unsigned development
build of an independent community continuation of the Nexus Mods App. It is not
an official Nexus Mods release or a stable release of this fork.

## Download and run

Install the latest [Microsoft Visual C++ v14 Redistributable for x64](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)
if it is not already installed. The native database library needs this Windows
runtime in addition to the .NET runtime included in the ZIP.

1. Open the latest successful **Clean Environment Tests** run for the development
   branch in [GitHub Actions](https://github.com/beryllium22/NexusMods.AppRework/actions/workflows/clean_environment_tests.yaml).
2. Download the `app-rework-windows-x64` artifact. GitHub may require sign-in for
   artifact downloads. The artifact contains a preview ZIP and its SHA-256 file.
3. Extract the preview ZIP into a folder you own. Keep all its files together.
4. Run `NexusMods.App.exe`. The executable name is inherited; this build uses
   separate App Rework storage. Its .NET runtime is included.

Builds are unsigned and may trigger Windows reputation warnings. Check the
repository, commit, and checksum before deciding whether to run a preview.
`BUILD-INFO.json` identifies the source commit and the exact build commit tested
by CI; pull requests are tested together with the target branch.

To compare the ZIP with its checksum file in PowerShell:

```powershell
Get-FileHash .\AppRework-windows-x64-<commit>.zip -Algorithm SHA256
```

Use a disposable game installation for mod install/apply testing. A passing
automated suite does not certify a particular installed game, mod, or collection.
Separate app storage does not prevent two mod managers from modifying the same
game files; manage each test installation with one manager at a time.

## Storage and existing installations

Fresh defaults are under `%LOCALAPPDATA%\NexusMods.AppRework`, including settings,
database, downloaded archives, logs, Steam authentication storage, and the
single-process synchronization file. Temporary extraction data also uses an
App Rework directory under the system temporary folder.

The preview does not import the original app's settings or database. Existing
explicitly configured paths still take effect if you deliberately copy settings;
automatic migration and repair of old collection records are not implemented.

To remove the portable binaries, close the app and delete the extracted folder.
This leaves application data and any applied game changes in place. Revert
managed game changes from the app before discarding its data. No inherited
installer/uninstaller scripts are included in this ZIP.

## Nexus Mods links and browser sign-in

Startup leaves your current `nxm://` association unchanged. Before using browser
sign-in or Nexus **Mod Manager Download** links with this preview:

1. Open **Settings → General**.
2. Enable **Open Nexus Mods links in this app** and save the setting.

Enabling the setting makes this app the current handler for Nexus download and
sign-in links. Disabling it stops this app from registering the handler again;
use your other mod manager's association setting to switch back.

For developers, the existing explicit command is also available:

```powershell
.\NexusMods.App.exe as-main associate-nxm
```

Run the command while the app is closed. It associates links once; the setting
controls whether the app registers on future starts.

## Profiles and experimental games

Use **Profiles** to name, duplicate, rename, search, and organize mod setups. A duplicate preserves collections, conflict priorities, and game load orders while starting unapplied. Open the profile to manage mods and apply changes before launching. Profiles do not automatically isolate game saves.

On **My Games**, enable **Show experimental games** to expose Skyrim SE/Anniversary, Fallout 4, Witcher 3, and other experimental integrations. The setting updates the page immediately; experimental labels remain visible. Start with a disposable installation and a small local mod archive. The [feature and compatibility guide](FEATURES_AND_COMPATIBILITY.md) describes archive formats, dependency validation, restoration behavior, and current limits.

## Updates and test coverage

Preview builds use manual update mode. They do not offer the original app's
release downloads as updates. Download a newer successful preview when needed.

The Windows pipeline builds the solution, runs the eligible automated tests,
publishes the x64 app with its runtime, and launches its native executable to
check service/database startup and actual game-locator construction/enumeration.
Zero installed games is a valid discovery result. Both checks also verify that
original application data and the current Nexus link association remain unchanged. Interactive GUI,
browser authentication, and real-game install/apply/launch checks remain separate
validation work. See the [stability record](STABILITY.md) for addressed failures,
regression coverage, and remaining limitations.

To run the read-only discovery check locally while the app is closed:

```powershell
.\NexusMods.App.exe as-main diagnose-game-discovery
```

A successful run prints `GameDiscovery: OK` after enumerating all registered
locators. Include its output and the app log when reporting a discovery problem.

Source, issues, and development progress:
[beryllium22/NexusMods.AppRework](https://github.com/beryllium22/NexusMods.AppRework).
See `LICENSE.md`, `PORTS-AND-ROADMAP.md`, `STABILITY.md`, `FEATURES_AND_COMPATIBILITY.md`, and `BUILD-INFO.json` in the preview for
licensing, port attribution, and build provenance.

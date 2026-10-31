# App Rework

An independent community continuation of the archived [Nexus Mods App](https://github.com/Nexus-Mods/NexusMods.App). **Windows x64 is the primary development target.** Existing Linux checks help preserve shared-code compatibility.

This repository is `beryllium22/NexusMods.AppRework`. It is not an official Nexus Mods product. **App Rework is a working title**; a distinct public identity, distribution, and data migration plan are required before a public release.

## Current status

**Development alpha.** The inherited application contains a native Avalonia interface, mod library, loadouts, collections, downloads, game discovery, installers, and file conflict handling. The rework is improving these workflows and expanding experimental Windows game compatibility.

Continuation changes address interrupted HTTP downloads, game-file queries, collection installation and copying, publication-state persistence, and independent build checks. Fresh defaults use separate App Rework storage, and Nexus link registration is opt-in. See the [roadmap and port record](docs/REWORK_ROADMAP.md) for scope, provenance, and release gates.

The original project documented support for Stardew Valley, Cyberpunk 2077, Baldur's Gate 3, and Mount & Blade II: Bannerlord. These are inherited integrations, **not a fresh compatibility certification for current game versions**. Skyrim Special Edition/Anniversary and Fallout 4 now have experimental archive installation and plugin validation, and Witcher 3 has an initial Mods/DLC integration. See the [feature and compatibility guide](docs/FEATURES_AND_COMPATIBILITY.md) for implemented behavior and boundaries.

The **Profiles** page adds named creation, duplication, renaming, search, sorting, and live apply status. Collection installs preserve completed work for retry, and FOMOD sessions validate their recorded choices. Enable **Show experimental games** on My Games to find the new game integrations. These changes remain subject to real-game validation.

## Try the Windows development preview

Successful Windows CI jobs publish an unsigned, self-contained x64 ZIP with the .NET runtime included. Follow the [Windows preview guide](docs/WINDOWS_PREVIEW.md) for download instructions, separate storage, Nexus sign-in/link setup, and validation limits.

The preview is for testing with disposable game installations. Automated startup and fixture tests do not certify real-game modding, and two managers can still conflict if they apply changes to the same game folder.

## Build and contribute

Install a stable [.NET 9 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/9.0), then:

```sh
git clone --recurse-submodules https://github.com/beryllium22/NexusMods.AppRework.git
cd NexusMods.AppRework
dotnet restore NexusMods.App.sln
dotnet build NexusMods.App.sln --configuration Release --no-restore
```

The first build also downloads public game metadata and hashes. It does not require a Nexus Mods account. See the [contributor guide](docs/developers/Contributing.md) for running the app, platform dependencies, and the separate xUnit/TUnit test commands.

Fresh settings and application data use `NexusMods.AppRework` directories; the original app's database is not imported. Public branding, API identity, signed installation, updates, and data migration remain release work. Development ZIPs use manual update mode and exclude the inherited uninstall helpers.

## Development priorities

1. Reliable downloads, installation, recovery, and reproducible builds.
2. Clear onboarding, actionable errors, understandable conflicts, and persistent work.
3. Verified game and store compatibility with a repeatable integration test process.
4. Measured performance with large mod libraries and a maintainable game extension interface.

Community fixes are reviewed and ported individually, with attribution and regression coverage. [Apocrypha](https://github.com/Jeagermeister/Apocrypha) is a valuable continuation to learn from; its platform, schema, and infrastructure changes need their own review before adoption here.

## License and credits

See [LICENSE.md](LICENSE.md). The inherited code and selected Apocrypha fixes are GPLv3. Original copyright and license notices remain in place. The [port record](docs/REWORK_ROADMAP.md#selected-community-work) identifies the commits and contributors behind reused work.

The [archived upstream documentation](https://github.com/Nexus-Mods/NexusMods.App/wiki) remains useful background, but its release promises, support channels, and current compatibility claims do not apply to this fork.

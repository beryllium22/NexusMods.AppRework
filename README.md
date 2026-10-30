# App Rework

An independent community continuation of the archived [Nexus Mods App](https://github.com/Nexus-Mods/NexusMods.App), with Windows and Linux as development targets.

This repository is `beryllium22/NexusMods.AppRework`. It is not an official Nexus Mods product. **App Rework is a working title**; a distinct public identity, distribution, and data migration plan are required before a public release.

## Current status

**Development alpha.** The inherited application contains a native Avalonia interface, mod library, loadouts, collections, downloads, game discovery, installers, and file conflict handling. The rework starts by making these workflows reliable and maintainable.

The first continuation changes address interrupted HTTP downloads, a game-file database query, a Cyberpunk 2077 Linux diagnostic, and independent Windows/Linux build checks. See the [roadmap and port record](docs/REWORK_ROADMAP.md) for scope, provenance, and release gates.

The original project documented support for Stardew Valley, Cyberpunk 2077, Baldur's Gate 3, and Mount & Blade II: Bannerlord. These are inherited integrations, **not a fresh compatibility certification for current game versions**. Skyrim Special Edition and Fallout 4 code is experimental and incomplete.

## Build and contribute

Install a stable [.NET 9 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/9.0), then:

```sh
git clone --recurse-submodules https://github.com/beryllium22/NexusMods.AppRework.git
cd NexusMods.AppRework
dotnet restore NexusMods.App.sln
dotnet build NexusMods.App.sln --configuration Release --no-restore
```

The first build also downloads public game metadata and hashes. It does not require a Nexus Mods account. See the [contributor guide](docs/developers/Contributing.md) for running the app, platform dependencies, and the separate xUnit/TUnit test commands.

Use disposable game installations and separate test data during development. Storage paths, application identifiers, telemetry settings, update endpoints, and packaging still include inherited upstream behavior; this branch is not a ready-to-install public release.

## Development priorities

1. Reliable downloads, installation, recovery, and reproducible builds.
2. Clear onboarding, actionable errors, understandable conflicts, and persistent work.
3. Verified game and store compatibility with a repeatable integration test process.
4. Measured performance with large mod libraries and a maintainable game extension interface.

Community fixes are reviewed and ported individually, with attribution and regression coverage. [Apocrypha](https://github.com/Jeagermeister/Apocrypha) is a valuable continuation to learn from; its platform, schema, and infrastructure changes need their own review before adoption here.

## License and credits

See [LICENSE.md](LICENSE.md). The inherited code and selected Apocrypha fixes are GPLv3. Original copyright and license notices remain in place. The [port record](docs/REWORK_ROADMAP.md#selected-community-work) identifies the commits and contributors behind reused work.

The [archived upstream documentation](https://github.com/Nexus-Mods/NexusMods.App/wiki) remains useful background, but its release promises, support channels, and current compatibility claims do not apply to this fork.

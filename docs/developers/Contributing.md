## Code Contributions

### Prerequisites

- A stable [.NET 9 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/9.0).
  `global.json` selects the newest installed .NET 9 feature band and patch, and excludes preview SDKs and other major versions.
- An IDE:
    - [JetBrains Rider](https://www.jetbrains.com/rider/) and the [Avalonia Rider Extension](https://plugins.jetbrains.com/plugin/14839-avaloniarider),
    - [Visual Studio](https://visualstudio.microsoft.com/downloads/) and the [Avalonia Visual Studio Extension](https://marketplace.visualstudio.com/items?itemName=AvaloniaTeam.AvaloniaVS).

There are also some [Item Templates](https://github.com/AvaloniaUI/avalonia-dotnet-templates) for Avalonia that can be very useful to have,
helping you make things like new 'Windows' and 'Controls' easier.

### Running the project

Windows x64 is the primary development target for App Rework. See the
[Windows preview guide](../WINDOWS_PREVIEW.md) for an already-built development
ZIP, storage behavior, and opting in to Nexus download/sign-in links.

- Clone the repository (optionally make a fork first)
- Ensure submodules have been cloned as well, you may use `git submodule update --init --recursive` to do so
- Build and run the `NexusMods.App` project from your IDE to start the app, or use
  `dotnet run --project src/NexusMods.App/NexusMods.App.csproj `

The first restore and build need internet access to download NuGet packages, the
public Nexus Mods game catalog, and the game hashes database. These downloads do
not require a Nexus Mods API key. The SMAPI source is pinned by the submodule
commit; do not update the submodule to its latest branch just to build the app.

### Building and testing the fork

From the repository root:

```sh
git submodule update --init --recursive
dotnet restore NexusMods.App.sln
dotnet build NexusMods.App.sln --configuration Release --no-restore
```

The solution includes large native dependencies for several operating systems.
Copying them into every test project's output can consume over 20 GB. On a
space-constrained build machine, MSBuild can hardlink compiled outputs and package
dependencies instead:

```sh
dotnet build NexusMods.App.sln --configuration Release --no-restore -p:CreateHardLinksForCopyFilesToOutputDirectoryIfPossible=true -p:CreateHardLinksForCopyLocalIfPossible=true
```

The two switches cover different copy tasks: the first handles a project's own
assembly and symbols; the second handles referenced assemblies and native NuGet
assets. The NuGet package cache and output directories must be on the same
filesystem for hardlinks to work. CI sets `NUGET_PACKAGES` to
`${{ github.workspace }}/.nuget/packages` before restoring, so this also works on
Windows runners whose default package cache is on a different drive. MSBuild
[falls back to normal copies](https://learn.microsoft.com/en-us/visualstudio/msbuild/copy-task)
when linking is unavailable. Source and test-fixture copy settings are unchanged.
Use normal copies when a workflow needs to edit dependency binaries in place.

Run an xUnit test project with the clean-environment filter, for example:

```sh
dotnet test tests/Networking/NexusMods.Networking.HttpDownloader.Tests/NexusMods.Networking.HttpDownloader.Tests.csproj --configuration Release --no-build --no-restore --filter "RequiresNetworking!=True&RequiresApiKey!=True&FlakeyTest!=True"
```

This excludes tests marked as requiring a live service or API key, and tests
already marked as flaky. Game integration fixtures in the clean suite use
stubbed installations; a passing suite does not certify a real game installation
or collection. Live downloads and installed-game checks remain separate manual
or integration validation.

The SDK and backend test projects use TUnit instead of xUnit. Run their executable
test runners directly; the xUnit `--filter` syntax does not apply to them:

```sh
dotnet run --project tests/NexusMods.Sdk.Tests/NexusMods.Sdk.Tests.csproj --configuration Release --no-build --no-restore
dotnet run --project tests/NexusMods.Backend.Tests/NexusMods.Backend.Tests.csproj --configuration Release --no-build --no-restore
```

The fork's **Clean Environment Tests** workflow builds the solution and runs its
registered test projects on Windows and Linux, including draft pull requests. It uses the
filter above for xUnit and direct execution for TUnit's local-fixture tests.
Failures are reported by project, and xUnit result files and failed snapshots
are kept as workflow artifacts. No signing, publishing, or service credentials
are required for this workflow.

After the Windows tests pass, the job publishes a self-contained x64 app and
executes `NexusMods.App.exe as-main heartbeat`. This checks the published native
host, service/database startup, and separate default data paths; it also verifies
that startup leaves the original data directory and `nxm` registration unchanged.
Successful jobs upload an unsigned development ZIP and SHA-256 checksum. This
does not replace interactive GUI or real-game testing.

To publish the same portable layout locally from PowerShell 7 on Windows:

```powershell
./.github/scripts/publish-windows-preview.ps1
```

Use a new output directory for each publish. The startup-isolation check script
is restricted to disposable GitHub Actions runners. Do not set an
`INSTALLATION_METHOD_*` define for development ZIPs: manual installation mode
keeps them from offering the original application's releases as updates.

### Writing code

Make sure to follow our [Development Guidelines](./development-guidelines/UICodingGuidelines.md).

### Debugging the App
This app is designed to be used with [games](../users/games/index.md) that are currently supported. It is recommended to
have a supported game installed on your system to debug the app with full functionality.

If you want to contribute to the app without a supported game installed, you can continue
doing so by manually adding a supported game. However, supported games that are manually added and not installed properly for 
testing purposes may not be playable. You can still use the app to test the UI and other features, 
but some features may not work as expected. To add a supported game manually:

1. Have a directory ready and copy its path for the game you want to add to store its mods.
2. After building the project, go to your terminal and change the directory to the `src/NexusMods.App/bin/Debug/net9.0` folder.
3. Enter the following CLI command and replace the arguments: 
   ```bash
   NexusMods.App as-main add-game -g "Game Name" -v [Version] -p "Path"
   ```
4. Save the `EntityID` provided and run the app to check if the game is listed in the "My Games" section.
5. Later on, you can remove the game by running the following CLI command:
   ```bash
   NexusMods.App as-main remove-game -g "Game Name" -id "EntityID"
   ```

### Testing with Nexus API Integration

Some tests in the project require a valid Nexus Mods API key to run properly.
These tests are marked with the `RequiresApiKey` trait and will fail if the API key is not set.

#### Obtaining a Nexus API Key

1. Go to your [API Key management page](https://www.nexusmods.com/users/myaccount?tab=api)
2. Generate a new API key for development/testing purposes

#### Setting Up the Environment Variable

To run networking tests locally, set the `NEXUS_API_KEY` environment variable.

## Translations

Translations are currently handled via the IDE. See [this issue](https://github.com/Nexus-Mods/NexusMods.App/issues/598) for more details.

## For Package Maintainers

If you want to create a package for your distribution, here are some helpful tips to get started. If you have questions or want to let us know about your new package, consider joining our [Discord](https://discord.gg/ReWTxb93jS).

If possible, use `nexusmods-app` for the package name and `com.nexusmods.app` as the rDNS ID for Flatpaks and similar. See [Linux Dependencies](../users/SystemRequirements.md#linux-dependencies) for a list of dependencies.

There are various build variables you should consider using. You can set compile constants using `-p:DefineConstants="NAME"` with `dotnet build` or `dotnet publish`. For a full list of available application-specific compile constants, see [`Directory.Build.props`](https://github.com/Nexus-Mods/NexusMods.App/blob/main/Directory.Build.props):

- `NEXUSMODS_APP_USE_SYSTEM_EXTRACTOR`: We ship a build of `7zz` that you can disable. Setting this constant will force the code to use `7zz` available in `PATH`. See [#1306](https://github.com/Nexus-Mods/NexusMods.App/issues/1306#issuecomment-2095755699) for details. Do note that some versions of `7zz` don't support RAR files, due to licensing issues. The build that the App ships with supports RAR files. Many mods still come in RAR archives, for a better user experience, we expect `7zz` to support RAR files. We won't accept issues around extraction failures for builds that don't support RAR archives.
- `INSTALLATION_METHOD_PACKAGE_MANAGER`: This constant will prevent the App from generating a `.desktop` file at runtime, and will change the update notification that notifies the user about new versions. If this constant is set, the App will tell the user to update using their package manager.

We publish the App using [PupNet](https://github.com/kuiperzone/PupNet-Deploy). Releases are built using GitHub Actions, see [`build-linux-pupnet.yaml`](https://github.com/Nexus-Mods/NexusMods.App/blob/main/.github/workflows/build-linux-pupnet.yaml) for details. PupNet will use `dotnet publish` before packaging the result in some specialized format.

If you don't wish to use PupNet, you should still prefer `dotnet publish` over `dotnet build`. The arguments we use with `dotnet publish` can be found in [`app.pupnet.conf`](https://github.com/Nexus-Mods/NexusMods.App/blob/main/src/NexusMods.App/app.pupnet.conf). Also see the [`dotnet publish` documentation](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-publish#options) for a list of options.

We provide a [Desktop Entry](https://specifications.freedesktop.org/desktop-entry-spec/desktop-entry-spec-latest.html) file called [`com.nexusmods.app.desktop`](https://github.com/Nexus-Mods/NexusMods.App/blob/main/src/NexusMods.App/com.nexusmods.app.desktop). When building the package, you should use this file and replace `${INSTALL_EXEC}` with an absolute path to the `NexusMods.App` binary.

The App will [generate](https://github.com/Nexus-Mods/NexusMods.App/blob/main/src/NexusMods.CrossPlatform/ProtocolRegistration/ProtocolRegistrationLinux.cs) this `.desktop` file and replace the placeholder with the absolute path to the executable if `INSTALLATION_METHOD_PACKAGE_MANAGER` is not set.

Besides the Desktop Entry file, we also provide the following files that you should make use of, if possible:

- [AppStream](https://www.freedesktop.org/software/appstream/docs/) [`com.nexusmods.app.metainfo.xml`](https://github.com/Nexus-Mods/NexusMods.App/blob/main/src/NexusMods.App/com.nexusmods.app.metainfo.xml)
- [`icon.svg`](https://github.com/Nexus-Mods/NexusMods.App/blob/main/src/NexusMods.App/icon.svg) and [`icon.ico`](https://github.com/Nexus-Mods/NexusMods.App/blob/main/src/NexusMods.App/icon.ico)

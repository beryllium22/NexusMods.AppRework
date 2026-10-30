# App Rework: continuation plan

Reviewed on **2026-10-30**. This is a development plan and provenance record, not a claim of release readiness.

## Starting point

- Repository: [beryllium22/NexusMods.AppRework](https://github.com/beryllium22/NexusMods.AppRework).
- Baseline: [`48284f38c2d91e53904b41093090fbaa8b5113ed`](https://github.com/beryllium22/NexusMods.AppRework/commit/48284f38c2d91e53904b41093090fbaa8b5113ed), dated 2025-11-19. The fork initially had one branch and no additional development commits.
- Nexus Mods [announced the end of App development on 2026-01-14](https://help.nexusmods.com/article/164-discontinuing-the-nexus-mods-app-faq). Its [repository](https://github.com/Nexus-Mods/NexusMods.App) was archived on 2026-02-20.
- The application uses C#/.NET 9 and Avalonia, with reactive UI/job services, MnemonicDB state, content-addressed archive storage, per-game installers and synchronizers, collections, FOMOD, and Steam/GOG/Epic-related discovery.
- The inherited user guide documents Stardew Valley, Cyberpunk 2077, Baldur's Gate 3, and Bannerlord. Creation Engine integrations remain unfinished: `MissingMasterEmitter.Diagnose(loadout, cancellationToken)` throws `NotImplementedException`, for example.

The first goal is a trustworthy Windows/Linux mod manager for an explicitly tested set of games. Additional game modules follow demonstrated end-to-end reliability.

## Selected community work

The strongest continuation found is [Jeagermeister/Apocrypha](https://github.com/Jeagermeister/Apocrypha), with contributions also made through [luismiramirez/Apocrypha](https://github.com/luismiramirez/Apocrypha). Its `linux-fork` head reviewed here is [`dfb9a77c4b516eb7dc279649e59639d1726bd749`](https://github.com/Jeagermeister/Apocrypha/commit/dfb9a77c4b516eb7dc279649e59639d1726bd749).

Apocrypha renamed namespaces/projects, changed application infrastructure, and removed Windows builds. A wholesale merge would import unrelated platform, persistence, and release decisions. This fork adopts reviewed changes individually. Both repositories retain GPLv3 licensing; imported source notices and attribution must be preserved.

### First implementation

| Source and contributor | Problem | Treatment here |
| --- | --- | --- |
| [b9075230](https://github.com/Jeagermeister/Apocrypha/commit/b9075230f4334efd9f5cde0484ec83f2e56bc848), Jeagermeister | A full HTTP response can be appended to a partial download. | Adapt restart behavior and add deterministic HTTP regression tests. |
| [590d8153](https://github.com/Jeagermeister/Apocrypha/commit/590d8153ad48549670221f8a1c9e8cc26e31d0e0), Jeagermeister | A short response can be reported as successful. | Check received byte counts before completion. Length checking does not prove cryptographic integrity. |
| [5b23c74f](https://github.com/Jeagermeister/Apocrypha/commit/5b23c74f50dccecca324e2913cb5a636c48815b0), Jeagermeister | The progress timer can run before resumed-byte counters initialize. | Initialize counters before scheduling callbacks and test an immediate first tick. |
| [18065d81](https://github.com/Jeagermeister/Apocrypha/commit/18065d819a17558a272a70a867e5f1c3eb5ad466), Jeagermeister | Game-file query selects seven columns for a six-field result. | Remove the redundant parent column; test unchanged and changed files. |
| [5a9b14ca](https://github.com/Jeagermeister/Apocrypha/commit/5a9b14ca572170c22ef3ffb7f793a9e6c37e541c), luismiramirez | Linux Protontricks diagnostic exits when REDmod exists. | Correct the condition and test REDmod present/absent. |

Downloader adaptations also address response range bounds, stale validators, and progress lifetime in this fork's namespace/job model. These are adaptations rather than identical cherry-picks. Servers without a matching strong entity validator restart a partial transfer instead of combining unvalidated responses.

The build foundation replaces the upstream reusable clean-test workflow with local Windows/Linux jobs, selects stable .NET 9, and handles xUnit and TUnit separately. Execution results belong in the associated pull request; configuration alone is not evidence of a passing build.

### Further ports to evaluate

1. Collection `TargetPath` loadout IDs and repair logic. Test cloning, installation, removal, migration, and recovery against existing databases before adopting repairs.
2. Interrupted loadout-switch recovery. Terminate between filesystem/database operations and verify that recovery preserves user files and the selected loadout.
3. FOMOD destination validation. Review traversal, absolute paths, Windows drives/UNC paths, alternate data streams, and symlinks across platforms.
4. Store/game discovery improvements. Require per-store fixtures and realistic game-version tests; advertised game counts are not verified compatibility.

For every port, record source SHA/license, inspect dependencies and migrations, retain attribution, and review both platforms. Never import credentials or assume access to another project's private infrastructure.

## Milestones and acceptance criteria

### 1. Maintainable and recoverable foundation — in progress

**Deliver:** independent build checks, reliable downloads, install/apply recovery, safe installer paths, and an independent release process.

- Windows/Linux compile from a clean checkout with documented public inputs and no developer credentials.
- Download failures, pauses, changed resources, ignored ranges, and truncated bodies cannot silently become successful corrupted archives.
- Install/apply failures preserve recoverable state and produce useful errors. This is a separate gate from HTTP tests.
- Review dependencies and bundled binaries. Initial restore reports advisory [GHSA-4625-4j76-fww9](https://github.com/advisories/GHSA-4625-4j76-fww9) for inherited `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.12.0; resolve before release.
- Establish public name, icons, package IDs, data paths, update feed, support destinations, telemetry behavior, and API identity, with an explicit data migration plan.
- Define maintenance/source policy for game hashes and game metadata. Their present upstream availability is not a long-term guarantee.

Nexus Mods requests a distinct community-fork name in its [FAQ](https://help.nexusmods.com/article/164-discontinuing-the-nexus-mods-app-faq). The repository name stays in place during development; public branding is a release decision. This first increment does not rename user data or migrate existing installations.

### 2. A clear everyday modding workflow

**Deliver:** guided discovery → install → review → apply → launch, durable jobs/history, actionable errors, and understandable conflicts.

- New users can add a game, choose storage, install a local archive or authorized Nexus download, and understand the next step.
- Returning users see queued, failed, paused, and completed work after restart.
- Conflicts identify the winning file, why it wins, what reordering changes, and how to revert the choice.
- Apply explains missing dependencies, disk space, external changes, and recovery options before an irreversible step.
- Verify collection metadata persistence: `LoadoutViewModel.UpdateCollectionInfo` was identified as a transaction path needing investigation.
- Implement and test Undo/rollback before advertising it. The inherited Undo page labels it as a future feature.

### 3. Verified game coverage and extension contracts

**Deliver:** a compatibility matrix covering game version, platform, store, installers, script extenders, load order, collections, and known limitations.

Each supported entry needs discover → install → apply → launch → update → uninstall → restore evidence. Use disposable installations, overlapping files, and existing user modifications; uninstall must preserve unrelated files.

Start with the inherited four documented integrations. Skyrim/Fallout need a separate milestone covering plugins, masters, archives, script extenders, downgrade/version constraints, and large collections. Registered game classes do not establish compatibility.

Define a versioned extension contract and reference module after identifying recurring adapter requirements. Evaluate external ecosystems module by module; do not promise drop-in Vortex extension compatibility without an implemented adapter.

### 4. Performance and a dependable public release

**Deliver:** measured responsiveness, maintained diagnostics, supported updates, and useful documentation.

- Measure startup, peak memory, library filtering/sorting, conflicts, apply time, and cancellation latency on declared hardware and datasets.
- Include small setups and representative 1,000+ mod libraries, with cold/warm runs and archive sizes. These are proposed benchmark datasets, not achieved results.
- Preserve correctness while improving indexing, caching, concurrency, and UI virtualization.
- Produce separately identifiable Windows/Linux packages, integrity checks, source references, release notes, and an exercised update/rollback procedure.
- Publish only game/platform combinations that passed the integration matrix.

## Validation boundaries

See [Contributing](developers/Contributing.md) for build/test commands. Clean tests need no live Nexus credentials, but dependency restoration and inherited build-time downloads need network access. Fixtures do not substitute for playing a modded game or installing a real collection.

The first increment does not certify every inherited component, complete Bethesda support, provide a public installer, implement durable crash recovery, or finish rebranding. Those remain explicit milestones.

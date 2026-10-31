# App Rework stability work

This record describes the Windows stabilization increment and its regression
coverage. The associated pull request and **Clean Environment Tests** run are
the source of execution results for a particular commit. A listed test is not
evidence that every installed game or collection has been validated.

The subsequent [profiles and compatibility increment](FEATURES_AND_COMPATIBILITY.md)
adds collection retry/cancellation handling, isolated FOMOD sessions, recursive
enabled-state checks, profile-copy recovery, deletion cleanup, and experimental
game-specific validation. Its guide records those mechanisms and limits; the
original stability protections below remain part of the regression gate.

## Crashes, hangs, and incorrect success states

| Area | Confirmed failure | Change and regression coverage |
| --- | --- | --- |
| Windows game discovery | Steam/GOG/Epic locator construction could fail because the Windows registry service was missing. The heartbeat command did not construct these lazy services. | Register the native registry without replacing caller-provided implementations. Test Windows and non-Windows construction; run actual locator enumeration in the published Windows smoke check. |
| Saved installations | UI lists assumed saved games were still discoverable. Matching by game and store alone could also select a different installed copy. | Skip unresolved rows without deleting saved loadouts. Prefer normalized installation paths and allow relocation only when unambiguous. Test multiple copies, relocation, missing games, and an activated list receiving later updates. |
| Empty collections | The sorter called `Partitioner.Create(0, 0)`, which throws. | Return an empty order for an empty input. Cover all public input forms. |
| Mod updates | Replacing linked items collapsed several copies into one and reset their collection, disabled state, and conflict order. Root items and nested read-only collections were handled incorrectly. | Install one replacement per original group in the same optional parent, preserve its local disabled marker and priority, and respect read-only ancestors. Test multiple copies across and within collections, inherited disabled state, parentless mods, and unchanged unrelated priorities. |
| Game catalog | A debug assertion assumed every game ID had a unique domain, although the bundled catalog contains duplicate domains. | Preserve ID lookups and deterministic first-entry domain lookup while logging duplicates. Exercise duplicate IDs/domains and invalid data. |
| Discarded collection revisions | Parsing optional revision metadata executed an unconditional `Debugger.Break`. | Preserve the typed server error while ignoring unmodeled metadata. Exercise the real GraphQL error path with absent, null, numeric, and unfamiliar string metadata. |
| GraphQL error handling | Typed error lookup cast the dictionary entry instead of its value and threw even for a correctly parsed server error. | Return the matching typed payload; cover matching, absent, unrelated, and malformed error entries. |
| Overlay queue | A worker could hold the queue lock while waiting for the UI thread, which could itself be waiting for that lock to close a dialog. Repeated completion could replace an already chosen result. | Serialize queue and completion operations on the UI thread. Test concurrent enqueue/close, queued cancellation, early close, and first-result-wins behavior. Restore the existing overlay cases to the normal offline gate. |
| Login cancellation | Closing the login overlay did not cancel the OAuth job; repeated or late cancel actions could affect an already closed operation. | Explicit Cancel stops the cancellable job once and closes the overlay; ordinary Close disposes the URI subscription. Test repeated and late cancellation. |
| Launch feedback | Some launch failures only reached the log; loadout lookup occurred outside the error handler, and labels could change off the UI thread. | Handle lookup and tool failures, show an actionable error dialog, and restore the button on the UI thread. Exercise a database-read failure through the real command and overlay path. |
| Apply feedback | Unexpected deployment failures escaped the command without a useful dialog. | Log the original failure and show its message; keep cancellation separate from failure and success. Exercise failed, cancelled, and completed jobs through an activated view model, checking progress recovery and UI-thread notifications. |
| Window persistence | Window-state commits were started without waiting before transaction disposal. The window-ID binding was never subscribed. | Finish commits before returning, invoking the asynchronous commit off the UI synchronization context to avoid a close-time deadlock. Test real persistence and window registration/removal. |
| Sort-order shutdown | Disposing reactive subscriptions cancelled work but did not await in-flight database operations. | Track owned operations, reject new work while stopping, cancel and drain before dependent database services are disposed. Test reactive and explicit operations with a gated real database read. |
| Native query shutdown | HyperDuck 0.28.2's observable adapter left live queries registered after unsubscription. Its polling worker could read an unregistered MnemonicDB connection or reconnect after DuckDB disposal began. | Own the native registration at every SQL-observable call site, and drain the polling worker after hosted services stop and before connection disposal. A pinned-version compatibility owner obtains the public updater through a temporary live-query handle. Gated native-query tests cover unsubscribe during execution, host stop, and provider disposal. |
| Pause/cancel | A synchronous or concurrent Cancel during Pause could be overwritten by a newly uncancelled token. | Publish the resume token before cancellation callbacks, serialize state transitions, and defer token-source disposal until callbacks finish. Test reentrant cancellation, concurrent cancellation, token reuse, and disposal from callbacks. |
| HTTP compatibility | Servers rejecting HEAD with 405/501 prevented valid GET downloads; stale ranges could stay stuck on 416, and empty successful downloads reported zero progress. | Restart without untrusted metadata after a method-specific HEAD rejection, clear invalid range offsets, and report completed empty downloads correctly. Preserve the existing body-length/validator checks and test interrupted fallback and cancellation. |
| Completed-download state | A parent completing before its HTTP child's final notification could retain a download with an active status after detaching its subscriptions. The independent test jobs exposed this ordering in CI. | Explicitly finalize retained successful downloads with completed status and progress. Match the real child-before-parent dependency in the normal fixture, and force the inverse order in a separate regression. Observe job removal and use keyed observable caches instead of polling unsynchronized lists. |
| Process output | The no-log branch checked the output pipe twice, leaving the error pipe unconfigured. | Configure stdout and stderr independently; retain real-process exit and cancellation coverage. |

## Cyberpunk REDmod deployment

Apply previously recorded the file baseline and completion time before REDmod
finished. A missing executable could return success, and a failed deployment
with no remaining file diff could be shown as current.

The new flow checks enabled winning manifests before changing game files,
records a per-loadout pending marker, synchronizes files, validates the copied
input set, runs REDmod with the Apply cancellation token, and records completion
only after successful deployment and ingestion. A fingerprint covers ordered
enabled REDmods and their winning file paths, hashes, and sizes. Failures and
changed inputs continue to require Apply even when the ordinary file diff is
empty. The pending marker and fingerprint are two additive database attributes;
existing collection records are not rewritten.

Launch requires the deployment to be current. The automatic `-modded` argument
is added only when enabled REDmods exist. Deactivating an old loadout reconciles
its files without retrying its failed REDmod process, so users can switch away
from a loadout that cannot currently deploy.

The lifecycle tests use a controlled process runner and real file/database
fixtures. They cover missing DLC, unsuccessful and cancelled processes,
successful retry, order/content changes, files edited during copying,
disabling the final mod during copying, and switching away from a failed
deployment. A fresh synchronizer reading the existing database connection
exercises persisted retry state; this is not a full process/database restart
test or a successful launch of the real game.

Generated cache files previously ingested as ordinary overrides have no reliable
ownership marker. This increment preserves those files, including possible user
edits, when the last REDmod is disabled. It removes disabled source manifests
through normal synchronization and omits the automatic `-modded` argument. It
does not claim to clean every generated cache. Explicit custom launch arguments
remain user overrides.

## Dependency and build maintenance

- Update the OpenTelemetry packages from 1.12.0 to 1.15.3 and pin the compatible
  Tmds.DBus.Protocol 0.21.3 fix for Avalonia's D-Bus dependency.
- Use SDK-provided GitHub Source Link instead of overriding it with the older
  Microsoft.SourceLink.GitHub 8.0.0 package. Require .NET SDK 9.0.317 or newer
  within the selected .NET 9 feature bands.
- Pin patched transitive packages used by inherited test helpers. Update the
  test-only native PNG decoder while retaining the existing Verify licenses.
  Image comparisons retain the existing PNG snapshots and Fuzz tolerance.
- Require System.Private.Uri 4.3.2 in the common SDK dependency graph. The
  Windows runtime-specific restore otherwise introduces the old 4.3.0 package
  through legacy System.Runtime dependencies. Its compatibility reference has
  no runtime assembly for this target; the app uses the .NET 9 runtime's URI
  implementation. This pin fixes the dependency graph's advisory findings.
- Audit transitive dependencies during restore. Audit warnings are not suppressed.
  Windows preview publication treats reported vulnerability warnings as errors,
  including those found only during its runtime-specific restore.
- Keep package dependency hardlinks for build disk usage, while leaving compiled
  project outputs separate from their intermediate files so post-processors
  cannot rewrite a shared intermediate assembly through an output hardlink.

Relevant published advisories:
[OpenTelemetry](https://github.com/advisories/GHSA-4625-4j76-fww9),
[D-Bus](https://github.com/advisories/GHSA-xrw6-gwf8-vvr9),
[Source Link build tasks](https://github.com/advisories/GHSA-23fw-v26w-5fgq),
[ASN.1](https://github.com/dotnet/runtime/security/advisories/GHSA-447r-wph3-92pm),
[HTTP](https://github.com/advisories/GHSA-7jgj-8wvc-jh57),
[JSON](https://github.com/advisories/GHSA-8g4q-xg66-9fp4),
[regular expressions](https://github.com/advisories/GHSA-cmhx-cq75-c4mj),
and [URI handling](https://github.com/dotnet/announcements/issues/113).

## Windows check and remaining evidence

The published native executable runs both `heartbeat` and
`diagnose-game-discovery` in CI. The latter constructs every registered locator
and enumerates it, accepts zero installed games, and emits `GameDiscovery: OK`
only on success. The script checks exit status, timeout, output, and the existing
application-data/NXM association isolation assertions for both invocations.

Outstanding work remains explicit:

- Real Windows game/store/version combinations need install → Apply → launch →
  update → remove → restore validation, including real REDmod and browser login.
- The earlier isolated Windows native test-host crash has no confirmed root
  cause. Awaited fixture cleanup and the sort-order lifetime fix address verified
  defects; a passing run does not establish that either caused that crash.
  Windows dumps, event logs, test sequence files, and diagnostics remain enabled.
- Database/process termination in the middle of a loadout switch needs failure
  injection and recovery testing. The REDmod pending marker does not provide
  general rollback of filesystem operations.
- Mod replacement commits the group changes together, but concurrent user edits
  or deletion of a parent while an asynchronous installer runs need separate
  conflict-detection coverage.
- Game-hash catalog gaps for newer large game archives still require catalog and
  real-installation evidence. The experimental integrations now retain their
  initial disk baseline; the 5 GB backup batch limit remains.
- Mirror fallback and repairs for previously corrupted collection targets remain
  separate work. The ordinary Creation Engine missing-master path is completed;
  independent plugin activation, LOOT metadata, extender/runtime checks, and
  broader plugin compatibility still need implementation and validation.
- Closing the entire UI while a modal is waiting needs dedicated dispatcher
  shutdown coverage. Normal overlay close/cancel tests do not certify that path.
- The job monitor still needs a host-shutdown boundary that cancels and awaits
  active jobs. The query and sort-order lifetime fixes do not establish that
  every background operation has finished before application shutdown.
- File-hash services privately own additional database connections. Their
  cleanup must join the shared query-engine shutdown boundary before those
  connections can be safely unregistered in every disposal mode. A local
  `Dispose` addition alone would not establish that ordering.

Upstream reports used in triage include
[#4185](https://github.com/Nexus-Mods/NexusMods.App/issues/4185),
[#4092](https://github.com/Nexus-Mods/NexusMods.App/issues/4092),
[#4141](https://github.com/Nexus-Mods/NexusMods.App/issues/4141),
[#4052](https://github.com/Nexus-Mods/NexusMods.App/issues/4052),
[#4119](https://github.com/Nexus-Mods/NexusMods.App/issues/4119), and
[#4037](https://github.com/Nexus-Mods/NexusMods.App/issues/4037).
Port attribution is recorded in [the roadmap](REWORK_ROADMAP.md).

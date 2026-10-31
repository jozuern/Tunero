# Open items

What is still open for the project. What needs real hardware or another Windows setup than the test VM is not ours to do: it is listed publicly in section 8 of [vm-test-plan.md](vm-test-plan.md).

## Release channels and the VM plan

- **winget:** the first version (pull request #450585, `jozuern.Tunero` 0.8.0) is still in review. Chocolatey offers the current version and Scoop's bucket follows every release.
- **New VM plan rows** (section 1 and 6 of [vm-test-plan.md](vm-test-plan.md)): a failed start after a self-update (needs a signed release newer than the installed one), and the code signing rows with a signed release.

## From the audit of 2026-10-30

- **VM checks** for findings the code alone cannot settle: whether `HibernateEnabled` can be missing while `powercfg /a` reports hibernation off (undo of `power.hibernateOff` reads a missing value as On); whether a feature update removes `SystemRestorePointCreationFrequency` while its backup stays (a needless restore point question within 24 hours); whether `RPSessionInterval` is set with System Protection on D: only (the restore point check is not specific to C:); whether a sign-out during a long `--apply` is now held like one in the window (fixed in code on 2026-10-31: `OnSessionEnding` also waits for a command line run); whether the install media page stalls when the picked ISO lies on a network share that drops.

## From the audit of 2026-10-31

All 186 findings of [audit-2026-10-31.md](audit-2026-10-31.md) and the follow-ups from the review of the merged fixes are done. What stays open:

- **Own ISO with two images of one edition:** the remembered choices name the edition, not the image index, so the page picks the first such image after a restart, and Resume then refuses the saved build (`Media_ResumeInvalid`) instead of building another image. Microsoft's ISOs give every image its own EditionID; only custom ISOs can hit this.
- **Website build:** `deepmerge` 4.3.1 (prototype poisoning, low) comes only through `@sveltejs/vite-plugin-svelte`, which merges its defaults with our own config files at build time; nothing reaches the site, and no fixed version exists. The Dependabot alert is dismissed as a tolerable risk (2026-10-31); take the fixed version once one is out.
- **Large files, as pure moves only, in a session that works in the file anyway:** `InstallMediaViewModel.cs` (choices, stages, resume), `AppsViewModel.cs` (programs, package managers), the three page view models in `PageViewModels.cs`, `MediaBuild.cs` (leftover cleanup, `MediaSteps`, the request records) and the fakes in `MediaBuildTests.cs`. Each move is its own commit with no other change.

## Code structure and tests

From a review of the architecture (October 2026), with nothing of it left open. The window and the command line pick the profile and the plan with one function each (`ProfileCatalog.Choose`, `Recommendations.ForProfile`), `FirstSignInTests` compares both paths, `ExpectedPlanTests` keeps the plans of four sample PCs in `tests/Optimizer.Core.Tests/Fixtures/plans`, and the window's scan and plan state is a plain class (`ScanState`) that `ScanStateTests` runs without the window.

Looked at and left as is, so they need no second look: a startup policy class around `Program.Main` (every step there already calls its own tested class; what remains is their order, which is the policy), more interfaces around registry, services and tasks (`ActionContext` and `ArchitectureTests` cover them), versioned catalog metadata (the catalog ships inside the exe; the saved formats, backups and exports, carry versions already), more policy gates for risky changes (`IsBatchSafe`, enforced again in `TweakEngine`, edition and build conditions, `OsGate`, signed updates), a lint for vague wording (`DocLint` covers the typography; a phrase list would mostly flag good text), splitting `AdvisorChecks.cs` (22 small, independent check classes), automating the lock file and license step of NuGet Dependabot pull requests (it is the review that a new version brings its license), injecting `ChangeGate` and `Loc` instead of the singletons that serialize some App tests (seconds of test time), and making Core members internal to catch unused public ones (large churn for a few findings).


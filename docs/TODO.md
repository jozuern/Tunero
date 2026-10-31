# Open items

What is still open for the project. What needs real hardware or another Windows setup than the test VM is not ours to do: it is listed publicly in section 8 of [vm-test-plan.md](vm-test-plan.md).

## Release channels and the VM plan

- **winget:** the first version (pull request #450585, `jozuern.Tunero` 0.8.0) is still in review. Chocolatey offers the current version and Scoop's bucket follows every release.
- **New VM plan rows** (section 1 and 6 of [vm-test-plan.md](vm-test-plan.md)): a failed start after a self-update (needs a signed release newer than the installed one), and the code signing rows with a signed release.

## From the audit of 2026-10-30

- **VM checks** for findings the code alone cannot settle: whether `HibernateEnabled` can be missing while `powercfg /a` reports hibernation off (undo of `power.hibernateOff` reads a missing value as On); whether a feature update removes `SystemRestorePointCreationFrequency` while its backup stays (a needless restore point question within 24 hours); whether `RPSessionInterval` is set with System Protection on D: only (the restore point check is not specific to C:); whether a sign-out during a long `--apply` is held like one in the window (`OnSessionEnding` looks only at `ChangeGate.Busy`); whether the install media page stalls when the picked ISO lies on a network share that drops.

## Code structure and tests

From a review of the architecture (October 2026). The window and the command line now pick the profile and the plan with one function each (`ProfileCatalog.Choose`, `Recommendations.ForProfile`), `FirstSignInTests` compares both paths, and `ExpectedPlanTests` keeps the plans of four sample PCs in `tests/Optimizer.Core.Tests/Fixtures/plans`. What is left:

- **The window's state without WPF.** `MainViewModel` (13 partial files, about 2,900 lines) is the only view model no test creates: it takes the concrete `AppServices`. Move the scan and plan state (`_findings`, `_tweakStates`, `_facts`, `_usage`, `_profiled`, `_profiledFindings`, `_plan`, `_drift`) and the logic around it into a plain class, and test it as `StorageViewModel` is tested: a scan requested while one runs (one more scan after it, never two at once), a laptop profile on a desktop falling back to Gaming for the session, ignored results and tweaks, reapplying reset changes. Not a split into several services: Core already holds the scan (`ScanService`), the plan (`Recommendations`), the changes (`ChangeSession`) and the backups (`BackupStore`).
- **Optional:** the developer switches take about 430 lines of `App.xaml.cs` (`SaveScreenshotsAsync`, `WritePerfReportAsync`, tab order). A move into a partial `App.Developer.cs`, only while working in that file anyway.

Looked at and left as is, so they need no second look: a startup policy class around `Program.Main` (every step there already calls its own tested class; what remains is their order, which is the policy), more interfaces around registry, services and tasks (`ActionContext` and `ArchitectureTests` cover them), versioned catalog metadata (the catalog ships inside the exe; the saved formats, backups and exports, carry versions already), more policy gates for risky changes (`IsBatchSafe`, enforced again in `TweakEngine`, edition and build conditions, `OsGate`, signed updates), and a lint for vague wording (`DocLint` covers the typography; a phrase list would mostly flag good text).


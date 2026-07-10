# HyperVault / HyperVBackupAgent Handoff

## Context

The workspace is `C:\Users\felipe\source\repos\limaum87\hypervault`.

The project implements `HyperVBackupAgent`, a .NET 8 Hyper-V backup agent. The full requirements are already captured in the repository at `TASK.md`; do not duplicate that spec. Repository-level agent rules are in `AGENTS.md`; notably the agent name is `trunks`, and task completion/error/blocked states require notification to the configured ntfy endpoint. Do not include secrets or credentials in responses or commits.

## Suggested Skills

- `github:yeet`: use if the next session needs to commit, push, or open a PR.
- `github:gh-fix-ci`: use if pushed changes trigger GitHub Actions failures.
- `github:github`: use if the next session needs repository/PR/issue orientation.

## Current Code State

The repository already contains the .NET solution and projects:

- `HyperVBackupAgent.sln`
- `HyperVBackupAgent.Core`
- `HyperVBackupAgent.Infrastructure`
- `HyperVBackupAgent.Cli`
- `HyperVBackupAgent.Api`
- `HyperVBackupAgent.Service`
- `HyperVBackupAgent.Tests`
- `tools/RctTool`

Important implementation files touched during this session:

- `HyperVBackupAgent.Infrastructure/RctServices.cs`
- `HyperVBackupAgent.Infrastructure/BackupEngine.cs`
- `HyperVBackupAgent.Infrastructure/RestoreMaterializer.cs`
- `tools/RctTool/Program.cs`
- `.gitignore`

The most important recent changes:

- Native RCT path was implemented using `virtdisk.dll` and Hyper-V WMI/CIM fallback.
- RCT enable/read P/Invoke alignment was fixed for x64 union layout.
- Incremental block writing now streams data and supports large ranges.
- RCT ranges are now treated as virtual disk offsets. Incremental backup mounts the VHDX read-only and reads from `\\.\PhysicalDriveN`, rather than reading offsets directly from the `.vhdx` container file.
- `artifacts/` is ignored in `.gitignore`.

Run validation before committing:

```powershell
dotnet build HyperVBackupAgent.sln
dotnet test HyperVBackupAgent.sln --no-build
```

## Remote Test Host

A Hyper-V host was used over SSH. Do not write the private key contents into any artifact.

Redacted connection facts:

- Host: `192.168.8.225`
- User: `administrador`
- SSH key path: `~\.ssh\accept_lab_win2022_192_168_8_56`
- Hyper-V host name observed: `ACC-HYPER-03`

The test VM is `acc-lab-ad`, VM id `4539d573-67c5-4da1-9750-340459d8b517`.

The VM disk under test:

```text
F:\VMs\ACC-LAB-AD\acc-lab-ad.vhdx
```

Backup destination used for the real native RCT chain:

```text
E:\HyperVBackupAgentBackupsNativeRct
```

Current successful chain:

```text
E:\HyperVBackupAgentBackupsNativeRct\ACC-HYPER-03\4539d573-67c5-4da1-9750-340459d8b517\chain-20260709-142224
```

## Remote Test Results

Initial simulated tests passed earlier, but the important final result is the native RCT test.

RCT was enabled offline for `acc-lab-ad` using `tools/RctTool`. The VM was then started again.

Successful full baseline:

- Restore point: `full-20260709-142224`
- Full VHDX size copied: `30,404,509,696` bytes
- RCT reference id recorded: `rctX:83440d86:8052:4a0d:8c78:7b169cdd380f:00000002`
- `verify-chain`: `valid`
- VM remained `Running`
- Checkpoints remaining: `0`

After the user copied an ISO of roughly 5 GB into the VM, a native RCT incremental succeeded:

- Restore point: `inc-0001`
- `SCSI_0_0.blocks`: `6,647,487,564` bytes
- `inc.json`: `126,762` bytes
- `verify-chain`: `valid`
- VM remained `Running`
- Checkpoints remaining: `0`

This confirms the agent is no longer using simulated 1 MB ranges for this test path. The larger block file is expected because RCT reports changed disk blocks, including filesystem metadata, alignment overhead, and other guest writes.

## Known Issues / Next Work

The current native RCT implementation is promising but still needs hardening:

- Add automated tests around large block format read/write if possible without Hyper-V.
- Add clearer cleanup for failed partial increment directories. A failed earlier `inc-0001` attempt left a partial `.blocks` file until the next successful retry overwrote it.
- Consider not including huge `changedRanges` arrays inline in `chain.json`; they can become very large. Better to keep detailed ranges in `inc.json` and summarize in `chain.json`.
- Validate restore materialization from the real `inc-0001` chain. The restore logic should apply the streamed `.blocks` format, but a full restore validation from the native RCT chain has not yet been run.
- Decide whether `tools/RctTool` remains a separate diagnostic utility or should be folded into the main agent.
- Production checkpoint failures occurred once when the guest VSS integration was not ready shortly after boot. Retrying after the guest stabilized worked later.

## Git / Publishing Notes

Earlier, `gh` was not installed on the machine, so PR creation via GitHub CLI was blocked. Plain `git` commit/push may still work if the user asks. Be careful with scope:

- `AGENTS.md` and `TASK.md` were originally untracked user-provided files.
- The implementation files are uncommitted unless the user has committed after this handoff.
- Do not commit published binaries under `artifacts/`; that path is now ignored.

Check status with:

```powershell
git status -sb
```

## Notification Requirement

Follow `AGENTS.md`. Use agent name `trunks` in notifications. Notify on success, error, or blocked/user-intervention states before finalizing or waiting for input.

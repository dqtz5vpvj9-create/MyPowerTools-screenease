# Archived integration snapshots

The 10 files listed below (3,442 physical source lines) were retired from the current tree because they are superseded integration snapshots, not current build inputs.

The active UI is current-integration/src/ScreenEase.Surface; the adapter and service are ScreenEase.MyPowerTools and ScreenEase.Service.

The superproject solution, project includes, tool build scripts, package manifests, and runtime source staging do not consume these paths. Existing Surface XAML, reflection-loaded factory types, shared runtime ownership, original-source trees, and linked tests remain unchanged. No runtime behavior change or build-time speedup is claimed from deleting files that were not compiled.

## Restore from Git history

Recovery commit in this tool repository: `20469c27705d76f0b36fbc0d4e45902998b284bc`. Keep the repository history containing this commit when publishing or mirroring it. A shallow clone or a source ZIP alone may not contain these historical blobs.

Run from this tool repository after saving any current changes:

```sh
git cat-file -e 20469c27705d76f0b36fbc0d4e45902998b284bc^{commit}
git restore --source=20469c27705d76f0b36fbc0d4e45902998b284bc -- current-integration/src/MyPowerTools.Shell.Avalonia
```

If the commit is absent, obtain it from an existing full clone or the repository remote containing this history before restoring. Restoring adds historical source files to the working tree; it does not wire them back into the current build.

The original source-map capture fields remain historical provenance. Its archivedIntegrationSnapshots entries describe the later retirement and point to this document, which stays inside current-integration so source-bundle materialization preserves it.

## Exact file recovery inventory

| Path | Git blob SHA-1 | Physical lines |
| --- | --- | ---: |
| `current-integration/src/MyPowerTools.Shell.Avalonia/Services/ScreenEaseToolService.cs` | `9dd9018908fa72892f7d4826ee29bc887524e437` | 943 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/ViewModels/ScreenEaseModeViewModel.cs` | `b917cc8d4b26d6c2336c113298ef6bcaa98fcc1b` | 79 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/ViewModels/ScreenEaseViewModel.Advanced.cs` | `944b4214d5e9192d7a30ec15d0b32c9103e3fe42` | 327 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/ViewModels/ScreenEaseViewModel.Presets.cs` | `5670cd405bfc859d90911b67db5f19faffd61f8a` | 47 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/ViewModels/ScreenEaseViewModel.Profiles.cs` | `144c0305e5d0740c120a8e45c73428c7ccb1b317` | 302 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/ViewModels/ScreenEaseViewModel.Properties.cs` | `1c34161a33bb54dec323258172b45a4175c671fc` | 314 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/ViewModels/ScreenEaseViewModel.Reminders.cs` | `cdd9769087781b8a7f648ee461003109b216abd8` | 240 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/ViewModels/ScreenEaseViewModels.cs` | `c752495ec7d823dc33a0b118a136d86fd55c999d` | 234 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/Views/ScreenEaseView.axaml` | `4f0f915c3c00f36b8af30942b1d6ed0b59cccd38` | 834 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/Views/ScreenEaseView.axaml.cs` | `1eb3c84a774a49a2c605407caf19d0e3c1a42a39` | 122 |

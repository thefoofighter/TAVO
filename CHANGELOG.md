# Changelog

All notable changes to TAVO are listed here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow [Semantic Versioning](https://semver.org/).

## [0.1.0] - 2026-11-24

First public release.

### Added

- Tray app that badges taskbar pins when two or more versions of one app are pinned.
- Labels: `major.minor`; a third (or fourth) number only for versions that share `major.minor`; a short prerelease tag (`a3`, `b2`, `rc1`) when the numbers are equal.
- One badge color per version inside an app.
- Pin detection from the taskbar pin list (`TaskBar` and `ImplicitAppShortcuts`), with a registry watch for pin changes.
- Window grouping fix: running windows of badged apps get the taskbar ID of their own version's pin. Pins of different versions that share one ID get their own IDs.
- **Refresh explorer**: resets the shell icon cache (`IconCache.db`) and restarts Explorer, so the taskbar shows the badges.
- A reminder after sign-in when badge changes still wait for a refresh.
- **Show version badges** switch that restores all original icons.
- **Start with Windows** (on by default).
- Command line: `--plan`, `--apply`, `--restore`, `--refresh-explorer`, `--version`.

[0.1.0]: https://github.com/thefoofighter/TAVO/releases/tag/v0.1.0

# Unified integration verification

Source baselines:
- PySide6: `e25f28fb2302b9d3495f7b687077d7457b39d55b`.
- Original WPF: `31fdab1735593899ec40bb6f78ab4c1d450a8186`.

Local Linux verification: original Python suite 21/21; expanded suite 29/29,
including real QProcess pipes, split readiness, process reuse, missing executable,
timeout, crash recovery, busy/login guard, normal legacy close, coordinated parent
close and the visible wallpaper-mode button. Qt uses offscreen rendering here.

Windows workflow runs both business suites, original WPF UI smoke extended with
the return button, busy guard and state preservation; builds both complete apps;
uses Windows UI Automation to click the actual WPF return button twice; checks
parent EOF, normal legacy close, frozen package startup and portable ZIP output.
See the workflow run associated with the PR for its actual result.

No real Pixiv login or bookmark mutation is included in these tests. Each engine
retains its existing business logic and separate persistent browser configuration.

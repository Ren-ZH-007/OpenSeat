# Native-session game launcher (development)

This change implements a real per-session launcher, not a multiseat engine.
It does not create sessions, route physical input, partition a GPU, integrate an
undocumented ASTER SDK, or claim compatibility with any anti-cheat system.
The remaining dependency is a separately installed, configured and licensed
native multiseat engine. Merely opening its control panel proves nothing about
device isolation. A Windows service and cross-session launch broker are not
implemented in this change; each user launches games in their own session.

## Implementation

- `GamingConfig`: three editable executable/argument profiles with Chinese
  names for LoL, Delta Force and CrossFire. These are labels, not certifications.
- Atomic XML persistence under `%LOCALAPPDATA%\OpenSeat\native-gaming.xml`.
  Separate Windows user accounts have separate settings. A damaged file is
  never overwritten by the launcher; back it up and repair/remove it manually.
- Session-local named mutex prevents duplicate launcher UIs in one session.
- WTS session inventory is diagnostic only. It does not map sessions to seats
  or prove three active gaming workplaces.
- EXEs start under the current user's token in the current session, without a
  command shell, password storage, DLL injection or explicit game elevation. An elevated parent still passes its token to children.
- Launch-process handles prevent a second start while that process remains
  alive. If an official launcher exits or hands off to another process, the
  game's state is **unknown**, not "stopped". Independently started games are
  not tracked; duplicate prevention is not global game detection.
- Window placement is explicit and applies only to the process started here.
  It does not control children, bypass full-screen rules or claim input isolation.
- External engine console is selected manually and opened using its normal
  launch/UAC behavior. There is no guessed command-line automation.
- Closing this launcher leaves games running. Logs rotate at 1 MiB and contain
  UTC time, seat label, session ID, game label and launched PID, not credentials
  or launch arguments. Errors may include filesystem paths.

## Build and validation

Run `build-native.cmd` on Windows x64 with the .NET Framework compiler. It builds
both `dist/OpenSeat.exe` and `dist/OpenSeat.Gaming.exe`; distribute them together.
`build-gaming.cmd` builds just the new launcher. Both development CI and the v0.4 release workflow package both executables.
Release checks run all 63 assertions before publishing and include test logs,
build provenance and SHA-256 checksums.

The new main-window button stops input tests and pauses/closes OpenSeat screen
monitoring before opening the launcher. The existing VMware path is retained.
The launcher can also be opened directly in each externally established user
session. Choose the local installed official game launcher EXE and configure
its arguments only if needed; no default game paths are guessed.

Automated tests cover config corruption, atomic writes, external XML entity
rejection, executable path restrictions, exact argument passing and missing
screen behavior. Hardware/game validation was deliberately skipped at the
user's request. No claim of three simultaneous playable seats is made.

# Diagnostics and reproduction

Milestone 1 adds optional Layer-2 logging without changing B0 quarantine/suppression.

## Enable diagnostics

1. Start `AcerInputFix.exe`.
2. Tray menu → check **Layer-2 diagnostics**.
3. Confirm the log shows:

```text
DIAG: Enabled (GetAsyncKeyState + HSHELL_APPCOMMAND + Consumer Raw Input). B0 suppression behavior unchanged.
```

Log lines are prefixed:

| Prefix | Meaning |
|--------|---------|
| `DIAG KEYSTATE:` | `GetAsyncKeyState` transition for media/volume/modifiers |
| `DIAG APPCOMMAND:` | Shell `HSHELL_APPCOMMAND` (cmd + device KEY/MOUSE/OEM) |
| `DIAG RAWINPUT:` | Consumer Control Raw Input (`0x0C` / `0x01`), with Col07 `0x33` decode when present |
| `DIAG MARK:` | Manual tray markers (broken/recovered/cycle). Always logged, even if Layer-2 streaming diagnostics are off. |

## Controlled comparison

Goal: find the first layer that differs between synthetic and physical recovery.

1. Enable Layer-2 diagnostics.
2. Verify taskbar previews work.
3. Tray → **DIAG: Mark taskbar previews recovered** (baseline marker), or skip if already known-good.
4. Press Volume Down or a brightness key.
5. Confirm text selection / PowerShell still work (B0 suppression).
6. Confirm taskbar previews are broken → **DIAG: Mark taskbar previews broken**.
7. Tray → **Test synthetic Previous Track**. Confirm previews stay broken.
8. Press **physical** Previous Track. Confirm previews recover → **DIAG: Mark taskbar previews recovered**.
9. Repeat steps 4–8 with synthetic vs physical Play/Pause.
10. Also idle for a while: the fault can appear without deliberate hotkeys.
11. Save / copy `AcerInputFix.log`.

## Col07 disable/enable cycle

Hypothesis: Layer-2 (taskbar previews) is cleared only when the Acer Col07 HID collection resets, which physical Previous does via a real `33 B6 00` report. Cycling the PnP device may force the same reset without a physical key.

1. Enable Layer-2 diagnostics.
2. Break taskbar previews (volume/brightness). Confirm B0 suppression still fixes apps.
3. Mark previews broken.
4. Tray → **DIAG: Cycle Col07 (disable/enable)** → OK.
5. If the log shows `Win32 5` / access denied, exit and restart `AcerInputFix.exe` **as Administrator**, then repeat from step 4.
6. After `Col07 cycle END ok=True`, check taskbar previews immediately.
7. Mark recovered or broken.
8. Confirm volume keys still work after re-enable.
9. Note whether `B0 suppress active` flipped from true → false across the cycle.

If the cycle repairs previews, a future workaround can automate an elevated Col07 reset after each REPAIR (with care). If it does not, the reset path is narrower than whole-collection restart (firmware latch / another collection / Explorer state).

## What to look for

Compare timestamps around:

- volume/brightness (or idle) → broken
- synthetic Previous / Play-Pause → still broken
- physical Previous / Play-Pause → recovered

Questions:

1. Does `GetAsyncKeyState` for B0/B1/B3 change only on physical recovery?
2. Does `DIAG APPCOMMAND` show a different cmd or device (KEY vs OEM) for physical vs synthetic?
3. Does `DIAG RAWINPUT` show a Col07 report (or another HID payload) only for physical recovery?
4. Is B0 suppression still active (`Bogus ... stream ended` not yet logged) while taskbar previews are broken?

### Findings from 2026-09-18 capture

Re-read with correct `winuser.h` APPCOMMAND IDs (an early logger build had media/volume names swapped):

| Event | Col07 HID | APPCOMMAND | KEYSTATE | B0 suppress cleared? | Taskbar |
|-------|-----------|------------|----------|----------------------|---------|
| Volume Down → fault | `EA` then phantom `B5` ScanNextTrack | VOLUME_DOWN (9) | AE down/up | no (repair starts) | breaks |
| Synthetic Previous | none | PREVIOUSTRACK (12) | missed (too fast for 40 ms poll) | no | still broken |
| Synthetic Play/Pause | none | PLAY_PAUSE (14) | missed | no | still broken |
| Physical Previous | `B6` then neutral | PREVIOUSTRACK (12) | B1 down/up | yes | recovered |
| Col07 disable/enable cycle | PnP reset | n/a | n/a | yes (on DISABLE) | recovered |

Conclusion so far:

- Shell APPCOMMAND alone does **not** repair taskbar previews (synthetic Previous emits the same PREVIOUSTRACK appcommand).
- Physical recovery and Col07 cycle both clear the upstream B0 stream; synthetic VKs do not.
- Col07 cycle is a viable user-mode reset path (needs elevation for SetupAPI).

### False recovery from tray interaction (2026-09-18 22:39)

Marking broken/recovered can *appear* to fix previews for a few seconds, but those marks still showed `B0 suppress active=True`. That means the HID/B0 fault was still open — only Explorer hover/UI state flickered.

By contrast, a successful Col07 cycle logs `B0 suppress active=False` and a lasting `RECOVERED`.

Rule of thumb:

- `RECOVERED` + `B0 suppress active=True` → ignore (transient shell/UI)
- `RECOVERED` + `B0 suppress active=False` after Col07 cycle or physical Previous → real repair

The tray menu now also logs `DIAG MARK: tray menu opened` so you can see whether the flicker lines up with opening the menu rather than the mark text itself.

# How TAVO works

This page describes what TAVO does, and the Windows behavior it depends on. It was written against Windows 11 x64, build 26300.

## 1. Find the pins

Windows keeps taskbar pins as shortcut (`.lnk`) files in two folders:

- `%APPDATA%\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar`
- `%APPDATA%\Microsoft\Internet Explorer\Quick Launch\User Pinned\ImplicitAppShortcuts\<hash>\`. Windows creates these when you pin a running window of an app that sets its own taskbar ID.

A shortcut in `ImplicitAppShortcuts` can stay after you unpin the app. So TAVO counts a shortcut as a pin only when the taskbar pin list references it. The pin list is the `Favorites` and `FavoritesResolve` values under `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband`. TAVO watches both folders and that registry key, and rescans every 10 minutes.

## 2. Group the versions

For each pin that starts an `.exe`, TAVO reads the version resource of the target:

- **Product key** = `ProductName|FileDescription`. A launcher and the main exe of one install (for example `blender-launcher.exe` and `blender.exe`) share it. Different Office apps do not.
- **Version** = the numeric file version. If that is `0.0.0.0`, TAVO parses the product version text or the install folder names (for example `UE_5.3`).
- **Prerelease tag** (`alpha3`, `beta2`, `rc1`, `preview`) from the product version text or the folder names (for example `Kanzi 4_2_0_1146_alpha3`).

A product gets badges when two or more different versions of it are pinned.

## 3. Make the badges

TAVO takes the pin's original icon (or the exe icon) and draws a colored band with the label across the bottom. It writes a multi-size `.ico` (16 to 256 px; 32-bit frames, with a PNG frame at 256 px) to `%LOCALAPPDATA%\TAVO\icons`. Then it points the shortcut's icon location at that file. The file name contains a hash of the content, so a changed badge gets a new path.

TAVO saves the original icon location in `%LOCALAPPDATA%\TAVO\state.json` before it changes a shortcut. It restores the original when the badge is no longer needed or when you turn badges off.

## 4. Show the change: Refresh explorer

The taskbar loads pin icons when Explorer starts. It does not reload them when a shortcut changes. Explorer also keeps its image list in `%LOCALAPPDATA%\IconCache.db`. It saves that file when it exits normally (sign-out, shutdown) and reloads it at start. So a restart alone can bring back old images.

**Refresh explorer** therefore:

1. Deletes `IconCache.db` (Explorer does not hold the file open while it runs).
2. Terminates the Explorer process that owns the taskbar. A terminated Explorer does not save the cache again.
3. Waits until Windows restarts Explorer, and starts it if Windows does not.

Explorer then builds a fresh image list and reads the badged icons.

## 5. Keep windows on the right pin

The taskbar puts a window under the pin that has the same **AppUserModelID** (taskbar ID). Some apps set an explicit ID:

- Blender sets one per version (`blender.5.0`, `blender.5.3`). A pin created from a shortcut has no ID, so a running Blender opens a second taskbar button next to its own pin.
- Unreal Editor (through its embedded Chromium) can use `Chromium.<hash of the user SID>`. That ID is the same for every engine version, so all versions collapse into one taskbar group.

TAVO fixes both cases:

- It reads the ID that Windows resolves for each badged pin, through the undocumented `IApplicationResolver` COM interface (Windows 8+ layout).
- When pins of different versions resolve to the same ID, TAVO writes a unique ID (`TAVO.<Product>.<version>`) into each of those shortcuts.
- An out-of-context `SetWinEventHook` (`EVENT_OBJECT_SHOW`) reports new top-level windows. When a window belongs to a badged app, TAVO sets its window-level ID to the ID of the matching pin, through `SHGetPropertyStoreForWindow`. This works across processes, and the taskbar regroups the window at once.

A window matches a pin when its exe path is the pin's target. It also matches when it is in the same folder and has the same product key (the launcher case).

## Lessons learned

- **Do not call `SHUpdateImage` with an image index from your own process.** Each process has its own system image list. A foreign index can make Explorer overwrite the wrong slot, and a normal exit then saves the damage into `IconCache.db`.
- **Do not ask Explorer to exit gracefully** (the hidden "Exit Explorer" message) before a refresh. A graceful exit saves the current, stale images to `IconCache.db`.
- **Test outside packaged apps.** A terminal that runs inside an MSIX package redirects new files under AppData and all HKCU writes into the package. Explorer does not see them.
- `SHChangeNotify`, `SHUpdateImage` and `ie4uinit -show` do not make the running taskbar reload a pin icon.

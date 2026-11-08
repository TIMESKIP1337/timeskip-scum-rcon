# ScumRcon user guide

*Thai version: [USAGE.th.md](USAGE.th.md).*

This guide takes a server owner from nothing to typing commands. It only describes what has been tested; things
that are not finished are listed in [Known limits](#12-known-limits).

1. [How it works](#1-how-it-works)
2. [What you need](#2-what-you-need)
3. [Install, step by step](#3-install-step-by-step)
4. [Start it and type commands](#4-start-it-and-type-commands)
5. [Game commands](#5-game-commands)
6. [`say` and `saypm`: chat messages](#6-say-and-saypm-chat-messages)
7. [Users, roles and who may run what](#7-users-roles-and-who-may-run-what)
8. [Configuration reference](#8-configuration-reference)
9. [Logs and the audit trail](#9-logs-and-the-audit-trail)
10. [Troubleshooting](#10-troubleshooting)
11. [Safety checklist](#11-safety-checklist)
12. [Known limits](#12-known-limits)
13. [Updating and removing](#13-updating-and-removing)

---

## 1. How it works

```
 you (rcon> prompt, or an RCON client)
        │  command text
        ▼
 ScumRcon.Server  ── checks login, role, length, rate ──▶ audit log
        │  writes a small file
        ▼
 <server>\...\ue4ss\Mods\ScumRcon\bridge\rcon_commands\
        │  the game mod picks it up every 0.5 s
        ▼
 ScumRcon mod (inside the SCUM server process, via UE4SS)
        │  runs the command as an admin command, or sends a chat line
        ▼
 the game
```

Two programs are involved, and both run on the **same machine** as the game server:

| Part | What it is | Where |
|---|---|---|
| **ScumRcon.Server** | The RCON server (a .NET program). Takes your commands, checks them, hands them to the game. | any folder, e.g. `D:\ScumRcon` |
| **ScumRcon mod** | A small Lua mod that runs inside the game through [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS). | `...\SCUM\Binaries\Win64\ue4ss\Mods\ScumRcon` |

You can restart the game server without restarting ScumRcon.Server: they only talk through files.

## 2. What you need

* A Windows machine running a **SCUM dedicated server**.
* The **.NET 8 runtime** (to run) or the **.NET 8 SDK** (to build from source).
  <https://dotnet.microsoft.com/download/dotnet/8.0>
* **UE4SS** in the server's `Win64` folder. It is *not* included in this repository; the installer script in
  step 3 downloads the official release and checks it.
* At least **one player online** when you run game commands (see [Known limits](#12-known-limits)).

## 3. Install, step by step

All paths below use `D:\SCUM-Server` for your server and `D:\ScumRcon` for the RCON server. Use your own.

### 3.1 Get the files

Download or clone this repository. There are no prebuilt releases yet, so build the server once:

```powershell
cd path\to\this\repository
dotnet publish src\ScumRcon.Server -c Release -o D:\ScumRcon
```

Then copy the helper files next to it:

```powershell
Copy-Item clients\rcon-client.ps1        D:\ScumRcon\
Copy-Item extras\windows\Start-ScumRcon.bat D:\ScumRcon\
```

### 3.2 Install UE4SS

Close the game server first, then run (PowerShell):

```powershell
.\scripts\Install-Ue4ss.ps1 -ServerBinaries 'D:\SCUM-Server\SCUM\Binaries\Win64'
```

What it does: downloads one pinned official UE4SS release, **refuses to continue unless the SHA-256 matches**, puts
`dwmapi.dll` next to `SCUMServer.exe` and the `ue4ss` folder next to it, and installs the settings file from
`extras\ue4ss\UE4SS-settings.scum-server.ini`.

Why that settings file: with UE4SS's own default settings (eight extra engine hooks switched on) a test SCUM server
**crashed the moment the first player joined**. With the settings file from this repository the same UE4SS build
did not. If you already have UE4SS, the script refuses to touch it (add `-Force` to replace the program files; your
mods and your settings file are never overwritten, and replaced files are backed up). More details:
[INSTALL.md](INSTALL.md#ue4ss-required-not-included-in-this-repository).

### 3.3 Install the game mod

Copy the folder `mods\ScumRcon` to:

```
D:\SCUM-Server\SCUM\Binaries\Win64\ue4ss\Mods\ScumRcon
```

The empty file `enabled.txt` inside it switches the mod on (alternatively add the line `ScumRcon : 1` to
`ue4ss\Mods\mods.txt`, above the line `Keybinds : 1`). The mod needs no other mod.

### 3.4 Start the game server once

Start SCUM as usual. Open `D:\SCUM-Server\SCUM\Binaries\Win64\ue4ss\UE4SS.log` and look for:

```
[ScumRcon] RCON bridge active, polling for commands every 500 ms
```

The mod has now created the two folders the RCON server needs:
`...\Mods\ScumRcon\bridge\rcon_commands` and `...\rcon_responses`.

### 3.5 First start of the RCON server

```powershell
cd D:\ScumRcon
.\ScumRcon.Server.exe
```

The first time, it creates `config\rcon.json`, **prints the admin password once, and exits**:

```
First run: created D:\ScumRcon\config\rcon.json
  Login : admin:<password>   (RCON password field = admin:XXXXXXXXXXXXXXXXXXXXXXXX)
  This password is shown ONCE and is stored only as a hash. Save it now.
```

Save that password in a password manager. It is stored only as a hash, so it cannot be shown again (to get a new
one, see [Users and roles](#7-users-roles-and-who-may-run-what)).

### 3.6 Edit `config\rcon.json`

Open it in a text editor. Two things must be changed for the file bridge. (`config\rcon.example.json` shows every
setting.)

```json
"bridge": {
  "mode": "File",
  "commandsDirectory":  "D:\\SCUM-Server\\SCUM\\Binaries\\Win64\\ue4ss\\Mods\\ScumRcon\\bridge\\rcon_commands",
  "responsesDirectory": "D:\\SCUM-Server\\SCUM\\Binaries\\Win64\\ue4ss\\Mods\\ScumRcon\\bridge\\rcon_responses",
  "timeoutSeconds": 15
}
```

* `mode` must be `"File"` (the default is `"Tcp"`, which needs a different game-side plugin that this repository
  does not ship).
* In JSON every backslash is doubled (`\\`).
* The two folders must be **absolute** paths and must already exist (step 3.4 created them).
* Leave `listenAddress` at `127.0.0.1` and `port` at `28015` unless you need something else (see
  [Remote access](#remote-access)).

### 3.7 Start it for real

```powershell
.\ScumRcon.Server.exe --console
```

or double-click `Start-ScumRcon.bat`. If the configuration has a mistake, the server prints every problem and does
not start.

## 4. Start it and type commands

### One window (recommended)

`Start-ScumRcon.bat` (or `ScumRcon.Server.exe --console`) shows the logo, the status, and a prompt:

```
listening  127.0.0.1:28015
bridge     files   (a folder the game mod watches)
audit log  D:\ScumRcon\logs\audit.jsonl

rcon> ListPlayers
rcon> say global Server restarts in 5 minutes
rcon> saypm god 4 hello
rcon> exit
```

* Type a command and press Enter. A leading `#` is optional (`#ListPlayers` = `ListPlayers`).
* `exit` or `quit` (or Ctrl+C) stops the RCON server. The game keeps running.
* The prompt needs no password: it is for whoever sits at the server machine. Every command is still written to
  the audit log as user `console`.
* Colours need a modern terminal (Windows Terminal, Windows 10/11 console). Set the environment variable
  `NO_COLOR` to turn them off. When output is redirected to a file, no colour codes are written.
* **Only one copy of ScumRcon.Server can run per configuration.** A second one stops with a clear message.

### From another window, script or machine

While the server runs, any RCON client can connect. The login is the Source-RCON "password" field filled with
**`user:password`**, for example `admin:YourPassword`.

The included PowerShell client:

```powershell
.\rcon-client.ps1 -Command ListPlayers          # one command, then exit (asks for the password, hidden)
.\rcon-client.ps1                               # interactive prompt
.\rcon-client.ps1 -HostName 10.0.0.5 -Port 28015 -User moderator
```

The password is typed at a hidden prompt and never stored. Other Source-RCON clients should work too (the protocol is
covered by automated tests); the included script is the only client tested by hand.

### Remote access

RCON is **not encrypted**: passwords and commands cross the network as plain text. If admins connect from another
machine, put the traffic in a VPN or an SSH tunnel, set `listenAddress` to the VPN address, list only the
addresses that may connect in `ipAllowlist` (CIDR such as `10.8.0.0/24` works; an empty list lets **nobody** in),
and open the RCON port in the firewall only to those addresses. **Never open port 28016.**

## 5. Game commands

### `players`: who is online (answers right here)

```
rcon> players
2 players online:
1. Alice  76561198000000001
2. Bob  76561198000000002
```

This one is answered by the ScumRcon mod itself, so **you really see the list** at your prompt (the game's own
`ListPlayers` only shows its text inside the game). It lists the in-game name and the SteamID of everybody online,
sorted by name, and says `No players online.` when the server is empty. It works even with nobody online and
needs no admin player. Use it to find the SteamID for `saypm`. Extra words are ignored, and it is allowed for the
`Viewer` role by default.

**`ListPlayers` does the same.** It is answered by the mod and is **never sent to the game**, on purpose: the game's
own `ListPlayers` prints names, SteamIDs, balances and **positions of everybody** into the in-game chat of one
online player. Scripts and bots that send `ListPlayers` therefore get the mod's list (names and SteamIDs only;
the format is the mod's, not the game's).

### Everything else: the game's admin commands

Anything that is not `say`, `saypm` or `players` is passed to the game as an **admin command**, exactly as if an
admin had typed it in the in-game chat. So the syntax is the game's own. Type `#Help` in the in-game chat to see
the list; SCUM's admin command documentation has the rest.

Commands that were run through ScumRcon on a test server and showed up in the game log:

```
ListPlayers
SpawnItem Weapon_AKM 1 76561198000000042 Location
Teleport X=-183753.672 Y=-213822.312 Z=32899.160 76561198000000042
announce Hello everybody
SetImmortality true
settime 23:00
setweather 1
SetAttributesPlayer GOD 8 5 5 5
```

### Where the game's text goes

When the game runs an admin command it prints the command's text (a player list, flags, vehicles, squads, a
confirmation...) into the **in-game chat of the player whose connection ran the command**, not back to you. A list
command can therefore **reveal other players' data** (positions, SteamIDs, owners) on that player's screen. This was
seen on a test server: running the game's `listplayers` over RCON showed the full player data on the screen of the
player whose connection ran it. So:

* **Only an admin runs your command.** The mod runs a game command through the connection of an **online player who
  is in the game's own admin list** (`SCUM\Saved\Config\WindowsServer\AdminUsers.ini`). If no such player is online
  the command is **refused** (`No admin player is online...`) and nothing is sent to anybody. With several admins
  online the first one in the player list runs it. The admin's chat is where the text appears, and the mod log says
  who ran it (`[RCON-Bridge] Running as admin <name> (<steamid>)`).
* `ListPlayers` is handled by the mod and never sent to the game (see [`players`](#players-who-is-online-answers-right-here));
* the game's list commands (`ListFlags`, `ListSpawnedVehicles`, `DumpAllSquadsInfoList`, ...) are **not** allowed
  for the `Viewer` role by default; only give them to someone you trust, and know the text will show up in an
  admin's chat;
* `say`, `saypm` and `players` need no admin online.

The mod reads `AdminUsers.ini` itself (a line is a SteamID, optionally followed by `[Godmode]`). If your file is
somewhere else, or the mod cannot read it, the answer is `Cannot tell who the admins are...`: see
[game-side settings](#game-side-settings-in-mainlua).

Things to know:

* The command runs in the name of an online admin (the game logs it under that player), so **at least one admin
  must be in the game**. Otherwise the answer is `No admin player is online...`.
* The **text the game answers is not returned** to your prompt yet (see [Known limits](#12-known-limits)). A
  successful command prints `Executed: <command> (the game sent no reply text)`. The command *did* run; check the
  game, or the server log (`SCUM\Saved\Logs\SCUM.log`, a line `Command: '...'`). For the player list use
  `players` above.
* A command cannot contain a double quote `"` or a backslash `\` (the file bridge cannot carry them safely). The
  server refuses it with a message instead of sending something damaged.
* Maximum command length is `maxCommandLength` (512 by default). Control characters are refused.

## 6. `say` and `saypm`: chat messages

These two are handled by the ScumRcon mod itself, not by the game's admin system. They show a line of text in the
players' chat.

```
say    [channel] <message>                   everyone online
saypm  <name or steamid> [channel] <message> one player
```

Examples:

```
say global Server restarts in 5 minutes
say 4 admins only please
saypm god 4 hello
saypm GOD admin Welcome back
saypm 76561198000000042 squad You are needed at the base
saypm god just a private note              (no channel = "server")
```

### Channels

The chat **colour** comes from the channel, not from the text.

| Number | Name(s) | Notes |
|---|---|---|
| 0 | `default` | |
| 1 | `local` | |
| 2 | `global` | cyan in our test |
| 3 | `squad` | green in our test |
| 4 | `admin` | yellow in our test |
| 5 | `commands` / `commandsonly` | |
| 6 | `server` / `servermessage` | **used when you give no channel** |
| 7 | `error` | |

(Only 2, 3 and 4 have been checked on screen.) A single digit 0-7 or one of the names works; `say 12 hi` is
refused.

### Who gets it

* `say` goes to **every connected player**; the answer tells you how many (`to 3 player(s)`).
* `saypm` goes to **one** player. The first word after `saypm` is the target:
  * **10 or more digits** = a SteamID;
  * **anything else** = the player's **in-game name** (the whole name, English letter case ignored: `god` finds `GOD`).
* A **name must be one word** (no spaces), because quotation marks cannot be carried. For a name with spaces use
  the SteamID.
* If **two online players have the same name**, nothing is sent and you get their SteamIDs to choose from.
* If nobody matches: `No online player named X` / `No online player with SteamID X`.

### The name in front

Lines start with a name so players see who speaks: `[TIMESKIPCON] Server restarts in 5 minutes`. The game does not
print a sender name for a line with no real player behind it, so the mod writes it into the text. Change it at the
top of `mods\ScumRcon\Scripts\main.lua`:

```lua
if RCON_SAY_NAME == nil then RCON_SAY_NAME = "TIMESKIPCON" end
```

Use `RCON_SAY_NAME = ""` for no name. Up to 32 bytes (about 10 Thai letters). Restart the game server to apply.

### Limits

400 characters of message, no `"` or `\`. Other languages (for example Thai) work.

## 7. Users, roles and who may run what

Every login has a **role**:

| Role | Meaning |
|---|---|
| `Viewer` | lowest |
| `Moderator` | |
| `Admin` | highest; includes everything below |

Each command word has a **minimum role** in `commandRoles` (the word is the first word, any letter case, `#`
ignored). **A command that is not listed needs `Admin`.** Unknown means restricted, never open.

The defaults let a `Viewer` run only these:

```json
"commandRoles": {
  "help": "Viewer",
  "ListPlayers": "Viewer",
  "players": "Viewer"
}
```

(`ListPlayers` is answered by the mod, see [`players`](#players-who-is-online-answers-right-here). The game's own
list commands such as `ListFlags` or `ListSpawnedVehicles` are **not** in the defaults: see
[where the game's text goes](#where-the-games-text-goes).)

To let moderators send chat messages and announce:

```json
"say": "Moderator",
"saypm": "Moderator",
"announce": "Moderator"
```

The `rcon>` prompt in the server window always acts as `Admin`.

### Add a user

1. Create a password hash: `.\ScumRcon.Server.exe --hash-password` (it asks for the password; **it is visible
   while you type**), copy the line it prints (it starts with `pbkdf2-sha256$`).
2. Add the user to `config\rcon.json`:

```json
"users": [
  { "name": "admin",     "passwordHash": "pbkdf2-sha256$600000$...", "role": "Admin" },
  { "name": "moderator", "passwordHash": "pbkdf2-sha256$600000$...", "role": "Moderator" }
]
```

3. Restart ScumRcon.Server. Names: 1-32 letters, digits, `_`, `.` or `-`. Names must be unique.

**Lost the admin password?** Run `--hash-password` with a new one and replace that user's `passwordHash`. (Or
delete `config\rcon.json` to start from scratch: a new random admin password is printed.)

## 8. Configuration reference

`config\rcon.json` (comments `//` and trailing commas are allowed). Defaults in brackets.

| Setting | Meaning |
|---|---|
| `listenAddress` [`127.0.0.1`] | Address the RCON server listens on. Loopback = this machine only. |
| `port` [`28015`] | RCON port. |
| `ipAllowlist` [`127.0.0.1`, `::1`] | Who may connect: addresses or CIDR ranges. **Empty = nobody.** `0.0.0.0/0` is refused. |
| `maxConnections` [10] | Simultaneous connections. |
| `authTimeoutSeconds` [15] | Time allowed to log in. |
| `idleTimeoutSeconds` [300] | A silent connection is closed after this. |
| `maxFailedAuth` [5], `failureWindowMinutes` [10], `blockMinutes` [15] | 5 wrong logins in 10 minutes block that address for 15 minutes. |
| `maxCommandsPerSecond` [5] | Rate limit per connection. |
| `maxCommandLength` [512] | 1 to 2048. |
| `users` | See [Users](#7-users-roles-and-who-may-run-what). |
| `commandRoles` | Minimum role per command word. |
| `bridge.mode` | `"File"` (use this) or `"Tcp"`. |
| `bridge.commandsDirectory`, `bridge.responsesDirectory` | Absolute paths of the two bridge folders (File mode). |
| `bridge.timeoutSeconds` [15] | How long to wait for the game. |
| `bridge.host`, `bridge.port`, `bridge.secret` | Only for the TCP bridge ([BRIDGE-PROTOCOL.md](BRIDGE-PROTOCOL.md)). |
| `auditLogPath` [`logs/audit.jsonl`] | Where the audit log is written. |

The server **refuses to start** (and says why) if: the address is not an IP, a number is out of range, the allowlist
allows the whole internet, a non-loopback address has an empty allowlist, a user name is invalid or duplicated, a
password hash is invalid, a role is unknown, or the bridge folders do not exist.

Use another configuration file with `ScumRcon.Server.exe --config D:\other\rcon.json`.

### Game-side settings in `main.lua`

The mod has a few settings at the top of `mods\ScumRcon\Scripts\main.lua` (restart the game server after a change):

| Setting | Meaning |
|---|---|
| `RCON_SAY_NAME = "TIMESKIPCON"` | The name in front of `say` and `saypm` lines. `""` = no name. Up to 32 bytes. |
| `RCON_EXECUTOR = "any"` | **Not recommended.** Commands then run through the first player found, not an admin, so their output appears in any player's chat. Without this line only admins are used. |
| `RCON_ADMIN_USERS_FILE = "..\\..\\Saved\\Config\\WindowsServer\\AdminUsers.ini"` | Where the game's admin list is, relative to the `Win64` folder (or an absolute path). Only needed if yours is elsewhere. |
| `RCON_BRIDGE_DIR = "ue4ss\\Mods\\ScumRcon\\bridge"` | The exchange folder, relative to `Win64`. Must match `bridge.commandsDirectory` and `responsesDirectory`. |

Lines that are commented out (`--` at the start) are the defaults. Besides these, an empty file
`ue4ss\Mods\ScumRcon\debug.txt` switches on debug logging.

## 9. Logs and the audit trail

`logs\audit.jsonl` has one JSON object per line. Passwords are never written.

| `event` | Meaning |
|---|---|
| `auth_ok` / `auth_fail` | Login succeeded / failed (`blockedNow` shows when this failure caused a block). |
| `blocked` | A blocked address tried to connect. |
| `rejected_ip` | The address is not in `ipAllowlist`. |
| `cmd` | A command ran (`user`, `role`, `command`, `success`). The prompt appears as user `console`, `ip` `local`. |
| `cmd_denied` | The command was refused (`reason`: role too low, too long, control character). |
| `rate_limited`, `too_many_connections`, `auth_timeout`, `idle_timeout`, `protocol_error`, `session_error` | Connection problems. |

Game side: `ue4ss\UE4SS.log` contains the mod's lines (`[ScumRcon]`, `[RCON-Bridge] Executing: ...`).
Turn on **debug mode** to log more: create an empty file `ue4ss\Mods\ScumRcon\debug.txt` and restart the game
server (delete it to turn off). It logs which game functions run around a command; it is meant for developers.

## 10. Troubleshooting

| What you see | Cause and fix |
|---|---|
| `No connection could be made because the target machine actively refused it` | ScumRcon.Server is not running (or the port differs). Start it; check `port`. |
| `Login failed` | Wrong user or password, the address is not in `ipAllowlist`, or it is blocked for 15 minutes after 5 failures. The password field is `user:password`. |
| `Cannot open the audit log ... Is another ScumRcon.Server already running` | A second copy is running. Close it (Task Manager, or `exit` in its window). |
| `Cannot listen on ...: the port is already in use` | Another program uses the port. Close it or change `port`. |
| `The bridge folder does not exist` | Start the game once with the mod loaded (it creates the folders), or create them; fix the paths in `bridge`. |
| `Error: The game did not pick up the command. Is the game server running with the ScumRcon mod loaded?` | The game is off, or the mod did not load. Check `UE4SS.log` for `[ScumRcon] RCON bridge active`. |
| `Error: No admin player is online. The game runs admin commands in the name of an online player...` | Nobody from `AdminUsers.ini` is in the game. Join as an admin and try again. (`say`, `saypm`, `players` work without one.) |
| `Error: Cannot tell who the admins are: cannot read ...AdminUsers.ini` | The mod cannot find the game's admin list. Set `RCON_ADMIN_USERS_FILE` in `mods\ScumRcon\Scripts\main.lua` (see [game-side settings](#game-side-settings-in-mainlua)). |
| `Error: Failed to execute` | The game refused the call. Try again; if it persists, look at `UE4SS.log`. |
| `Executed: ... (the game sent no reply text)` | Normal today: the command ran; the game's answer text is not returned (see Known limits). |
| `No online player named X` | The name must match the whole in-game name; names with spaces need the SteamID. |
| `More than one online player named X` | Use the SteamID from the answer. |
| `Denied: 'xyz' needs the Admin role.` | Add the command to `commandRoles` for that role, or log in as Admin. |
| `The configuration has problems...` | Fix the lines it lists. |
| `UE4SS.log` has no `[ScumRcon]` line | The mod folder is in the wrong place or `enabled.txt` is missing. The path must be `...\ue4ss\Mods\ScumRcon\Scripts\main.lua`. |
| The game server **crashes when a player joins** | Check that `ue4ss\UE4SS-settings.ini` is the one from `extras\ue4ss` (the `Hook...` lines = 0). With UE4SS default settings this happened in our test. |
| The UE4SS debug window appears | In `UE4SS-settings.ini`, section `[Debug]`, set `ConsoleEnabled`, `GuiConsoleEnabled`, `GuiConsoleVisible` to `0`. (The shipped file has them at `1`, the setting that was tested; turning them off was not part of the crash test.) |
## 11. Safety checklist

1. Keep `listenAddress` on `127.0.0.1` unless admins really connect from elsewhere; then use a VPN or SSH tunnel.
2. Keep `ipAllowlist` as short as possible. Never open port 28016.
3. Give people the lowest role that does their job; list only the commands they need. Remember that the output of
   a game command appears in a player's in-game chat ([details](#where-the-games-text-goes)).
4. Anyone who can write into the bridge's `rcon_commands` folder can run admin commands: give that folder to the
   account running ScumRcon.Server and to admins only.
5. Keep `config\rcon.json` and `logs\` out of Git and out of backups you share.
6. Read `logs\audit.jsonl` now and then for `auth_fail`, `blocked` and `cmd_denied`.
7. Download UE4SS only from its official release (the script does this and checks the hash).

See [SECURITY.md](../SECURITY.md) for what the project promises and how to report a vulnerability.

## 12. Known limits

* **The game's reply text is not returned.** The mod listens for chat lines the game sends back, but in a real test
  of `ListPlayers` it captured nothing. Commands run, but you only see `Executed: ...`. `say`, `saypm` and
  `players` do answer with real text, because the mod produces that text itself. The likely reason for the
  missing game text: the game seems to deliver a command's output through an internal function, not through chat,
  and reading it would need native (C++) code tied to one game build. This is open work.
* Game commands need **an admin player in the game** (someone listed in the game's `AdminUsers.ini`): the game runs
  them in that player's name and shows their text in that player's chat. Without an admin online they are refused;
  `say`, `saypm` and `players` still work. The admin-only rule has been tested with a fake game; **it has not yet
  been confirmed on a real server with several players.**
* No `"` or `\` in commands; a `saypm` name must be one word.
* UE4SS can break when SCUM updates; keep the version you tested. The pinned release was tested with one server, one
  player, a few minutes.
* The TCP bridge mode is documented ([BRIDGE-PROTOCOL.md](BRIDGE-PROTOCOL.md)) but no game-side plugin for it is
  included; use the File bridge.
* Early alpha: not yet checked on a busy server with many players.

## 13. Updating and removing

**Update ScumRcon.Server:** stop it (`exit`), build again with `dotnet publish ... -o D:\ScumRcon`, start it.
`config\` and `logs\` are not touched by a build.

**Update the mod:** copy the new `mods\ScumRcon` over the old one (keep your edits to `main.lua`, such as
`RCON_SAY_NAME`) and restart the game server.

**Remove:** stop ScumRcon.Server, delete its folder; delete `ue4ss\Mods\ScumRcon`. To remove UE4SS, delete
`dwmapi.dll` and the `ue4ss` folder from `Win64` (the installer script keeps backups of anything it replaced in
`ue4ss-backup-<time>`).

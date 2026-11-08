# ScumRcon

An open-source RCON server for **SCUM dedicated servers**: secure by default, role based, and easy to audit.

> **Status: early alpha.** The RCON server and the game-side UE4SS mod are covered by 301 automated tests.
> The bridge code has been run on a real SCUM dedicated server (game commands, `say`, `saypm`, `players`), and
> the standalone `mods/ScumRcon` mod loads and runs commands on a test server with the official UE4SS release and
> the settings file from this repository (one player, a few minutes). **Not yet confirmed with several players.**
> **The game's reply text is not returned yet**: see [Known limits](#known-limits).
> This project is not affiliated with the makers of SCUM.

## Why

Server owners should be able to read every line of the tool that holds admin access to their server.
ScumRcon has no obfuscation, no hidden updates and no calls to third-party servers. It only listens for your
admins and talks to the game on the same machine.

## How it fits together

```
 admin tools            RCON server (this repo)                 game side
 (bot, console, web) ─▶ port 28015 ─▶ login → role → policy ─▶ bridge ─▶ UE4SS Lua mod in the game process
                         allowlist, lockout, rate limit,                  runs the command in the game and
                         audit log                                        (reply text: see Known limits)
```

* Speaks the standard **Source RCON** wire protocol, so existing RCON clients work.
* The link to the game is either a loopback-only, secret-protected TCP bridge
  ([docs/BRIDGE-PROTOCOL.md](docs/BRIDGE-PROTOCOL.md)) or a folder of command files that a game-side script watches
  ([docs/FILE-BRIDGE.md](docs/FILE-BRIDGE.md)). Choose with `bridge.mode`.
* [`mods/ScumRcon`](mods/ScumRcon) is the game-side **UE4SS Lua mod** for the file bridge. It needs no other mod:
  copy the folder to `ue4ss\Mods\ScumRcon`. It runs commands as admin commands and adds three commands of its own:
  `say [channel] <message>` (everyone), `saypm <name or steamid> [channel] <message>` (one player, e.g.
  `saypm god 4 hello`), both shown in the chat colour of the chosen channel, and `players` (who is online, with
  SteamIDs; the answer appears at your prompt). `ListPlayers` is answered the same way and is never sent to the
  game, because the game prints that list, with everybody's position and balances, into one player's chat.

## Security defaults

| Area | Default |
|---|---|
| Listen address | `127.0.0.1` (not reachable from outside until you change it) |
| First run | random admin password (shown once, stored only as a PBKDF2-SHA256 hash) and random 256-bit bridge secret |
| Who can connect | explicit IP allowlist; an empty list allows **nobody** |
| Logins | `user:password`, lockout after repeated failures, same answer for unknown user and wrong password |
| Commands | role per command word; **commands you did not list need Admin**; control characters and over-long commands are refused |
| Startup | refuses to start with placeholder secrets, plain-text passwords, a non-loopback bridge, or an open-to-everyone allowlist |
| Logs | JSON-lines audit log of logins, blocks and commands. Passwords are never written |

## Build and run

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet test                                   # run the test suite (301 tests)
dotnet run --project src/ScumRcon.Server      # first start creates config/rcon.json
```

The first start prints the admin password **once** and exits so you can review the file.
Edit `config/rcon.json` (users, `ipAllowlist`, `commandRoles`, `bridge`), then start again.
Use `--hash-password` to create a hash for another user. [`config/rcon.example.json`](config/rcon.example.json)
shows every setting.

Login: send an RCON auth packet with the password field `user:password`.

**One window is enough:** start the server with `--console` and type commands at its `rcon>` prompt
(`ListPlayers`, `say global Hello`, `exit` to stop). The prompt needs no login: it is for whoever sits at the
server machine, and every command is still written to the audit log as user `console`. Other RCON clients can
connect to the same server at the same time. In a real terminal the window shows an ASCII logo and coloured
output (green = done, red = error, yellow = refused); set the environment variable `NO_COLOR` to switch the
colours off. Output sent to a file or a pipe never contains colour codes.

`say` and `saypm` lines start with a name, `[TIMESKIPCON] Server restarts soon`. Change the name (or set it to
`""` for none) at the top of `mods/ScumRcon/Scripts/main.lua`.

```bash
dotnet run --project src/ScumRcon.Server -- --console
```

From another machine or a script, use any RCON client. A small PowerShell one is included for quick tests:

```powershell
.\clients\rcon-client.ps1 -Command ListPlayers      # asks for the password at a hidden prompt
.\clients\rcon-client.ps1                           # interactive prompt
```

**Full guide:** [docs/USAGE.md](docs/USAGE.md) (English) or [docs/USAGE.th.md](docs/USAGE.th.md) (ภาษาไทย): install
step by step, commands, `say`/`saypm`, users and roles, configuration, logs and troubleshooting.
[docs/INSTALL.md](docs/INSTALL.md) has the shorter setup notes.

## Configuration in short

`config/rcon.json` is created on first run and is git-ignored. Important fields:

* `ipAllowlist`: addresses or CIDR ranges that may connect.
* `users`: `name`, `passwordHash`, `role` (`Viewer`, `Moderator`, `Admin`).
* `commandRoles`: minimum role per command word (case-insensitive, `#` ignored).
* `bridge.mode`: `File` (folder bridge) or `Tcp` (loopback bridge with `bridge.secret`).

## Known limits

* The file bridge cannot carry `"` or `\` in a command; the server refuses such commands with a clear message.
* The file bridge has no authentication of its own: protect the commands folder with file permissions.
* **The game's reply text is not returned yet.** The mod listens for chat lines the game sends back (types Admin,
  CommandsOnly, ServerMessage, Error), but in a real-server test of `ListPlayers` it captured **0 lines**, so
  commands answer `Executed: <command> (the game sent no reply text)`. The command itself runs. `say`, `saypm`
  and `players` answer with real text because the mod produces it itself. The game seems to deliver a command's
  output through an internal function rather than chat; reading that would need native (C++) code tied to one
  game build, so it is open work.
* Game commands run in the name of an **online admin** (a player listed in the game's `AdminUsers.ini`), because
  the game prints a command's text in that player's chat. With no admin online they are refused; `say`, `saypm`
  and `players` still work. Not yet confirmed on a real server with several players.
* Requires [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) on the game server. It is not included: run
  `scripts/Install-Ue4ss.ps1`, which downloads the official release and checks its SHA-256 (see
  [docs/INSTALL.md](docs/INSTALL.md)). Game updates can break UE4SS or the game functions the mod uses.

## Roadmap

- [x] RCON server: protocol, login, roles, policy, lockout, audit log, tests
- [x] Reference game-side file bridge (UE4SS Lua) with reply capture, `say` and `saypm`
- [x] A ready-made UE4SS mod that needs no other mod (`mods/ScumRcon`; real-server confirmation pending)
- [ ] Documented command set (players, squads, vehicles, announcements)
- [ ] Scheduler, watchdog, Discord notifications
- [ ] Read-only access to `SCUM.db`
- [ ] Packaged releases with checksums

## Contributing

Issues and pull requests are welcome. Please keep security-relevant changes small, with tests, and report
vulnerabilities privately as described in [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE)

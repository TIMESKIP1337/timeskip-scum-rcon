# Installation (server owners)

## What goes where

All on the machine that runs the SCUM dedicated server:

```
<SCUM server>\SCUM\Binaries\Win64\
    ue4ss\Mods\ScumRcon\                                     ← the game-side mod (copy of mods\ScumRcon)
    ue4ss\Mods\ScumRcon\bridge\rcon_commands\                ← command files (created by the mod)
    ue4ss\Mods\ScumRcon\bridge\rcon_responses\               ← reply files

D:\ScumRcon\                                ← any folder you like
    ScumRcon.Server.exe (+ files)
    config\rcon.json                        ← created on first start
    logs\audit.jsonl                        ← created automatically
```

The RCON server and the game must run on the **same machine**: the bridge only works on this computer.

## UE4SS (required, not included in this repository)

The game-side mod runs inside [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) (MIT licence), which is **not part of
this repository on purpose**: it is a code injector that must come from its official source, it changes with
every SCUM and UE4SS update, and a copy committed here would go stale and could not be checked by you.

**Easiest: use the installer script.** It downloads one pinned official release file, refuses to continue unless
its SHA-256 matches the pinned value, and copies it next to `SCUMServer.exe` without touching your mods or your
`UE4SS-settings.ini` (it never replaces an existing UE4SS unless you add `-Force`, and then it backs up what it
replaces):

```powershell
.\scripts\Install-Ue4ss.ps1 -ServerBinaries 'D:\SCUM-Server\SCUM\Binaries\Win64'
```

The pinned file is `UE4SS_v3.0.1-1217-g4fb9b615.zip` from the official `experimental2` release.

**Use the settings file that comes with this repository.** On a fresh install the script replaces the release's
`UE4SS-settings.ini` with [`extras/ue4ss/UE4SS-settings.scum-server.ini`](../extras/ue4ss/UE4SS-settings.scum-server.ini)
(the release's own file is kept as `UE4SS-settings.ini.official`; add `-KeepOfficialSettings` to skip this). The
reason: with the release's default settings (eight extra engine hooks switched on: `HookInitGameState`,
`HookLoadMap`, `HookEndPlay`, `HookLocalPlayerExec`, `HookAActorTick`, `HookEngineTick`,
`HookGameViewportClientTick`, `HookUStructLink`) a test SCUM server **crashed with an access violation as soon as
the first player joined**. With the file from this repository the same release did not crash. What was tested:
one server, one player, a few minutes, so treat it as "works for us", not as a guarantee, and report problems.
An existing `UE4SS-settings.ini` of yours is never replaced.

The project was originally developed with a self-built `UE4SS v3.0.1 Beta, Git SHA 83b2a4a` and the same settings
file; that build did not crash either. If the pinned release does not work with your SCUM version, try another one:
pass `-Tag`, `-Asset` and the `-Sha256` that GitHub shows for that file on the release page, or build UE4SS from source.

**By hand:** download a release from <https://github.com/UE4SS-RE/RE-UE4SS/releases>, compare its SHA-256 with the
one on the release page, and extract it into `<SCUM server>\SCUM\Binaries\Win64\` so that `dwmapi.dll` sits next
to `SCUMServer.exe` and the `ue4ss` folder next to it. **Also copy**
`extras\ue4ss\UE4SS-settings.scum-server.ini` over `ue4ss\UE4SS-settings.ini` (see above for why). Then start the
server once and check that `ue4ss\UE4SS.log` is created.

## Steps

1. Install the **.NET 8 runtime** (or build with `dotnet publish -c Release src/ScumRcon.Server`).
2. Put the RCON server files in their own folder.
3. Start `ScumRcon.Server`. It creates `config\rcon.json`, prints the admin password **once**, and exits.
   Save the password in a password manager.
4. Open `config\rcon.json`:
   * keep `listenAddress` as `127.0.0.1` unless you need remote access;
   * set `bridge.mode` to `File` and `commandsDirectory` / `responsesDirectory` to the two bridge folders above
     (absolute paths, they must exist; the Lua script creates them);
   * add other users with `ScumRcon.Server --hash-password` (paste the printed hash into `passwordHash`);
   * list which commands lower roles may use in `commandRoles`.
5. Install the game side: install [UE4SS](https://github.com/UE4SS-RE/RE-UE4SS) on the server, then copy the
   folder `mods\ScumRcon` to `<server>\SCUM\Binaries\Win64\ue4ss\Mods\ScumRcon`. The empty file `enabled.txt`
   in it enables the mod (or add the line `ScumRcon : 1` to `ue4ss\Mods\mods.txt`). It needs no other mod.
   To use a bridge folder other than `ue4ss\Mods\ScumRcon\bridge`, edit the `RCON_BRIDGE_DIR` line in its
   `Scripts\main.lua`. Already have your own UE4SS mod? Copy only `Scripts\modules\rcon_bridge.lua` and
   `player_lookup.lua` and wire them up as `main.lua` does ([FILE-BRIDGE.md](FILE-BRIDGE.md)).
6. Start the game server, then `ScumRcon.Server --console` (it prints the address it listens on and shows an
   `rcon>` prompt in the same window: type `ListPlayers`, `say global Hello`, `exit` to stop).
   `extras\windows\Start-ScumRcon.bat` does exactly this, from the server's own folder, on Windows.
7. Firewall: open the RCON port (default **28015**) only to the addresses in `ipAllowlist`.
   **Do not open 28016** (the TCP bridge).

## Remote access safely

RCON is not encrypted. If admins connect from other machines, put the connection inside a VPN or an SSH tunnel,
and keep `ipAllowlist` as narrow as possible.

## Logging in

Use any Source-RCON client. The password field is `user:password`, for example `admin:YourPassword`.
The included `clients\rcon-client.ps1` does this for you and asks for the password at a hidden prompt.

## Try it

```
ListPlayers
say global Server restarts in 5 minutes
saypm 76561198000000042 admin Hello, only you can read this
saypm god admin Hello, same player addressed by his in-game name
```

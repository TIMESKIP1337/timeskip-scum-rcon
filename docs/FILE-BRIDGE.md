# File bridge (RCON server ⇄ game script)

An alternative to the [TCP bridge](BRIDGE-PROTOCOL.md) for servers that already run a game-side script which
watches a folder for command files (for example a UE4SS Lua mod). Set `bridge.mode` to `"File"`.

```json
"bridge": {
  "mode": "File",
  "commandsDirectory":  "C:\\SCUM-Server\\SCUM\\Binaries\\Win64\\ue4ss\\Mods\\ScumRcon\\bridge\\rcon_commands",
  "responsesDirectory": "C:\\SCUM-Server\\SCUM\\Binaries\\Win64\\ue4ss\\Mods\\ScumRcon\\bridge\\rcon_responses",
  "timeoutSeconds": 15
}
```

Both paths must be **absolute**, different from each other, and must already exist (the game script normally
creates them).

## Exchange

1. The RCON server writes `<id>.tmp`, then renames it to `<id>.json` in the commands folder
   (the game never sees half a file). `<id>` is digits only.
2. The game script reads `<id>.json`, deletes it, runs the command, and writes `<id>.json` in the responses folder.
3. The RCON server reads the answer, deletes it, and returns the text to the admin.

```
request : {"command":"ListPlayers","id":"1731070000000001"}
response: {"id":"1731070000000001","response":"Executed: ListPlayers","success":true}
```

The request has **no spaces** and the same key order on purpose: simple scripts read it with text patterns such as
`"command":"([^"]*)"`.

## The game-side mod

[`mods/ScumRcon`](../mods/ScumRcon) is a complete UE4SS Lua mod that needs no other mod: copy the folder to
`ue4ss\Mods\ScumRcon`. Its `main.lua` polls every 500 ms and hooks the chat function;
[`Scripts/modules/rcon_bridge.lua`](../mods/ScumRcon/Scripts/modules/rcon_bridge.lua) is the bridge itself and
`Scripts/modules/player_lookup.lua` finds a player by SteamID. You can also copy the bridge into your own mod.
The bridge:

* it starts **one command per poll**, so a reply can never be mixed up with another command's reply;
* it only accepts ids made of digits (the id becomes a file name);
* it tries to collect the chat lines the server sends to the player while the command runs (chat types Admin,
  CommandsOnly, ServerMessage and Error; normal player chat is ignored) and returns them as the response.
  **This does not work yet on a real server**: for `ListPlayers` it captured 0 lines (the tests, which use a fake
  game, pass). The game probably delivers command output some other way; this is open work;
* so today the usual answer is `Executed: <command> (the game sent no reply text)`. The command did run.

It also understands **`players`**, which answers with the online players (`2 players online:` then one line per
player: number, in-game name, SteamID; `No players online.` when empty). The text is produced by the mod itself
from the game's player objects (`Scripts/modules/player_lookup.lua`, `ListOnline`), which is why it can be returned
even though the game's own command output cannot be captured yet.

It also understands **`say [channel] <message>`**, which broadcasts a chat line on a
chosen chat channel (`0`-`7`, or `default`, `local`, `global`, `squad`, `admin`, `commands`, `server`, `error`;
the default is `server`). The chat colour in the game follows the channel, not markup in the text. The line is
sent to every connected player with the game function `Chat_Client_SendMessageToChat` (the call the game
itself uses to show a line on a player's screen). The game prints no sender name for a line that has no real
player behind it, so the mod writes a name into the text: `[TIMESKIPCON] message` (change `RCON_SAY_NAME` at the
top of `main.lua`, or set it to `""` for no name; up to 32 bytes). Calling the server-side
`Chat_Server_BroadcastChatMessage` from a script displayed nothing in our tests, so it is not used. Messages are
limited to 400 characters and, like every command on this bridge, cannot contain `"` or `\`.

**`saypm <name or steamid> [channel] <message>`** sends the same kind of line to **one** player, for example
`saypm god 4 hello` or `saypm 76561198000000042 admin hello`; the channel works as in `say`. The first word is the
target: 10 or more digits is a SteamID, anything else is the player's **in-game name** (the whole name, English
letter case ignored). The player must be online.

* A name must be **one word**, because the file bridge cannot carry quotation marks. For a name with spaces use
  the SteamID.
* If two online players have the same name, nothing is sent and the answer lists their SteamIDs.
* The player is found with `modules/player_lookup.lua` of the mod (`FindOnline`). Replace `M.findPlayers` if you
  use another lookup.

To use the bridge in your own mod, call `ProcessPendingCommands(nil, channels)` every 500 ms and
`OnChatMessage(channel, text, chatType)` from a hook on the game's "send chat message to player" function
(`mods/ScumRcon/Scripts/main.lua` shows how). The test project `tests/ScumRcon.LuaBridge.Tests` runs these
scripts against the real RCON bridge code and a fake game.

## Limits you should know

* **No double quotes or backslashes in commands.** Such a command would be cut off or damaged by a pattern based
  reader, so the RCON server refuses it with a clear message instead of sending it.
* **No authentication.** Anyone who can write into the commands folder can run admin commands. Give that folder to
  the account that runs the RCON server and to administrators only. Prefer the TCP bridge when you can.
* The answer is whatever the script writes. A script that only confirms ("Executed: ...") does not return the
  command's real output. The reference script below does collect the game's reply lines.
* The reference mod runs a game command through an **online admin's** connection (the game prints the command's
  text in that player's chat) and answers `No admin player is online...` when there is none; see
  [USAGE.md](USAGE.md#where-the-games-text-goes).
* If the game does not pick the command up within `timeoutSeconds`, the file is deleted again so it can never run
  later by surprise. Answers nobody collected are removed after an hour (only files named `<digits>.json`).

# Bridge protocol (RCON server ⇄ game plugin)

The RCON server forwards allowed commands to a plugin running inside the game process.
This document is the contract a plugin must implement.

## Transport

* TCP on **loopback only** (default `127.0.0.1:28016`). The plugin must bind to a loopback address.
* The RCON server is the client; the plugin is the server.
* One request at a time per connection.

## Framing

Every message in both directions is:

```
int32 little-endian  length   (1 .. 65536)
bytes[length]        UTF-8 JSON
```

Frames with a length outside the range must make the plugin close the connection.

## Request

```json
{ "id": "7", "auth": "<bridge.secret>", "command": "ListPlayers" }
```

* `id`: string chosen by the RCON server, unique per connection.
* `auth`: the shared secret from `bridge.secret`.
* `command`: the command line, already checked by the RCON server (no control characters).

## Response

```json
{ "id": "7", "success": true, "response": "text shown to the admin" }
```

* `id` **must** equal the request id. The RCON server treats a different id as a failure.
* `success: false` means the command failed; `response` carries the reason.

## Plugin requirements

1. Compare `auth` with the configured secret in **constant time**
   (e.g. `CryptographicOperations.FixedTimeEquals`). On mismatch, close the connection without replying.
2. Never log the secret.
3. Run game calls on the game thread (queue them; do not call engine functions from the network thread).
4. Reject unknown or malformed JSON by closing the connection.
5. Answer within the configured `bridge.timeoutSeconds` (default 15 s).

## Why a shared secret on loopback?

Loopback is not private from other programs on the same machine. Without the secret, any local process could
send admin commands to the game.

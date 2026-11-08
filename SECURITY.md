# Security policy

ScumRcon holds admin access to game servers, so security reports are taken seriously.

## Reporting a vulnerability

Please **do not open a public issue** for a security problem.
Report it privately through GitHub's "Report a vulnerability" button on the Security tab of this repository.
<!-- Maintainer: enable private vulnerability reporting in the repository settings before publishing. -->

Include what you found, how to reproduce it, and the version. You will get an answer as soon as the
maintainers can; please allow time for a fix before sharing details publicly.

## What the project promises

* No telemetry, no hidden network calls, no self-updating, no obfuscated code.
* Passwords are stored only as salted PBKDF2-SHA256 hashes and are never logged.
* Safe defaults: loopback listen address, explicit IP allowlist, random secrets generated on first run.

## Hardening checklist for server owners

1. Keep `listenAddress` on `127.0.0.1` unless you need remote access. If you do, set a **narrow** `ipAllowlist`.
2. Never expose the bridge port (default 28016) in the firewall. It must stay on loopback.
3. Give people the lowest role that does their job. Commands you do not list in `commandRoles` need Admin.
4. Keep `config/rcon.json` and the `logs` folder out of version control and out of backups you share.
5. Review `logs/audit.jsonl` now and then for `auth_fail`, `blocked` and `cmd_denied` events.

## Known limitations

* The RCON protocol itself is **not encrypted**. Passwords and commands cross the network in clear text.
  Use it only on loopback, a VPN, or an SSH tunnel.
* The `--console` prompt needs no login and runs as Admin: it is meant for whoever sits at the server machine
  (the same trust as being able to write the file-bridge command folder). Its commands are audited as user
  `console`. Do not start the server with `--console` inside a shared or remote terminal you do not trust.
* The file bridge has no authentication of its own: protect its command folder with file permissions.
* The game-side mod (`mods/ScumRcon`) runs inside the game process through UE4SS. UE4SS is not included;
  install it from its official release (see `docs/INSTALL.md`).

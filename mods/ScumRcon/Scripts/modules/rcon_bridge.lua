-- ScumRcon file bridge for a UE4SS Lua mod -- version 2
--
-- This is the game-side bridge of the ScumRcon mod; main.lua next to modules/ wires it up. To use it in
-- another mod, call ProcessPendingCommands(nil, channels) every 500 ms (channels = FindAllOf("PlayerRpcChannel")),
-- hook Chat_Client_SendMessageToChat and call OnChatMessage from it (see ../main.lua), and -- for "saypm" --
-- provide modules/player_lookup.lua (FindOnlineBySteamId) or replace M.findPlayer.
--
-- The folder the RCON server writes to defaults to ue4ss\Mods\ScumRcon\bridge. To use another one set
-- the global RCON_BRIDGE_DIR (relative to the game's Win64 folder) BEFORE this file is loaded; it must
-- match bridge.commandsDirectory / responsesDirectory in config/rcon.json.
--
-- Reads one command file at a time from rcon_commands, runs it through the game's admin command
-- function, COLLECTS the text the game sends back to the player, and writes that text to
-- rcon_responses so the RCON server can return it to the admin.
--
--   request : {"command":"ListPlayers","id":"1731070000000001"}      (written by the RCON server)
--   response: {"id":"1731070000000001","response":"<reply text>","success":true}
--
-- See docs/FILE-BRIDGE.md of the ScumRcon repository for the contract.

local M = {}

local BRIDGE_DIR   = RCON_BRIDGE_DIR or "ue4ss\\Mods\\ScumRcon\\bridge"
local RCON_CMD_DIR  = BRIDGE_DIR .. "\\rcon_commands"
local RCON_RESP_DIR = BRIDGE_DIR .. "\\rcon_responses"

-- The caller polls every 500 ms (see main.lua). After a command has run we keep listening for
-- reply lines until one full tick passes without a new line (but at least MIN_TICKS), and never
-- longer than MAX_TICKS.
local MIN_TICKS = 2
local MAX_TICKS = 8
local MAX_REPLY_CHARS = 30000
local MAX_ID_LENGTH = 24

-- EChatType values that carry command output: Admin 4, CommandsOnly 5, ServerMessage 6, Error 7.
-- Normal player chat (Default 0, Local 1, Global 2, Squad 3) is deliberately ignored.
local REPLY_TYPES = { [4] = true, [5] = true, [6] = true, [7] = true }

-- ── File access (replaceable so the logic can be tested without a game) ───────────────────────
local function DefaultFs()
    local fs = {}

    function fs.list(dir)
        local files = {}
        local handle = io.popen('dir /b "' .. dir .. '" 2>nul')
        if not handle then return files end
        for line in handle:lines() do
            if line:match("%.json$") then
                table.insert(files, line)
            end
        end
        handle:close()
        return files
    end

    function fs.read(path)
        local f = io.open(path, "r")
        if not f then return nil end
        local content = f:read("*a")
        f:close()
        return content
    end

    function fs.write(path, content)
        local f = io.open(path, "w")
        if not f then return false end
        f:write(content)
        f:close()
        return true
    end

    function fs.remove(path)
        os.remove(path)
    end

    function fs.mkdir(path)
        pcall(function() os.execute('mkdir "' .. path .. '" 2>nul') end)
    end

    return fs
end

M.fs = DefaultFs()

M.fs.mkdir(RCON_CMD_DIR)
M.fs.mkdir(RCON_RESP_DIR)

-- ── Helpers ───────────────────────────────────────────────────────────────────────────────────

--- Escapes text so it can sit inside a JSON string.
local function JsonEscape(s)
    s = tostring(s or "")
    -- Works byte by byte on purpose: only bytes 0-31, 127, the quote and the backslash are
    -- touched, so the bytes of multi-byte UTF-8 characters always pass through unchanged,
    -- whatever the locale or the Lua version.
    local out = {}
    for i = 1, #s do
        local b = s:byte(i)
        if b == 34 then
            out[#out + 1] = '\\"'
        elseif b == 92 then
            out[#out + 1] = "\\\\"
        elseif b == 10 then
            out[#out + 1] = "\\n"
        elseif b == 13 then
            out[#out + 1] = "\\r"
        elseif b == 9 then
            out[#out + 1] = "\\t"
        elseif b < 32 or b == 127 then
            out[#out + 1] = string.format("\\u%04x", b)
        else
            out[#out + 1] = s:sub(i, i)
        end
    end
    return table.concat(out)
end
M.JsonEscape = JsonEscape

local function WriteResponse(id, success, text)
    local json = '{"id":"' .. JsonEscape(id) .. '","response":"' .. JsonEscape(text) ..
                 '","success":' .. (success and "true" or "false") .. "}"
    M.fs.write(RCON_RESP_DIR .. "\\" .. id .. ".json", json)
end

--- True when both values are the same game object. If the objects cannot be compared we accept.
local function SameObject(a, b)
    if a == b then return true end
    local ok, equal = pcall(function() return a:GetAddress() == b:GetAddress() end)
    if ok then return equal end
    return true
end

-- ── The "say" and "saypm" commands: chat lines on a chosen chat channel ──────────────────────
-- The chat colour in the game depends on the channel (EChatType), not on markup in the text.
--   say   [channel] <message>             -> every connected player
--   saypm <steamid> [channel] <message>   -> one player
local CHAT_CHANNELS = {
    default = 0, ["local"] = 1, global = 2, squad = 3, admin = 4,
    commands = 5, commandsonly = 5, server = 6, servermessage = 6, error = 7,
}
local CHAT_CHANNEL_NAMES = {
    [0] = "default", [1] = "local", [2] = "global", [3] = "squad",
    [4] = "admin", [5] = "commands", [6] = "server", [7] = "error",
}
local DEFAULT_SAY_CHANNEL = 6          -- "server": reads like an announcement
local MAX_SAY_CHARS = 400
local SAY_SENDER_NAME = "Server"          -- name given as the sender of a "say" line
local MIN_STEAMID_DIGITS = 10             -- shorter numbers cannot be a SteamID
local CHANNEL_HELP = "channel = 0-7 or default/local/global/squad/admin/commands/server/error"
local SAY_USAGE = "Usage: say [channel] <message>   " .. CHANNEL_HELP
local SAYPM_USAGE = "Usage: saypm <name or steamid> [channel] <message>   " .. CHANNEL_HELP ..
                    "   (a name is one word; use the SteamID for a name with spaces)"

--- Finds connected players by in-game name (any letter case) or by SteamID. Returns a list of
--- { channel = <PlayerRpcChannel>, name = <string>, steamId = <string> }, empty when nobody matches.
--- Uses modules/player_lookup.lua of this mod (FindOnline); a mod with its own lookup that has
--- FindAllOnlineMatchesByNameOrSteamID is understood too. Replaceable so it can be tested without a game.
function M.findPlayers(target)
    local ok, result = pcall(function()
        local lookup = require("modules.player_lookup")
        if lookup.FindOnline then return lookup.FindOnline(target) end
        local found = {}
        for _, m in ipairs(lookup.FindAllOnlineMatchesByNameOrSteamID(target) or {}) do
            found[#found + 1] = { channel = m.ch, name = m.name, steamId = m.uniqueId }
        end
        return found
    end)
    if ok and type(result) == "table" then return result end
    if not ok then print("[RCON-Bridge] player lookup failed: " .. tostring(result)) end
    return {}
end

--- Everybody who is online, as a list of { name = <string>, steamId = <string> } (empty if nobody).
--- Uses modules/player_lookup.lua of this mod (ListOnline). Replaceable so it can be tested without a game.
function M.listPlayers()
    local ok, result = pcall(function()
        local lookup = require("modules.player_lookup")
        return lookup.ListOnline and lookup.ListOnline() or {}
    end)
    if ok and type(result) == "table" then return result end
    if not ok then print("[RCON-Bridge] player list failed: " .. tostring(result)) end
    return {}
end

--- Splits "[channel] <message>" and checks it. Returns channel, message -- or nil, errorText.
local function ParseChannelAndMessage(rest, usage)
    rest = rest:gsub("^%s+", "")
    rest = rest:gsub("%s+$", "")

    local channel = DEFAULT_SAY_CHANNEL
    local message = rest
    local first, remainder = rest:match("^(%S+)%s*(.*)$")
    if first then
        local named = CHAT_CHANNELS[first:lower()]
        if named then
            channel, message = named, remainder
        elseif first:match("^%d+$") then
            if #first == 1 and tonumber(first) <= 7 then
                channel, message = tonumber(first), remainder
            else
                return nil, "The channel must be 0-7 or a name. " .. usage
            end
        end
    end

    if message == "" then return nil, usage end
    if #message > MAX_SAY_CHARS then
        return nil, "The message is longer than " .. MAX_SAY_CHARS .. " characters."
    end
    return channel, message
end

--- A live player channel (not the class default object).
local function IsUsableChannel(ch)
    local valid = false
    pcall(function() valid = ch:IsValid() end)
    if not valid then return false end
    local isDefaultObject = false
    pcall(function() isDefaultObject = tostring(ch:GetFullName()):find("Default__", 1, true) ~= nil end)
    return not isDefaultObject
end

-- Chat_Server_BroadcastChatMessage is the call a player's own chat box makes TO the server, so
-- calling it from here shows nothing. What puts a line on a player's screen is the CLIENT call
-- Chat_Client_SendMessageToChat (the game itself uses it to show command replies).
--- The name shown in front of "say"/"saypm" lines, like "[MyServer] hello". The game does not print a
--- sender name for a line that has no real player behind it, so the name is written into the text.
--- nil or "" means no name. Control characters are removed and the name is cut to 32 bytes (never in the
--- middle of a multi-byte character, so any language is safe).
M.sayName = nil
local MAX_SAY_NAME = 32

function M.SetSayName(name)
    name = tostring(name or "")
    -- byte by byte on purpose (see JsonEscape): only bytes 0-31 and 127 are control characters
    local kept = {}
    for i = 1, #name do
        local b = name:byte(i)
        if b >= 32 and b ~= 127 then kept[#kept + 1] = name:sub(i, i) end
    end
    name = table.concat(kept):gsub("^[ \t]+", ""):gsub("[ \t]+$", "")

    if #name > MAX_SAY_NAME then
        local cut = MAX_SAY_NAME
        while cut > 0 do                       -- the next byte continues a character: step back to its start
            local b = name:byte(cut + 1)
            if b and b >= 0x80 and b <= 0xBF then cut = cut - 1 else break end
        end
        name = name:sub(1, cut)
    end
    M.sayName = (name ~= "") and name or nil
end

local function SendLine(ch, message, channel)
    local text = M.sayName and ("[" .. M.sayName .. "] " .. message) or message
    -- (Message, SenderPlayerState, senderFakeName, netIdRepl, Channel, shouldCopyToClipboard)
    return pcall(function()
        ch["Chat_Client_SendMessageToChat"](ch, FString(text), nil, FString(M.sayName or SAY_SENDER_NAME), {}, channel, false)
    end)
end

--- Handles "say [channel] <message>" (everyone). `rest` is the text after the word "say".
local function HandleSay(id, rest, rpcChannels)
    local channel, message = ParseChannelAndMessage(rest, SAY_USAGE)
    if not channel then
        WriteResponse(id, false, message)
        return
    end

    local reached, failed = 0, 0
    local lastError
    if rpcChannels then
        for _, ch in pairs(rpcChannels) do
            if IsUsableChannel(ch) then
                local ok, err = SendLine(ch, message, channel)
                if ok then
                    reached = reached + 1
                else
                    failed = failed + 1
                    lastError = err
                    print("[RCON-Bridge] say failed on one channel: " .. tostring(err))
                end
            end
        end
    end

    if reached > 0 then
        print(string.format("[RCON-Bridge] say on channel %s (%d) to %d player channel(s)", CHAT_CHANNEL_NAMES[channel], channel, reached))
        WriteResponse(id, true, string.format("Sent on channel %s (%d) to %d player(s): %s",
            CHAT_CHANNEL_NAMES[channel], channel, reached, message))
    elseif failed > 0 then
        WriteResponse(id, false, "The game refused the chat call: " .. tostring(lastError))
    else
        WriteResponse(id, false, "Failed to send (no usable player channel)")
    end
end

--- Handles "saypm <name or steamid> [channel] <message>" (one player). `rest` is the text after "saypm".
--- The target is the first word: a SteamID (10 or more digits) or an in-game name (one word, any letter case).
local function HandleSayPm(id, rest)
    local target, remainder = rest:match("^%s*(%S+)%s*(.*)$")
    if not target then
        WriteResponse(id, false, SAYPM_USAGE)
        return
    end

    local channel, message = ParseChannelAndMessage(remainder, SAYPM_USAGE)
    if not channel then
        WriteResponse(id, false, message)
        return
    end

    local isSteamId = target:match("^%d+$") ~= nil and #target >= MIN_STEAMID_DIGITS
    local how = isSteamId and ("with SteamID " .. target) or ("named " .. target)

    local usable = {}
    for _, match in ipairs(M.findPlayers(target)) do
        if match.channel and IsUsableChannel(match.channel) then usable[#usable + 1] = match end
    end

    if #usable == 0 then
        WriteResponse(id, false, "No online player " .. how)
        return
    end
    if #usable > 1 then
        local ids = {}
        for _, match in ipairs(usable) do ids[#ids + 1] = tostring(match.steamId or "?") end
        WriteResponse(id, false, string.format("More than one online player %s (%d). Use the SteamID instead: %s",
            how, #usable, table.concat(ids, ", ")))
        return
    end

    local player = usable[1]
    local ok, err = SendLine(player.channel, message, channel)
    if not ok then
        print("[RCON-Bridge] saypm failed: " .. tostring(err))
        WriteResponse(id, false, "The game refused the chat call: " .. tostring(err))
        return
    end

    local shownName = (player.name and player.name ~= "") and tostring(player.name) or target
    local shownId = tostring(player.steamId or target)
    print(string.format("[RCON-Bridge] saypm on channel %s (%d) to %s", CHAT_CHANNEL_NAMES[channel], channel, shownId))
    WriteResponse(id, true, string.format("Sent on channel %s (%d) to %s (%s): %s",
        CHAT_CHANNEL_NAMES[channel], channel, shownName, shownId, message))
end

--- Handles "players": the answer comes from the mod itself, so unlike the game's own ListPlayers (whose text
--- is shown only in the game) the RCON user really sees it.
local function RemoveControlBytes(text)
    -- byte by byte on purpose (see JsonEscape): only bytes 0-31 and 127 are control characters
    text = tostring(text or "")
    local kept = {}
    for i = 1, #text do
        local b = text:byte(i)
        if b >= 32 and b ~= 127 then kept[#kept + 1] = text:sub(i, i) end
    end
    return table.concat(kept)
end

local function HandlePlayers(id)
    local players = M.listPlayers()
    if #players == 0 then
        WriteResponse(id, true, "No players online.")
        return
    end

    local lines = { string.format("%d player%s online:", #players, #players == 1 and "" or "s") }
    for i, player in ipairs(players) do
        lines[#lines + 1] = string.format("%d. %s  %s", i, RemoveControlBytes(player.name), tostring(player.steamId))
    end
    print(string.format("[RCON-Bridge] players: %d online", #players))
    WriteResponse(id, true, table.concat(lines, "\n"))
end

-- ── Which player runs a game command ──────────────────────────────────────────────────────────
-- The game runs an admin command in the name of a connected player and prints the command's text into THAT
-- player's chat. "The first player found" could therefore show a player list (names, positions, ...) on the
-- screen of any player and run admin commands in his name. So by default only a player who is in the game's
-- own admin list (AdminUsers.ini) is used, and the command is refused when no such player is online.
M.executorMode = "admin"                 -- "admin" (default) or "any" (first player found: the old, leaky way)
M.adminUsersFile = RCON_ADMIN_USERS_FILE or "..\\..\\Saved\\Config\\WindowsServer\\AdminUsers.ini"

function M.SetExecutorMode(mode)
    M.executorMode = (tostring(mode or ""):lower() == "any") and "any" or "admin"
end

--- The SteamIDs in the game's admin list, as a set { [steamId] = true }; or nil and a reason when the file
--- cannot be read. A line looks like  76561198000000000[Godmode]  (the part in brackets is ignored).
function M.readAdminIds()
    local content = M.fs.read(M.adminUsersFile)
    if not content then return nil, "cannot read " .. tostring(M.adminUsersFile) end
    content = content:gsub("^\239\187\191", "")          -- a UTF-8 byte order mark
    local ids = {}
    for line in content:gmatch("[^\r\n]+") do
        local id = line:match("^%s*(%d+)")
        if id and #id >= 10 then ids[id] = true end
    end
    return ids
end

--- Returns the player channel that runs a game command, or nil and the text to answer with.
--- A third value is the player ({ name, steamId, channel }) when the choice was made by admin rights.
function M.chooseExecutor(rpcChannels)
    if M.executorMode == "any" then
        for _, channel in pairs(rpcChannels or {}) do
            if IsUsableChannel(channel) then return channel end
        end
        return nil, "Failed to execute"
    end

    local admins, why = M.readAdminIds()
    if not admins then
        return nil, "Cannot tell who the admins are: " .. why .. ". Set RCON_ADMIN_USERS_FILE in the mod's " ..
                    "main.lua to the right path (or RCON_EXECUTOR = \"any\", which is not recommended)."
    end
    for _, player in ipairs(M.listPlayers()) do
        if player.channel and admins[tostring(player.steamId)] and IsUsableChannel(player.channel) then
            return player.channel, nil, player
        end
    end
    return nil, "No admin player is online. The game runs admin commands in the name of an online player, " ..
                "so one of the server's admins (AdminUsers.ini) must be in the game."
end

-- ── State of the command that is currently waiting for its reply ──────────────────────────────
local pending = nil

local function Finish()
    local p = pending
    pending = nil

    local text
    if #p.parts > 0 then
        text = table.concat(p.parts, "\n")
        if #text > MAX_REPLY_CHARS then
            text = text:sub(1, MAX_REPLY_CHARS) .. "\n... (reply cut)"
        end
    else
        text = "Executed: " .. p.command .. " (the game sent no reply text)"
    end

    local ignored = 0
    local kinds = {}
    for chatType, n in pairs(p.ignored) do
        ignored = ignored + n
        table.insert(kinds, tostring(chatType) .. "x" .. n)
    end
    table.sort(kinds)
    print(string.format("[RCON-Bridge] Reply for %s: %d line(s) captured, %d other chat line(s) ignored%s",
        p.id, #p.parts, ignored, (#kinds > 0) and (" [types " .. table.concat(kinds, ",") .. "]") or ""))

    WriteResponse(p.id, true, text)
end

--- Called by a hook on Chat_Client_SendMessageToChat for every chat line the server sends.
--- `channel` is the PlayerRpcChannel the line goes to, `text` the message, `chatType` the EChatType number.
function M.OnChatMessage(channel, text, chatType)
    local p = pending
    if not p or not text or text == "" then return end
    if p.channel and channel and not SameObject(p.channel, channel) then
        if M.debug then print("[RCON-Bridge] [debug] a chat line for another player channel was ignored") end
        return
    end

    chatType = tonumber(chatType)
    if chatType and REPLY_TYPES[chatType] then
        table.insert(p.parts, text)
    else
        local key = chatType or "?"
        p.ignored[key] = (p.ignored[key] or 0) + 1
    end
end

--- True while a command is waiting for its reply (lets the hook skip all work otherwise).
function M.IsCapturing()
    return pending ~= nil
end

local function Advance()
    local p = pending
    p.ticks = p.ticks + 1

    if #p.parts ~= p.lastCount then
        p.lastCount = #p.parts
        p.quiet = 0
    else
        p.quiet = p.quiet + 1
    end

    if (p.ticks >= MIN_TICKS and p.quiet >= 1) or p.ticks >= MAX_TICKS then
        Finish()
    end
end

--- Call every 500 ms. Starts at most ONE command per call, so a reply can never be mixed up with
--- another command's reply.
function M.ProcessPendingCommands(AdminCmdFunc, rpcChannels)
    if pending then
        Advance()
        return
    end

    local files = M.fs.list(RCON_CMD_DIR)
    if not files or #files == 0 then return end
    table.sort(files)

    local filepath = RCON_CMD_DIR .. "\\" .. files[1]
    local content = M.fs.read(filepath)
    M.fs.remove(filepath)
    if not content then return end

    local command = content:match('"command":"([^"]*)"')
    local id = content:match('"id":"([^"]*)"')
    if not command then return end

    -- The in-game chat box removes the leading "#" before it calls the game's function (the game
    -- log shows typed commands WITHOUT it), so do the same. Sent with the "#", the game does not
    -- run native commands such as SpawnItem.
    command = command:gsub("^%s+", "")
    command = command:gsub("^#+", "")
    command = command:gsub("^%s+", "")
    if command == "" then return end

    -- The id becomes a file name, so only plain digits are accepted (no folders, no "..").
    if not id or not id:match("^%d+$") or #id > MAX_ID_LENGTH then
        print("[RCON-Bridge] Ignored a command file with a missing or unsafe id")
        return
    end

    -- "say" and "saypm" are handled here, they are not game commands
    local word = command:match("^(%S+)")
    local lowered = word and word:lower()
    if lowered == "say" then
        print("[RCON-Bridge] Executing: " .. command)
        HandleSay(id, command:sub(#word + 1), rpcChannels)
        return
    elseif lowered == "saypm" then
        print("[RCON-Bridge] Executing: " .. command)
        HandleSayPm(id, command:sub(#word + 1))
        return
    elseif lowered == "players" or lowered == "listplayers" then
        -- "ListPlayers" is answered here too and NEVER sent to the game: the game would show its list (names,
        -- SteamIDs, locations, balances of everybody) in the chat of the player whose channel ran the command.
        print("[RCON-Bridge] Executing: " .. command)
        HandlePlayers(id)
        return
    end

    print("[RCON-Bridge] Executing: " .. command)

    local channel, problem, player = M.chooseExecutor(rpcChannels)
    if not channel then
        print("[RCON-Bridge] Not executed: " .. tostring(problem))
        WriteResponse(id, false, problem)
        return
    end
    if player then
        print(string.format("[RCON-Bridge] Running as admin %s (%s)", tostring(player.name), tostring(player.steamId)))
    end

    -- Start listening BEFORE the call: the game may answer while the call is running.
    pending = {
        id = id, command = command, channel = channel,
        parts = {}, ignored = {}, ticks = 0, lastCount = 0, quiet = 0,
    }
    if M.debug then
        pcall(function()
            print("[RCON-Bridge] [debug] running on channel " .. string.format("%X", channel:GetAddress()))
        end)
    end
    local ok, err = pcall(function()
        channel["Chat_Server_ProcessAdminCommand"](channel, FString(command))
    end)
    if not ok then
        print("[RCON-Bridge] RpcChannel failed: " .. tostring(err))
        pending = nil
        WriteResponse(id, false, "Failed to execute")
        return
    end
    print("[RCON-Bridge] Success via RpcChannel")
    pending.lastCount = #pending.parts
end

return M

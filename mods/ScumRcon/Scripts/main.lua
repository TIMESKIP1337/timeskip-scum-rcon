--[[
    ScumRcon -- UE4SS mod for SCUM dedicated servers (game side of the ScumRcon RCON server).

    It does three things:
      1. every 500 ms it asks modules/rcon_bridge.lua to look for command files written by the
         RCON server and run them in the game as admin commands;
      2. it listens to the chat lines the game sends back, to return them as the reply (experimental: on a
         real server this has captured nothing so far, see the README);
      3. it adds two commands of its own: "say" and "saypm" (see docs/FILE-BRIDGE.md).

    It needs no other mod. Install: copy this folder to <server>\SCUM\Binaries\Win64\ue4ss\Mods\ScumRcon
    (see docs/INSTALL.md).

    To use another folder than ue4ss\Mods\ScumRcon\bridge for the exchange files, uncomment the next line
    (relative to the Win64 folder; it must match bridge.commandsDirectory / responsesDirectory):
]]
-- RCON_BRIDGE_DIR = "ue4ss\\Mods\\ScumRcon\\bridge"

--[[
    Name in front of the lines sent by "say" and "saypm", for example  [TIMESKIPCON] Server restarts soon
    Change it here (up to 32 bytes), or set it to "" to send lines without a name.
]]
if RCON_SAY_NAME == nil then RCON_SAY_NAME = "TIMESKIPCON" end

--[[
    Which player runs a game command. The game prints a command's text into the chat of the player that runs it,
    so by default only an ADMIN of the server (listed in SCUM\Saved\Config\WindowsServer\AdminUsers.ini) is used,
    and a command is refused while no admin is online. Say-style commands (say, saypm, players) need no admin.
        RCON_EXECUTOR = "any"     use the first player found (not recommended: his chat shows the output)
    If your AdminUsers.ini is somewhere else, set its path (relative to the Win64 folder, or absolute):
        RCON_ADMIN_USERS_FILE = "..\\..\\Saved\\Config\\WindowsServer\\AdminUsers.ini"
    Both lines must come BEFORE the bridge is loaded, so keep them here.
]]
-- RCON_EXECUTOR = "any"
-- RCON_ADMIN_USERS_FILE = "..\\..\\Saved\\Config\\WindowsServer\\AdminUsers.ini"

local TAG = "[ScumRcon]"
local POLL_MS = 500
local CHANNEL_REFRESH_TICKS = 10      -- look for new players about every 5 seconds

-- Let require("modules.xxx") find this mod's files.
local scriptsDir = "."
pcall(function()
    local source = debug.getinfo(1, "S").source
    scriptsDir = source:match("@(.+)[/\\]") or "."
end)
pcall(function()
    package.path = scriptsDir .. "\\?.lua;" .. scriptsDir .. "/?.lua;" .. (package.path or "")
end)

local loaded, bridge = pcall(require, "modules.rcon_bridge")
if not loaded or not bridge then
    print(TAG .. " The RCON bridge could not be loaded: " .. tostring(bridge))
    return
end

if RCON_SAY_NAME then bridge.SetSayName(RCON_SAY_NAME) end
if RCON_EXECUTOR ~= nil then bridge.SetExecutorMode(RCON_EXECUTOR) end

local adminCommand = StaticFindObject("/Script/SCUM.PlayerRpcChannel:Chat_Server_ProcessAdminCommand")

-- ── Reply capture ────────────────────────────────────────────────────────────────────────────
-- The game answers an admin command by sending chat lines to the player. While a command waits for
-- its reply, hand every such line to the bridge.
local hooked, hookError = pcall(function()
    RegisterHook("/Script/SCUM.PlayerRpcChannel:Chat_Client_SendMessageToChat",
        function(self, Message, SenderPlayerState, senderFakeName, netIdRepl, Channel, shouldCopyToClipboard)
            if not bridge.IsCapturing() then return end
            pcall(function()
                bridge.OnChatMessage(self:get(), Message:get():ToString(), Channel:get())
            end)
        end)
end)
if not hooked then
    print(TAG .. " Could not hook the chat function, replies will not be returned: " .. tostring(hookError))
end

-- ── Debug mode (off by default) ──────────────────────────────────────────────────────────────
-- Logs which game functions run when a command is executed, to find where the game delivers the output of
-- a command. Switch it on by creating an empty file  ue4ss\Mods\ScumRcon\debug.txt  and restarting the game
-- server; remove the file to switch it off. Everything is written to UE4SS.log with the prefix [ScumRcon] [debug].
local function DebugFileExists()
    local ok, found = pcall(function()
        local f = io.open(scriptsDir .. "\\..\\debug.txt", "r")
        if f then f:close() return true end
        return false
    end)
    return ok and found == true
end

if RCON_DEBUG == true or DebugFileExists() then
    bridge.debug = true

    local function Short(text)
        text = tostring(text or ""):gsub("%c", " ")
        if #text > 160 then text = text:sub(1, 160) .. "..." end
        return text
    end
    local function Address(object)
        local ok, value = pcall(function() return string.format("%X", object:GetAddress()) end)
        return ok and value or "?"
    end
    local function Text(param)
        local ok, value = pcall(function() return param:get():ToString() end)
        return ok and value or "?"
    end
    local function Raw(param)
        local ok, value = pcall(function() return param:get() end)
        return ok and tostring(value) or "?"
    end
    local function DebugHook(path, callback)
        local ok, err = pcall(RegisterHook, path, function(...)
            pcall(callback, ...)
        end)
        print(TAG .. " [debug] hook " .. path .. (ok and " registered" or (" FAILED: " .. tostring(err))))
    end

    DebugHook("/Script/SCUM.PlayerRpcChannel:Chat_Server_ProcessAdminCommand", function(self, commandText)
        print(TAG .. " [debug] Chat_Server_ProcessAdminCommand channel=" .. Address(self:get()) .. " command=" .. Short(Text(commandText)))
    end)
    DebugHook("/Script/SCUM.PlayerRpcChannel:Chat_Client_ProcessAdminCommand", function(self, commandText)
        print(TAG .. " [debug] Chat_Client_ProcessAdminCommand channel=" .. Address(self:get()) .. " command=" .. Short(Text(commandText)))
    end)
    DebugHook("/Script/SCUM.PlayerRpcChannel:Chat_Client_SendMessageToChat",
        function(self, Message, SenderPlayerState, senderFakeName, netIdRepl, Channel)
            print(TAG .. " [debug] Chat_Client_SendMessageToChat channel=" .. Address(self:get()) ..
                  " type=" .. Raw(Channel) .. " text=" .. Short(Text(Message)))
        end)
    DebugHook("/Script/SCUM.PlayerRpcChannel:Chat_Client_SendSquadMessageToChat", function(self)
        print(TAG .. " [debug] Chat_Client_SendSquadMessageToChat channel=" .. Address(self:get()))
    end)
    DebugHook("/Script/SCUM.MiscStatics:SendChatLineToPlayer", function(self, PlayerController, Text_, ChatType)
        print(TAG .. " [debug] MiscStatics.SendChatLineToPlayer type=" .. Raw(ChatType) .. " text=" .. Short(Text(Text_)))
    end)
    DebugHook("/Script/SCUM.MiscStatics:BroadcastChatLine", function(self, WorldContextObject, Text_, ChatType)
        print(TAG .. " [debug] MiscStatics.BroadcastChatLine type=" .. Raw(ChatType) .. " text=" .. Short(Text(Text_)))
    end)
    print(TAG .. " [debug] debug mode is ON (remove ue4ss\\Mods\\ScumRcon\\debug.txt to switch it off)")
end

-- ── Polling ──────────────────────────────────────────────────────────────────────────────────
local channels = {}
local ticksSinceRefresh = CHANNEL_REFRESH_TICKS

local function HasLiveChannel()
    for _, channel in pairs(channels) do
        local ok, valid = pcall(function() return channel:IsValid() end)
        if ok and valid then return true end
    end
    return false
end

LoopAsync(POLL_MS, function()
    -- New players are picked up every few seconds; if none of the known ones is alive, at once.
    ticksSinceRefresh = ticksSinceRefresh + 1
    if ticksSinceRefresh >= CHANNEL_REFRESH_TICKS or not HasLiveChannel() then
        ticksSinceRefresh = 0
        pcall(function()
            local found = FindAllOf("PlayerRpcChannel")
            if found then channels = found end
        end)
    end

    pcall(function()
        bridge.ProcessPendingCommands(adminCommand, channels)
    end)

    return false    -- keep looping
end)

print(TAG .. " RCON bridge active, polling for commands every " .. POLL_MS .. " ms")

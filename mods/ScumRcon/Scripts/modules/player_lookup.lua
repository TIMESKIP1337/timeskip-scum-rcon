-- ScumRcon: finds a connected player by SteamID.
--
-- Used by the "saypm" command. Standalone: it needs nothing from other mods.

local M = {}

local function try(fn)
    local ok, v = pcall(fn)
    if ok then return v end
    return nil
end

--- The PlayerController that owns a PlayerRpcChannel (the channel is its component).
local function ControllerOf(channel)
    local owner = try(function() return channel:GetOwner() end)
    if owner then return owner end

    local current = channel
    for _ = 1, 12 do
        current = try(function() return current:GetOuter() end)
        if not current then return nil end
        local full = try(function() return current:GetFullName() end) or ""
        if tostring(full):find("PlayerController", 1, true) then return current end
    end
    return nil
end

local function SteamIdOf(controller)
    local id = try(function() return controller:GetUserId():ToString() end)
    if id and id ~= "" then return id end
    id = try(function() return controller.UserId:ToString() end)
    if id and id ~= "" then return id end
    return nil
end

local function NameOf(controller)
    local state = try(function() return controller.PlayerState end)
    if not state then return nil end
    local name = try(function() return state.PlayerNamePrivate:ToString() end)
    if name and name ~= "" then return name end
    return try(function() return state:GetPlayerName():ToString() end)
end

--- Finds the online players whose SteamID, or in-game name, equals `target`.
--- A target of 10 or more digits is a SteamID; anything else is a name (compared without regard to
--- English letter case, otherwise exactly). Returns a list of { channel, name, steamId } (empty if none).
function M.FindOnline(target)
    local found = {}
    if not target or target == "" then return found end
    local channels = try(function() return FindAllOf("PlayerRpcChannel") end)
    if not channels then return found end

    local byId = tostring(target):match("^%d+$") ~= nil and #tostring(target) >= 10
    local wanted = tostring(target):lower()

    for i = 1, #channels do
        local channel = channels[i]
        local valid = channel and try(function() return channel:IsValid() end)
        local full = valid and (try(function() return channel:GetFullName() end) or "") or ""
        if valid and not tostring(full):find("Default__", 1, true) then
            local controller = ControllerOf(channel)
            if controller then
                local steamId = SteamIdOf(controller)
                local name = NameOf(controller) or ""
                local hit
                if byId then hit = (steamId == tostring(target))
                else hit = (name ~= "" and name:lower() == wanted) end
                if hit then found[#found + 1] = { channel = channel, name = name, steamId = steamId } end
            end
        end
    end
    return found
end

--- Returns the PlayerRpcChannel and the name of the online player with this SteamID, or nil.
function M.FindOnlineBySteamId(steamId)
    local match = M.FindOnline(steamId)[1]
    if match then return match.channel, match.name end
    return nil
end

--- Everybody who is online: a list of { name, steamId }, sorted by name (English letter case ignored), then
--- by SteamID. A player is listed once. Empty when nobody is online.
function M.ListOnline()
    local list, seen = {}, {}
    local channels = try(function() return FindAllOf("PlayerRpcChannel") end)
    if not channels then return list end

    for i = 1, #channels do
        local channel = channels[i]
        local valid = channel and try(function() return channel:IsValid() end)
        local full = valid and (try(function() return channel:GetFullName() end) or "") or ""
        if valid and not tostring(full):find("Default__", 1, true) then
            local controller = ControllerOf(channel)
            local steamId = controller and SteamIdOf(controller)
            if steamId and not seen[steamId] then
                seen[steamId] = true
                list[#list + 1] = { name = NameOf(controller) or "", steamId = steamId, channel = channel }
            end
        end
    end

    table.sort(list, function(a, b)
        local an, bn = a.name:lower(), b.name:lower()
        if an ~= bn then return an < bn end
        return a.steamId < b.steamId
    end)
    return list
end

return M

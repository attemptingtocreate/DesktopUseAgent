--[[
  DesktopUseAgent Roblox Studio bridge plugin (Phase 2 MVP)

  Requires:
  - Roblox Studio HttpService enabled (Game Settings > Security > Allow HTTP Requests)
  - DesktopUseAgent Windows agent running locally
  - Opt-in polling enabled from the plugin toolbar

  The agent hosts a loopback-only HTTP bridge. This plugin polls for commands and POSTs results.
]]

local Config = require(script:WaitForChild("DesktopUseAgentConfig"))

local HttpService = game:GetService("HttpService")
local RunService = game:GetService("RunService")
local Selection = game:GetService("Selection")
local ChangeHistoryService = game:GetService("ChangeHistoryService")
local StudioService = game:GetService("StudioService")

local PLUGIN_NAME = "DesktopUseAgent"
local POLL_INTERVAL = 1.0
local MAX_BACKOFF = 8.0

local config = {
	host = Config.host or "127.0.0.1",
	port = Config.port or 18374,
	token = Config.token,
}

local toolbar = plugin:CreateToolbar(PLUGIN_NAME)
local enableButton = toolbar:CreateButton(
	"Enable Agent Bridge",
	"Opt in to poll the local DesktopUseAgent Roblox bridge (127.0.0.1). Requires HttpService.",
	"rbxassetid://6034509993"
)

local pollingEnabled = false
local sessionId = HttpService:GenerateGUID(false)
local instanceMap = {}
local nextInstanceId = 1
local commandQueue = {}
local backoff = POLL_INTERVAL

local function baseUrl(path)
	return string.format("http://%s:%d%s?token=%s", config.host, config.port, path, HttpService:UrlEncode(config.token or ""))
end

local function postJson(path, body)
	local url = baseUrl(path)
	local payload = HttpService:JSONEncode(body)
	return HttpService:PostAsync(url, payload, Enum.HttpContentType.ApplicationJson, false)
end

local function getJson(path, query)
	local url = baseUrl(path)
	if query then
		url ..= "&" .. query
	end
	local raw = HttpService:GetAsync(url, false)
	return HttpService:JSONDecode(raw)
end

local function getPlaceName()
	local ok, name = pcall(function()
		if StudioService and StudioService.ActivePlace then
			return StudioService.ActivePlace.Name
		end
		return game.Name
	end)
	if ok and type(name) == "string" then
		return name
	end
	return game.Name
end

local function resetInstanceMap()
	table.clear(instanceMap)
	nextInstanceId = 1
end

local function getInstanceId(instance)
	local existing = instanceMap[instance]
	if existing then
		return existing
	end
	local id = "inst_" .. tostring(nextInstanceId)
	nextInstanceId += 1
	instanceMap[instance] = id
	return id
end

local function resolveInstance(instanceId)
	for instance, id in pairs(instanceMap) do
		if id == instanceId then
			return instance
		end
	end
	return nil
end

local function getPath(instance)
	local parts = {}
	local current = instance
	while current and current ~= game do
		table.insert(parts, 1, current.Name)
		current = current.Parent
	end
	return table.concat(parts, ".")
end

local PROPERTY_KIND = {
	Name = "string",
	Anchored = "bool",
	CanCollide = "bool",
	CanQuery = "bool",
	CanTouch = "bool",
	Locked = "bool",
	Transparency = "number",
	Reflectance = "number",
	Visible = "bool",
	Enabled = "bool",
	Value = "number",
	Text = "string",
	BackgroundTransparency = "number",
	BorderSizePixel = "number",
	ZIndex = "number",
	LayoutOrder = "number",
	Rotation = "number",
	Color = "Color3",
	TextColor3 = "Color3",
	BackgroundColor3 = "Color3",
	Size = "Vector3",
	Position = "Vector3",
	Orientation = "Vector3",
}

local BLOCKED_PROPERTIES = {
	Parent = true,
	Source = true,
	Archivable = true,
}

local function isFiniteNumber(value)
	return type(value) == "number" and value == value and value ~= math.huge and value ~= -math.huge
end

local function serializeTaggedColor3(value)
	return { type = "Color3", r = value.R, g = value.G, b = value.B }
end

local function serializeTaggedVector3(value)
	return { type = "Vector3", x = value.X, y = value.Y, z = value.Z }
end

local function serializeProperty(instance, propertyName)
	local kind = PROPERTY_KIND[propertyName]
	if not kind then
		return nil
	end
	local ok, value = pcall(function()
		return instance[propertyName]
	end)
	if not ok then
		return nil
	end
	if kind == "string" and type(value) == "string" then
		return value
	end
	if kind == "bool" and type(value) == "boolean" then
		return value
	end
	if kind == "number" and isFiniteNumber(value) then
		return value
	end
	if kind == "Color3" and typeof(value) == "Color3" then
		return serializeTaggedColor3(value)
	end
	if kind == "Vector3" and typeof(value) == "Vector3" then
		return serializeTaggedVector3(value)
	end
	return nil
end

local function decodeTaggedValue(propertyName, value)
	local kind = PROPERTY_KIND[propertyName]
	if not kind then
		return nil, "property_not_allowed"
	end
	if kind == "string" then
		if type(value) ~= "string" then
			return nil, "invalid_string"
		end
		return value
	end
	if kind == "bool" then
		if type(value) ~= "boolean" then
			return nil, "invalid_bool"
		end
		return value
	end
	if kind == "number" then
		if not isFiniteNumber(value) then
			return nil, "invalid_number"
		end
		if (propertyName == "Transparency" or propertyName == "Reflectance" or propertyName == "BackgroundTransparency") and (value < 0 or value > 1) then
			return nil, "invalid_range"
		end
		return value
	end
	if kind == "Color3" then
		if type(value) ~= "table" or value.type ~= "Color3" then
			return nil, "invalid_color3"
		end
		if not isFiniteNumber(value.r) or not isFiniteNumber(value.g) or not isFiniteNumber(value.b) then
			return nil, "invalid_color3"
		end
		if value.r < 0 or value.r > 1 or value.g < 0 or value.g > 1 or value.b < 0 or value.b > 1 then
			return nil, "invalid_color3"
		end
		return Color3.new(value.r, value.g, value.b)
	end
	if kind == "Vector3" then
		if type(value) ~= "table" or value.type ~= "Vector3" then
			return nil, "invalid_vector3"
		end
		if not isFiniteNumber(value.x) or not isFiniteNumber(value.y) or not isFiniteNumber(value.z) then
			return nil, "invalid_vector3"
		end
		return Vector3.new(value.x, value.y, value.z)
	end
	return nil, "unsupported_kind"
end

local function resolveRoot(rootPath)
	if type(rootPath) ~= "string" or rootPath == "" then
		return game
	end
	local current = game
	for segment in string.gmatch(rootPath, "[^%.]+") do
		current = current:FindFirstChild(segment)
		if not current then
			return nil
		end
	end
	return current
end

local function buildHierarchy(root, depth, maxNodes)
	local nodes = {}
	local count = 0
	local queue = { { instance = root, depth = 0 } }

	while #queue > 0 and count < maxNodes do
		local item = table.remove(queue, 1)
		local instance = item.instance
		local currentDepth = item.depth
		local id = getInstanceId(instance)
		local entry = {
			id = id,
			name = instance.Name,
			className = instance.ClassName,
			path = getPath(instance),
			properties = {},
		}
		for propertyName in pairs(PROPERTY_KIND) do
			local value = serializeProperty(instance, propertyName)
			if value ~= nil then
				entry.properties[propertyName] = value
			end
		end
		table.insert(nodes, entry)
		count += 1

		if currentDepth < depth then
			for _, child in ipairs(instance:GetChildren()) do
				table.insert(queue, { instance = child, depth = currentDepth + 1 })
			end
		end
	end

	return {
		rootPath = getPath(root),
		nodeCount = #nodes,
		nodes = nodes,
	}
end

local function executeCommand(command)
	local operation = command.operation
	local params = command.params or {}

	if operation == "ping" then
		return {
			ok = true,
			data = {
				sessionId = sessionId,
				placeName = getPlaceName(),
				pollingEnabled = pollingEnabled,
			},
		}
	end

	if operation == "get_hierarchy" then
		local depth = math.clamp(tonumber(params.depth) or 3, 1, 10)
		local maxNodes = math.clamp(tonumber(params.maxNodes) or 100, 1, 500)
		local root = resolveRoot(params.rootPath)
		if not root then
			return { ok = false, error = "root_not_found" }
		end
		return { ok = true, data = buildHierarchy(root, depth, maxNodes) }
	end

	if operation == "get_selection" then
		local selected = {}
		for _, instance in ipairs(Selection:Get()) do
			table.insert(selected, {
				id = getInstanceId(instance),
				name = instance.Name,
				className = instance.ClassName,
				path = getPath(instance),
			})
		end
		return { ok = true, data = { selection = selected } }
	end

	if operation == "select" then
		local ids = params.instanceIds or params.ids or {}
		local instances = {}
		for _, id in ipairs(ids) do
			local instance = resolveInstance(id)
			if instance then
				table.insert(instances, instance)
			end
		end
		Selection:Set(instances)
		return { ok = true, data = { selectedCount = #instances } }
	end

	if operation == "set_property" then
		local instanceId = params.instanceId
		local propertyName = params.property
		local value = params.value
		if BLOCKED_PROPERTIES[propertyName] then
			return { ok = false, error = "property_blocked" }
		end
		local decodedValue, decodeError = decodeTaggedValue(propertyName, value)
		if decodedValue == nil then
			return { ok = false, error = decodeError or "property_not_allowed" }
		end
		local instance = resolveInstance(instanceId)
		if not instance then
			return { ok = false, error = "instance_not_found" }
		end

		local previous = serializeProperty(instance, propertyName)

		ChangeHistoryService:SetWaypoint("DesktopUseAgent set " .. propertyName)
		local ok, err = pcall(function()
			instance[propertyName] = decodedValue
		end)
		if not ok then
			return { ok = false, error = tostring(err) }
		end
		ChangeHistoryService:SetWaypoint("DesktopUseAgent after " .. propertyName)

		local current = serializeProperty(instance, propertyName)
		return {
			ok = true,
			data = {
				instanceId = instanceId,
				property = propertyName,
				previous = previous,
				current = current,
			},
		}
	end

	return { ok = false, error = "unsupported_operation" }
end

local function registerSession()
	postJson("/v1/register", {
		sessionId = sessionId,
		studioVersion = "RobloxStudio",
		placeName = getPlaceName(),
		pollingEnabled = true,
	})
end

local function heartbeat()
	postJson("/v1/heartbeat", {
		sessionId = sessionId,
		placeName = getPlaceName(),
	})
end

local function pollOnce()
	local response = getJson("/v1/poll", "sessionId=" .. HttpService:UrlEncode(sessionId))
	if type(response) ~= "table" or response.command == nil then
		return false
	end

	local command = response.command
	table.insert(commandQueue, command)
	return true
end

local function postResult(commandId, result)
	postJson("/v1/result", {
		sessionId = sessionId,
		commandId = commandId,
		ok = result.ok,
		data = result.data,
		error = result.error,
	})
end

local function processQueuedCommands()
	while #commandQueue > 0 do
		local command = table.remove(commandQueue, 1)
		local result = executeCommand(command)
		postResult(command.id, result)
	end
end

RunService.Heartbeat:Connect(function()
	if not pollingEnabled then
		return
	end
	processQueuedCommands()
end)

task.spawn(function()
	while true do
		if pollingEnabled then
			if not config.token or config.token == "" then
				warn("[DesktopUseAgent] Missing bridge token. Run scripts/install-roblox-plugin.ps1 after starting the agent.")
				task.wait(MAX_BACKOFF)
			else
				local ok = pcall(function()
					heartbeat()
					if pollOnce() then
						backoff = POLL_INTERVAL
					else
						backoff = math.min(backoff * 2, MAX_BACKOFF)
					end
				end)
				if not ok then
					backoff = math.min(backoff * 2, MAX_BACKOFF)
				end
				task.wait(backoff)
			end
		else
			task.wait(1)
		end
	end
end)

enableButton.Click:Connect(function()
	pollingEnabled = not pollingEnabled
	enableButton:SetActive(pollingEnabled)
	if pollingEnabled then
		resetInstanceMap()
		if config.token and config.token ~= "" then
			local ok, err = pcall(registerSession)
			if not ok then
				warn("[DesktopUseAgent] Failed to register with bridge:", err)
				pollingEnabled = false
				enableButton:SetActive(false)
			end
		else
			warn("[DesktopUseAgent] Bridge token missing. Re-run scripts/install-roblox-plugin.ps1.")
			pollingEnabled = false
			enableButton:SetActive(false)
		end
	else
		pcall(function()
			postJson("/v1/register", {
				sessionId = sessionId,
				placeName = getPlaceName(),
				pollingEnabled = false,
			})
		end)
	end
end)

print("[DesktopUseAgent] Plugin loaded. Enable bridge polling from the toolbar when ready.")

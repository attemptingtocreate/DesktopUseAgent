--[[
  DesktopUseAgent Roblox Studio bridge plugin

  Requires:
  - Roblox Studio HttpService enabled (Game Settings > Security > Allow HTTP Requests)
  - DesktopUseAgent Windows agent running locally
  - Opt-in polling enabled from the plugin toolbar

  The agent hosts a loopback-only HTTP bridge. This plugin polls for commands and POSTs results.
  No arbitrary Luau execution — script edits go through get/set_script_source only.
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
	CastShadow = "bool",
	Massless = "bool",
	Transparency = "number",
	Reflectance = "number",
	Visible = "bool",
	Enabled = "bool",
	Value = "number",
	Text = "string",
	PlaceholderText = "string",
	Font = "string",
	TextSize = "number",
	BackgroundTransparency = "number",
	BorderSizePixel = "number",
	ZIndex = "number",
	LayoutOrder = "number",
	Rotation = "number",
	Material = "string",
	BrickColor = "string",
	Shape = "string",
	Color = "Color3",
	TextColor3 = "Color3",
	BackgroundColor3 = "Color3",
	Size = "Vector3OrUDim2",
	Position = "Vector3OrUDim2",
	Orientation = "Vector3",
}

local BLOCKED_PROPERTIES = {
	Parent = true,
	Source = true,
	Archivable = true,
}

local CREATABLE_CLASSES = {
	Part = true, MeshPart = true, SpawnLocation = true, Model = true, Folder = true, Configuration = true,
	Script = true, LocalScript = true, ModuleScript = true,
	StringValue = true, IntValue = true, NumberValue = true, BoolValue = true, ObjectValue = true,
	RemoteEvent = true, RemoteFunction = true, BindableEvent = true, BindableFunction = true,
	ScreenGui = true, Frame = true, TextLabel = true, TextButton = true, TextBox = true,
	ImageLabel = true, ImageButton = true, ScrollingFrame = true,
	UIListLayout = true, UIPadding = true, UICorner = true, UIStroke = true,
	BillboardGui = true, SurfaceGui = true,
	Attachment = true, WeldConstraint = true, ProximityPrompt = true, ClickDetector = true,
	Sound = true, Animation = true, Humanoid = true, Accoutrement = true, Tool = true, Seat = true, VehicleSeat = true,
}

local SCRIPT_CLASSES = {
	Script = true,
	LocalScript = true,
	ModuleScript = true,
}

local FORBIDDEN_PARENT_SERVICES = {
	CoreGui = true,
	RobloxPluginGuiService = true,
	PluginGuiService = true,
	CorePackages = true,
}

local MAX_SCRIPT_SOURCE_BYTES = 262144
local MAX_BATCH_OPS = 32
local MAX_FIND_RESULTS = 200

local function isFiniteNumber(value)
	return type(value) == "number" and value == value and value ~= math.huge and value ~= -math.huge
end

local function serializeTaggedColor3(value)
	return { type = "Color3", r = value.R, g = value.G, b = value.B }
end

local function serializeTaggedVector3(value)
	return { type = "Vector3", x = value.X, y = value.Y, z = value.Z }
end

local function serializeTaggedUDim2(value)
	return {
		type = "UDim2",
		xScale = value.X.Scale,
		xOffset = value.X.Offset,
		yScale = value.Y.Scale,
		yOffset = value.Y.Offset,
	}
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
	if kind == "string" then
		if type(value) == "string" then
			return value
		end
		if typeof(value) == "EnumItem" then
			return value.Name
		end
		if typeof(value) == "BrickColor" then
			return value.Name
		end
		return nil
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
	if kind == "Vector3OrUDim2" then
		if typeof(value) == "Vector3" then
			return serializeTaggedVector3(value)
		end
		if typeof(value) == "UDim2" then
			return serializeTaggedUDim2(value)
		end
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
	if kind == "Vector3OrUDim2" then
		if type(value) ~= "table" then
			return nil, "invalid_size_position"
		end
		if value.type == "Vector3" then
			if not isFiniteNumber(value.x) or not isFiniteNumber(value.y) or not isFiniteNumber(value.z) then
				return nil, "invalid_vector3"
			end
			return Vector3.new(value.x, value.y, value.z)
		end
		if value.type == "UDim2" then
			if not isFiniteNumber(value.xScale) or not isFiniteNumber(value.xOffset)
				or not isFiniteNumber(value.yScale) or not isFiniteNumber(value.yOffset) then
				return nil, "invalid_udim2"
			end
			return UDim2.new(value.xScale, value.xOffset, value.yScale, value.yOffset)
		end
		return nil, "invalid_size_position"
	end
	return nil, "unsupported_kind"
end

local function applyStringEnumProperty(instance, propertyName, stringValue)
	if propertyName == "Material" then
		local enumItem = Enum.Material[stringValue]
		if enumItem == nil then
			return false, "invalid_material"
		end
		instance.Material = enumItem
		return true
	end
	if propertyName == "Shape" then
		local enumItem = Enum.PartType[stringValue]
		if enumItem == nil then
			return false, "invalid_shape"
		end
		instance.Shape = enumItem
		return true
	end
	if propertyName == "BrickColor" then
		local ok, brick = pcall(function()
			return BrickColor.new(stringValue)
		end)
		if not ok then
			return false, "invalid_brickcolor"
		end
		instance.BrickColor = brick
		return true
	end
	if propertyName == "Font" then
		local enumItem = Enum.Font[stringValue]
		if enumItem ~= nil then
			instance.Font = enumItem
			return true
		end
		instance.Font = stringValue
		return true
	end
	instance[propertyName] = stringValue
	return true
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

local function isForbiddenParent(instance)
	local current = instance
	while current do
		if FORBIDDEN_PARENT_SERVICES[current.Name] or FORBIDDEN_PARENT_SERVICES[current.ClassName] then
			return true
		end
		if current == game then
			break
		end
		current = current.Parent
	end
	return false
end

local function resolveParent(params)
	if type(params.parentId) == "string" and params.parentId ~= "" then
		local parent = resolveInstance(params.parentId)
		if not parent then
			return nil, "parent_not_found"
		end
		return parent
	end
	if type(params.parentPath) == "string" and params.parentPath ~= "" then
		local parent = resolveRoot(params.parentPath)
		if not parent then
			return nil, "parent_not_found"
		end
		return parent
	end
	return nil, "parent_required"
end

local function describeInstance(instance)
	return {
		id = getInstanceId(instance),
		name = instance.Name,
		className = instance.ClassName,
		path = getPath(instance),
	}
end

local function utf8ByteLength(text)
	return #text
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

local executeCommand

local function executeSingle(operation, params)
	params = params or {}

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
			table.insert(selected, describeInstance(instance))
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
			local kind = PROPERTY_KIND[propertyName]
			if kind == "string" and (propertyName == "Material" or propertyName == "Shape" or propertyName == "BrickColor" or propertyName == "Font") then
				local applied, applyErr = applyStringEnumProperty(instance, propertyName, decodedValue)
				if not applied then
					error(applyErr or "apply_failed")
				end
			else
				instance[propertyName] = decodedValue
			end
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

	if operation == "create_instance" then
		local className = params.className
		if type(className) ~= "string" or not CREATABLE_CLASSES[className] then
			return { ok = false, error = "class_not_allowed" }
		end
		local parent, parentErr = resolveParent(params)
		if not parent then
			return { ok = false, error = parentErr }
		end
		if isForbiddenParent(parent) then
			return { ok = false, error = "forbidden_parent" }
		end
		ChangeHistoryService:SetWaypoint("DesktopUseAgent create " .. className)
		local ok, createdOrErr = pcall(function()
			local created = Instance.new(className)
			if type(params.name) == "string" and params.name ~= "" then
				created.Name = params.name
			end
			created.Parent = parent
			return created
		end)
		if not ok then
			return { ok = false, error = tostring(createdOrErr) }
		end
		ChangeHistoryService:SetWaypoint("DesktopUseAgent after create")
		return { ok = true, data = describeInstance(createdOrErr) }
	end

	if operation == "destroy_instance" then
		local instance = resolveInstance(params.instanceId)
		if not instance then
			return { ok = false, error = "instance_not_found" }
		end
		if instance == game or instance.Parent == nil and instance ~= game then
			return { ok = false, error = "cannot_destroy" }
		end
		if instance:IsA("DataModel") or instance.Parent == game and instance:IsA("ServiceProvider") then
			return { ok = false, error = "cannot_destroy_service" }
		end
		-- Disallow destroying top-level services
		if instance.Parent == game then
			return { ok = false, error = "cannot_destroy_service" }
		end
		local snapshot = describeInstance(instance)
		ChangeHistoryService:SetWaypoint("DesktopUseAgent destroy")
		instance:Destroy()
		instanceMap[instance] = nil
		ChangeHistoryService:SetWaypoint("DesktopUseAgent after destroy")
		return { ok = true, data = snapshot }
	end

	if operation == "clone_instance" then
		local instance = resolveInstance(params.instanceId)
		if not instance then
			return { ok = false, error = "instance_not_found" }
		end
		local parent = instance.Parent
		if type(params.parentId) == "string" or type(params.parentPath) == "string" then
			local resolved, parentErr = resolveParent(params)
			if not resolved then
				return { ok = false, error = parentErr }
			end
			parent = resolved
		end
		if not parent then
			return { ok = false, error = "parent_required" }
		end
		if isForbiddenParent(parent) then
			return { ok = false, error = "forbidden_parent" }
		end
		ChangeHistoryService:SetWaypoint("DesktopUseAgent clone")
		local ok, cloneOrErr = pcall(function()
			local clone = instance:Clone()
			if type(params.name) == "string" and params.name ~= "" then
				clone.Name = params.name
			end
			clone.Parent = parent
			return clone
		end)
		if not ok then
			return { ok = false, error = tostring(cloneOrErr) }
		end
		ChangeHistoryService:SetWaypoint("DesktopUseAgent after clone")
		return { ok = true, data = describeInstance(cloneOrErr) }
	end

	if operation == "set_parent" then
		local instance = resolveInstance(params.instanceId)
		if not instance then
			return { ok = false, error = "instance_not_found" }
		end
		local parent, parentErr = resolveParent(params)
		if not parent then
			return { ok = false, error = parentErr }
		end
		if isForbiddenParent(parent) then
			return { ok = false, error = "forbidden_parent" }
		end
		ChangeHistoryService:SetWaypoint("DesktopUseAgent set_parent")
		local ok, err = pcall(function()
			instance.Parent = parent
		end)
		if not ok then
			return { ok = false, error = tostring(err) }
		end
		ChangeHistoryService:SetWaypoint("DesktopUseAgent after set_parent")
		return { ok = true, data = describeInstance(instance) }
	end

	if operation == "find_instances" then
		local maxResults = math.clamp(tonumber(params.maxResults) or 50, 1, MAX_FIND_RESULTS)
		local className = params.className
		local nameContains = params.nameContains
		local pathPrefix = params.pathPrefix
		local root = resolveRoot(params.rootPath) or game
		local matches = {}
		local function visit(node)
			if #matches >= maxResults then
				return
			end
			local include = true
			if type(className) == "string" and className ~= "" then
				include = node.ClassName == className or node:IsA(className)
			end
			if include and type(nameContains) == "string" and nameContains ~= "" then
				include = string.find(string.lower(node.Name), string.lower(nameContains), 1, true) ~= nil
			end
			if include and type(pathPrefix) == "string" and pathPrefix ~= "" then
				local path = getPath(node)
				include = string.sub(path, 1, #pathPrefix) == pathPrefix
			end
			if include and node ~= game then
				table.insert(matches, describeInstance(node))
			end
			for _, child in ipairs(node:GetChildren()) do
				if #matches >= maxResults then
					break
				end
				visit(child)
			end
		end
		visit(root)
		return { ok = true, data = { count = #matches, instances = matches } }
	end

	if operation == "get_script_source" then
		local instance = resolveInstance(params.instanceId)
		if not instance then
			return { ok = false, error = "instance_not_found" }
		end
		if not SCRIPT_CLASSES[instance.ClassName] then
			return { ok = false, error = "not_a_script" }
		end
		local ok, source = pcall(function()
			return instance.Source
		end)
		if not ok then
			return { ok = false, error = tostring(source) }
		end
		return {
			ok = true,
			data = {
				instanceId = params.instanceId,
				className = instance.ClassName,
				byteLength = utf8ByteLength(source),
				source = source,
			},
		}
	end

	if operation == "set_script_source" then
		local instance = resolveInstance(params.instanceId)
		if not instance then
			return { ok = false, error = "instance_not_found" }
		end
		if not SCRIPT_CLASSES[instance.ClassName] then
			return { ok = false, error = "not_a_script" }
		end
		if type(params.source) ~= "string" then
			return { ok = false, error = "source_required" }
		end
		if utf8ByteLength(params.source) > MAX_SCRIPT_SOURCE_BYTES then
			return { ok = false, error = "source_too_large" }
		end
		ChangeHistoryService:SetWaypoint("DesktopUseAgent set_script_source")
		local ok, err = pcall(function()
			instance.Source = params.source
		end)
		if not ok then
			return { ok = false, error = tostring(err) }
		end
		ChangeHistoryService:SetWaypoint("DesktopUseAgent after set_script_source")
		return {
			ok = true,
			data = {
				instanceId = params.instanceId,
				className = instance.ClassName,
				byteLength = utf8ByteLength(params.source),
			},
		}
	end

	if operation == "playtest_start" then
		local ok, err = pcall(function()
			plugin:StartPlaySolo()
		end)
		if not ok then
			return { ok = false, error = tostring(err) }
		end
		return { ok = true, data = { running = true } }
	end

	if operation == "playtest_stop" then
		local ok, err = pcall(function()
			plugin:StopPlaySolo()
		end)
		if not ok then
			return { ok = false, error = tostring(err) }
		end
		return { ok = true, data = { running = false } }
	end

	if operation == "batch" then
		local ops = params.operations or params.ops or {}
		if type(ops) ~= "table" then
			return { ok = false, error = "operations_required" }
		end
		if #ops > MAX_BATCH_OPS then
			return { ok = false, error = "too_many_operations" }
		end
		local results = {}
		for index, item in ipairs(ops) do
			local opName = item.operation or item.op
			local opParams = item.params or {}
			if type(opName) ~= "string" or opName == "batch" then
				table.insert(results, { ok = false, error = "invalid_batch_operation", index = index })
			else
				local result = executeSingle(opName, opParams)
				result.index = index
				result.operation = opName
				table.insert(results, result)
				if result.ok ~= true and params.stopOnError == true then
					break
				end
			end
		end
		return { ok = true, data = { results = results } }
	end

	return { ok = false, error = "unsupported_operation" }
end

executeCommand = function(command)
	return executeSingle(command.operation, command.params or {})
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

-- TESTING: upright Z-handle, inward upgrades, ore manage UI, paid rolls
local Players = game:GetService("Players")
local DataStoreService = game:GetService("DataStoreService")
local Lighting = game:GetService("Lighting")
local SS = game:GetService("ServerStorage")
local RS = game:GetService("ReplicatedStorage")
local TweenService = game:GetService("TweenService")
local RunService = game:GetService("RunService")

local Templates = SS:WaitForChild("Templates")
local PickaxeTemplate = Templates:WaitForChild("PickaxeTemplate")
local Plots = workspace:WaitForChild("Plots")

local Remotes = RS:FindFirstChild("Remotes")
if not Remotes then
	Remotes = Instance.new("Folder")
	Remotes.Name = "Remotes"
	Remotes.Parent = RS
end
local function ensureRemote(name, className)
	local r = Remotes:FindFirstChild(name)
	if not r then
		r = Instance.new(className or "RemoteEvent")
		r.Name = name
		r.Parent = Remotes
	end
	return r
end
local RequestAttach = ensureRemote("RequestAttach")
local RemoveOrePickaxe = ensureRemote("RemoveOrePickaxe")
local BuyRolledPickaxe = ensureRemote("BuyRolledPickaxe")
local OrePickaxesUpdate = ensureRemote("OrePickaxesUpdate")
local GetOrePickaxes = ensureRemote("GetOrePickaxes", "RemoteFunction")

local RARITIES = {
	{ Name = "Common", Weight = 50, Color = Color3.fromRGB(200, 190, 175), Mult = 1 },
	{ Name = "Uncommon", Weight = 25, Color = Color3.fromRGB(80, 200, 100), Mult = 1.4 },
	{ Name = "Rare", Weight = 14, Color = Color3.fromRGB(70, 140, 255), Mult = 2.0 },
	{ Name = "Epic", Weight = 8, Color = Color3.fromRGB(180, 80, 255), Mult = 3.2 },
	{ Name = "Legendary", Weight = 3, Color = Color3.fromRGB(255, 170, 40), Mult = 5.5 },
}

local BUY_COST = {
	Common = 25,
	Uncommon = 75,
	Rare = 200,
	Epic = 550,
	Legendary = 1500,
}

local BASE_INCOME = 2
local TICK = 1
local ROLL_COOLDOWN = 2.5
local SAVE_INTERVAL = 45
local MAX_LUCK = 10
local MAX_ORE = 10
local MAX_ACTIVE = 5
local MAX_ROLLS = 2 -- unlocks 2nd then 3rd spawner
local BURY = Vector3.new(0, -80, 0)
local SELL_RATE = 1 -- 1 Ore -> $1 Cash
local REFINE_RATE = 1 -- 1 Ore -> 1 Refined Ore per tick while standing
local PAD_TICK = 0.5

local LUCK_COST = {200,500,1200,2800,6000,13000,28000,60000,125000,260000}
local ORE_COST = {250,650,1500,3500,8000,18000,40000,85000,180000,380000}
local ACTIVE_COST = {500,2000,7500,25000} -- buys capacity 2..5
local ROLL_COST = {1200,5000} -- unlocks roll slot 2 then 3

local plotOwners, playerPlot, lastRoll, mining, dataCache = {}, {}, {}, {}, {}
local idlePickAnims = {}

local store
do
	local ok, ds = pcall(function()
		return DataStoreService:GetDataStore("TestingPickaxe_v2")
	end)
	if ok then store = ds end
end

-- Visual handle is along local +Z. Upright = +Z world up (head up, handle tip down).
local function worldUpright(pos, yaw)
	return CFrame.new(pos) * CFrame.Angles(0, yaw or 0, 0) * CFrame.Angles(math.rad(-90), 0, 0)
end

local function ensureLighting()
	Lighting.ClockTime = 16.8
	Lighting.Brightness = 1.15
	Lighting.Ambient = Color3.fromRGB(95, 78, 70)
	local mb = Lighting:FindFirstChild("MagmaBloom")
	if mb then mb.Enabled = false end
	local bloom = Lighting:FindFirstChild("Bloom")
	if bloom then
		bloom.Intensity = 0.12
		bloom.Threshold = 1.15
	end
end

local function plotPad(plot)
	return plot:FindFirstChild("Pad")
end

local function getLevels(plot)
	local luck = math.clamp(tonumber(plot:GetAttribute("LuckLevel")) or 0, 0, MAX_LUCK)
	local ore = math.clamp(tonumber(plot:GetAttribute("OreLevel")) or 0, 0, MAX_ORE)
	local active = math.clamp(tonumber(plot:GetAttribute("ActiveLevel")) or 0, 0, MAX_ACTIVE - 1)
	local rolls = math.clamp(tonumber(plot:GetAttribute("RollsLevel")) or 0, 0, MAX_ROLLS)
	return luck, ore, active, rolls
end

local function pickaxeCapacity(plot)
	local _, _, active = getLevels(plot)
	return 1 + active
end

local function luckMult(level)
	return 1 + 0.25 * (level or 0)
end

local function incomePerPick(rarityMult, oreLevel)
	-- Clean steps: ore 0=>$2, ore 1=>$4, ore 2=>$6... for Common
	return math.max(1, math.floor(BASE_INCOME * (rarityMult or 1) * (1 + (oreLevel or 0))))
end

local function buyPrice(rarityName, freeUsed)
	if not freeUsed then return 0 end
	return BUY_COST[rarityName] or 25
end

local function applyStarterRollSlots(plot)
	local _, _, _, rolls = getLevels(plot)
	local slots = math.clamp((plot:GetAttribute("UnlockedRollSlots") or (1 + rolls)), 1, 3)
	plot:SetAttribute("UnlockedRollSlots", slots)
	plot:SetAttribute("RollsLevel", math.clamp(slots - 1, 0, MAX_ROLLS))

	local function isUnderground(cfY)
		return cfY < -40
	end

	local function markOrBuryModel(m)
		if not m then return end
		if m:GetAttribute("Buried") then return end
		m:SetAttribute("Buried", true)
		if not isUnderground(m:GetPivot().Position.Y) then
			m:PivotTo(m:GetPivot() + BURY)
		end
		for _, d in ipairs(m:GetDescendants()) do
			if d:IsA("BasePart") then d.CanCollide = false end
		end
	end

	local function markOrBuryPart(p)
		if not p then return end
		if p:GetAttribute("Buried") then return end
		p:SetAttribute("Buried", true)
		if not isUnderground(p.Position.Y) then
			p.CFrame = p.CFrame + BURY
		end
		p.CanCollide = false
	end

	local function snapRaisedModel(m)
		if not m then return end
		local cf = m:GetPivot()
		while isUnderground(cf.Position.Y) do
			cf = cf - BURY
		end
		m:SetAttribute("Buried", nil)
		m:PivotTo(cf)
		for _, d in ipairs(m:GetDescendants()) do
			if d:IsA("BasePart") then d.CanCollide = true end
		end
	end

	local function snapRaisedPart(p)
		if not p then return end
		local cf = p.CFrame
		while isUnderground(cf.Position.Y) do
			cf = cf - BURY
		end
		p:SetAttribute("Buried", nil)
		p.CFrame = cf
		p.CanCollide = false
	end

	-- Pair spawn models with nearest spawn points (template names are swapped on Z)
	local function nearestSpawnPoint(spawnModel)
		local rs = plot:FindFirstChild("RollStation")
		if not rs or not spawnModel then return nil end
		local pivot = spawnModel:GetPivot().Position
		local best, bestDist = nil, math.huge
		for _, name in ipairs({ "SecondSpawnPoint", "ThirdSpawnPoint" }) do
			local pt = rs:FindFirstChild(name)
			if pt then
				local d = (Vector3.new(pt.Position.X, 0, pt.Position.Z) - Vector3.new(pivot.X, 0, pivot.Z)).Magnitude
				if d < bestDist then
					bestDist = d
					best = pt
				end
			end
		end
		return best
	end

	local second = plot:FindFirstChild("SecondSpawn")
	local third = plot:FindFirstChild("ThirdSpawn")
	local secondPt = nearestSpawnPoint(second)
	local thirdPt = nearestSpawnPoint(third)

	if slots < 2 then
		markOrBuryModel(second)
		markOrBuryPart(secondPt)
	else
		snapRaisedModel(second)
		snapRaisedPart(secondPt)
	end
	if slots < 3 then
		markOrBuryModel(third)
		markOrBuryPart(thirdPt)
	else
		snapRaisedModel(third)
		snapRaisedPart(thirdPt)
	end
end

local function raiseBuriedModel(m, duration)
	if not m or not m:GetAttribute("Buried") then return end
	m:SetAttribute("Buried", nil)
	local startCF = m:GetPivot()
	local goalCF = startCF
	while goalCF.Position.Y < -40 do
		goalCF = goalCF - BURY
	end
	local n = Instance.new("NumberValue")
	n.Value = 0
	local tw = TweenService:Create(n, TweenInfo.new(duration or 4, Enum.EasingStyle.Quad, Enum.EasingDirection.Out), { Value = 1 })
	local conn = n:GetPropertyChangedSignal("Value"):Connect(function()
		if m.Parent then m:PivotTo(startCF:Lerp(goalCF, n.Value)) end
	end)
	tw:Play()
	tw.Completed:Connect(function()
		conn:Disconnect()
		n:Destroy()
		if m.Parent then
			m:PivotTo(goalCF)
			for _, d in ipairs(m:GetDescendants()) do
				if d:IsA("BasePart") then d.CanCollide = true end
			end
		end
	end)
end

local function raiseBuriedPart(p, duration)
	if not p or not p:GetAttribute("Buried") then return end
	p:SetAttribute("Buried", nil)
	local startCF = p.CFrame
	local goalCF = startCF
	while goalCF.Position.Y < -40 do
		goalCF = goalCF - BURY
	end
	local tw = TweenService:Create(p, TweenInfo.new(duration or 4, Enum.EasingStyle.Quad, Enum.EasingDirection.Out), { CFrame = goalCF })
	tw:Play()
	tw.Completed:Connect(function()
		if p.Parent then p.CanCollide = false end -- spawn points stay non-collide
	end)
end

local function nearestSpawnPoint(plot, spawnModel)
	local rs = plot:FindFirstChild("RollStation")
	if not rs or not spawnModel then return nil end
	local pivot = spawnModel:GetPivot().Position
	local best, bestDist = nil, math.huge
	for _, name in ipairs({ "SecondSpawnPoint", "ThirdSpawnPoint" }) do
		local pt = rs:FindFirstChild(name)
		if pt then
			local d = (Vector3.new(pt.Position.X, 0, pt.Position.Z) - Vector3.new(pivot.X, 0, pivot.Z)).Magnitude
			if d < bestDist then
				bestDist = d
				best = pt
			end
		end
	end
	return best
end

local function unlockNextRollSlot(plot)
	local slots = plot:GetAttribute("UnlockedRollSlots") or 1
	if slots >= 3 then return end
	slots += 1
	plot:SetAttribute("UnlockedRollSlots", slots)
	if slots == 2 then
		local m = plot:FindFirstChild("SecondSpawn")
		raiseBuriedModel(m, 4.5)
		raiseBuriedPart(nearestSpawnPoint(plot, m), 4.5)
	elseif slots == 3 then
		local m = plot:FindFirstChild("ThirdSpawn")
		raiseBuriedModel(m, 4.5)
		raiseBuriedPart(nearestSpawnPoint(plot, m), 4.5)
	end
end

local function getActiveSpawnPoints(plot)
	local rs = plot:FindFirstChild("RollStation")
	if not rs then return {} end
	local slots = plot:GetAttribute("UnlockedRollSlots") or 1
	local pts = {}
	local main = rs:FindFirstChild("MainSpawnPoint") or rs:FindFirstChild("SpawnPoint")
	if main then table.insert(pts, main) end
	-- Prefer nearest-to-model pairing so misnamed points still work
	if slots >= 2 then
		local p = nearestSpawnPoint(plot, plot:FindFirstChild("SecondSpawn"))
		if p and not p:GetAttribute("Buried") then table.insert(pts, p) end
	end
	if slots >= 3 then
		local p = nearestSpawnPoint(plot, plot:FindFirstChild("ThirdSpawn"))
		if p and not p:GetAttribute("Buried") then table.insert(pts, p) end
	end
	return pts
end

local function setPlotLabelText(plot, text)
	local pad = plotPad(plot)
	if not pad then return end
	local gui = pad:FindFirstChild("PlotLabel")
	if not gui then
		gui = Instance.new("BillboardGui")
		gui.Name = "PlotLabel"
		gui.Parent = pad
	end
	gui.Size = UDim2.fromOffset(280, 56)
	gui.StudsOffset = Vector3.new(0, 14, 0)
	gui.MaxDistance = 140
	gui.AlwaysOnTop = false
	local label = gui:FindFirstChildOfClass("TextLabel")
	if not label then
		label = Instance.new("TextLabel")
		label.Size = UDim2.fromScale(1, 1)
		label.BackgroundTransparency = 1
		label.TextColor3 = Color3.new(1, 1, 1)
		label.Font = Enum.Font.GothamBold
		label.TextScaled = true
		label.Parent = gui
	end
	label.TextStrokeTransparency = 0.35
	label.Text = text or "Unclaimed Refinery"
end

local function raisePlotLabel(plot)
	setPlotLabelText(plot, "Unclaimed Refinery")
end

local function ensureOreIncomeGui(oreModel)
	local part = oreModel.PrimaryPart or oreModel:FindFirstChildWhichIsA("BasePart", true)
	if not part then return end
	local gui = part:FindFirstChild("OreIncomeGui")
	if not gui then
		gui = Instance.new("BillboardGui")
		gui.Name = "OreIncomeGui"
		gui.Size = UDim2.fromOffset(200, 40)
		gui.StudsOffset = Vector3.new(0, 3.4, 0)
		gui.MaxDistance = 80
		gui.Parent = part
		local t = Instance.new("TextLabel")
		t.Name = "Amount"
		t.Size = UDim2.fromScale(1, 1)
		t.BackgroundTransparency = 1
		t.Font = Enum.Font.GothamBold
		t.TextScaled = true
		t.TextColor3 = Color3.fromRGB(255, 220, 120)
		t.TextStrokeTransparency = 0.35
		t.Text = "0 Ore/s"
		t.Parent = gui
	end
	return gui:FindFirstChild("Amount")
end

local function setOreIncomeText(plot, perSec)
	local ore = plot:FindFirstChild("Ore")
	if not ore then return end
	local label = ensureOreIncomeGui(ore)
	if label then label.Text = tostring(perSec) .. " Ore/s" end
end

local function destroyUpgradeBoard(plot)
	local b = plot:FindFirstChild("UpgradeBoard")
	if b then b:Destroy() end
end

local function makeUpgradeBoard(plot)
	destroyUpgradeBoard(plot)
	local Templates = SS:FindFirstChild("Templates")
	local tmpl = Templates and Templates:FindFirstChild("UpgradeSign")
	if tmpl then
		local board = tmpl:Clone()
		board.Name = "UpgradeBoard"
		board.Parent = plot
		local pad = plotPad(plot)
		if pad then
			local ore = plot:FindFirstChild("Ore")
			local orePart = ore and (ore.PrimaryPart or ore:FindFirstChildWhichIsA("BasePart", true))
			local orePos = orePart and orePart.Position or pad.Position
			local right = pad.CFrame.RightVector
			local front = pad.CFrame.LookVector
			local pos = pad.Position + right * 10 + front * 2 + Vector3.new(0, 0.2, 0)
			local face = CFrame.lookAt(pos + Vector3.new(0, 3, 0), Vector3.new(orePos.X, 3, orePos.Z))
			board:PivotTo(face)
		end
		-- Ensure no Ore slot remains
		local oreSlot = board:FindFirstChild("OreSlot")
		if oreSlot then oreSlot:Destroy() end
		return board
	end
	-- Fallback standing sign
	local pad = plotPad(plot)
	if not pad then return end
	local board = Instance.new("Model")
	board.Name = "UpgradeBoard"
	board.Parent = plot
	return board
end

local function layoutSlotFrame(frame)
	local title = frame:FindFirstChild("Title")
	local lvl = frame:FindFirstChild("Level")
	local cost = frame:FindFirstChild("Cost")
	local prog = frame:FindFirstChild("Progress")
	if prog then prog:Destroy() end
	if title then
		title.Size = UDim2.new(1, -10, 0.28, 0)
		title.Position = UDim2.new(0, 5, 0.04, 0)
		title.ZIndex = 2
	end
	if lvl then
		lvl.Size = UDim2.new(1, -10, 0.32, 0)
		lvl.Position = UDim2.new(0, 5, 0.34, 0)
		lvl.TextColor3 = Color3.fromRGB(180, 210, 255)
		lvl.ZIndex = 3
	end
	if cost then
		cost.Size = UDim2.new(1, -10, 0.26, 0)
		cost.Position = UDim2.new(0, 5, 0.70, 0)
		cost.ZIndex = 3
	end
end

local function findUpgradeFrame(board, slotName, frameName)
	local slot = board:FindFirstChild(slotName)
	if slot then
		local gui = slot:FindFirstChildOfClass("SurfaceGui")
		local frame = gui and gui:FindFirstChildOfClass("Frame")
		if frame then return frame, slot end
	end
	local boardPart = board:FindFirstChild("Board")
	local titleGui = boardPart and boardPart:FindFirstChild("TitleGui")
	local frame = titleGui and titleGui:FindFirstChild(frameName)
	if frame then return frame, board:FindFirstChild(slotName) end
	return nil, nil
end

local function refreshUpgradeUI(plot)
	local luck, ore, active, rolls = getLevels(plot)
	local board = plot:FindFirstChild("UpgradeBoard")
	if not board then return end
	-- Remove legacy Ore UI
	local oreSlot = board:FindFirstChild("OreSlot")
	if oreSlot then oreSlot:Destroy() end
	local boardPart = board:FindFirstChild("Board")
	local titleGui = boardPart and boardPart:FindFirstChild("TitleGui")
	if titleGui and titleGui:FindFirstChild("Ore") then titleGui.Ore:Destroy() end

	local function upd(slotName, frameName, titleText, level, maxPurchases, costs, progressText, atMax)
		local frame = select(1, findUpgradeFrame(board, slotName, frameName))
		if not frame then return end
		layoutSlotFrame(frame)
		local title = frame:FindFirstChild("Title")
		if title and titleText then title.Text = titleText end
		local lvl = frame:FindFirstChild("Level")
		local cost = frame:FindFirstChild("Cost")
		if lvl then
			lvl.Text = "Lv " .. tostring(level) .. "/" .. tostring(maxPurchases) .. "\n" .. (progressText or "")
		end
		if cost then
			if atMax or level >= maxPurchases or level >= #costs then
				cost.Text = "MAX"
			else
				cost.Text = "$" .. tostring(costs[level + 1])
			end
		end
	end

	local luckProg
	if luck >= MAX_LUCK then
		luckProg = string.format("%gx", luckMult(luck))
	else
		luckProg = string.format("%gx > %gx", luckMult(luck), luckMult(luck + 1))
	end
	upd("LuckSlot", "Luck", "Luck", luck, MAX_LUCK, LUCK_COST, luckProg, luck >= MAX_LUCK)

	local cap = 1 + active
	local pickProg = if cap >= MAX_ACTIVE then tostring(cap) else (tostring(cap) .. " > " .. tostring(cap + 1))
	upd("PickaxesSlot", "Pickaxes", "Pickaxes", active, MAX_ACTIVE - 1, ACTIVE_COST, pickProg, cap >= MAX_ACTIVE)

	local spawners = 1 + rolls
	local rollProg = if rolls >= MAX_ROLLS then tostring(spawners) else (tostring(spawners) .. " > " .. tostring(spawners + 1))
	upd("RollsSlot", "Rolls", "Rolls", rolls, MAX_ROLLS, ROLL_COST, rollProg, rolls >= MAX_ROLLS)
end

local function rollRarity(luckLevel)
	local boost = (luckLevel or 0) * 0.1
	local weights = {}
	for i, r in ipairs(RARITIES) do
		local w = r.Weight
		if i > 1 then w = w * (1 + boost * (i - 1)) end
		if i == 1 then w = math.max(8, w * (1 - boost * 0.75)) end
		weights[i] = w
	end
	local total = 0
	for _, w in ipairs(weights) do total += w end
	local n = math.random() * total
	local acc = 0
	for i, r in ipairs(RARITIES) do
		acc += weights[i]
		if n <= acc then return r end
	end
	return RARITIES[1]
end

local function ensureLeaderstats(player)
	local ls = player:FindFirstChild("leaderstats")
	if not ls then
		ls = Instance.new("Folder")
		ls.Name = "leaderstats"
		ls.Parent = player
	end
	-- Public board: Cash only
	local cash = ls:FindFirstChild("Cash")
	if not cash then
		cash = Instance.new("IntValue")
		cash.Name = "Cash"
		cash.Value = 0
		cash.Parent = ls
	end
	for _, bad in ipairs({ "Ore", "RefinedOre", "Refined Ore" }) do
		local v = ls:FindFirstChild(bad)
		if v then v:Destroy() end
	end
	return cash
end

local function ensureCurrencies(player)
	local cash = ensureLeaderstats(player)
	local bag = player:FindFirstChild("Currencies")
	if not bag then
		bag = Instance.new("Folder")
		bag.Name = "Currencies"
		bag.Parent = player
	end
	local function iv(name)
		local v = bag:FindFirstChild(name)
		if not v then
			v = Instance.new("IntValue")
			v.Name = name
			v.Value = 0
			v.Parent = bag
		end
		return v
	end
	return cash, iv("Ore"), iv("RefinedOre")
end

local function loadData(player)
	local cash, oreV, refinedV = ensureCurrencies(player)
	local data = { Cash = 0, Ore = 0, RefinedOre = 0, LuckLevel = 0, OreLevel = 0, ActiveLevel = 0, RollsLevel = 0, FreeBuyUsed = false }
	if store then
		local ok, result = pcall(function()
			return store:GetAsync("u_" .. player.UserId)
		end)
		if ok and typeof(result) == "table" then
			data.Cash = math.max(0, tonumber(result.Cash) or 0)
			data.Ore = math.max(0, tonumber(result.Ore) or 0)
			data.RefinedOre = math.max(0, tonumber(result.RefinedOre) or 0)
			data.Rarity = result.Rarity
			data.Mult = result.Mult
			data.ColorR = result.ColorR
			data.ColorG = result.ColorG
			data.ColorB = result.ColorB
			data.LuckLevel = math.clamp(tonumber(result.LuckLevel) or 0, 0, MAX_LUCK)
			data.OreLevel = math.clamp(tonumber(result.OreLevel) or 0, 0, MAX_ORE)
			data.ActiveLevel = math.clamp(tonumber(result.ActiveLevel) or 0, 0, MAX_ACTIVE - 1)
			data.RollsLevel = math.clamp(tonumber(result.RollsLevel) or 0, 0, MAX_ROLLS)
			data.FreeBuyUsed = result.FreeBuyUsed == true
		end
	end
	cash.Value = data.Cash
	oreV.Value = data.Ore
	refinedV.Value = data.RefinedOre
	dataCache[player.UserId] = data
	return data
end

local function saveData(player)
	local cash, oreV, refinedV = ensureCurrencies(player)
	local data = dataCache[player.UserId] or {}
	data.Cash = cash.Value
	data.Ore = oreV.Value
	data.RefinedOre = refinedV.Value
	local id = playerPlot[player.UserId]
	local plot = id and Plots:FindFirstChild("Plot" .. id)
	if plot then
		data.LuckLevel = plot:GetAttribute("LuckLevel") or 0
		data.OreLevel = plot:GetAttribute("OreLevel") or 0
		data.ActiveLevel = plot:GetAttribute("ActiveLevel") or 0
		data.RollsLevel = plot:GetAttribute("RollsLevel") or 0
	end
	local char = player.Character
	local tool = char and char:FindFirstChildOfClass("Tool")
	if tool and tool:GetAttribute("IsPickaxe") then
		data.Rarity = tool:GetAttribute("Rarity")
		data.Mult = tool:GetAttribute("Mult")
		local h = tool:FindFirstChild("Handle")
		if h then
			data.ColorR, data.ColorG, data.ColorB = h.Color.R, h.Color.G, h.Color.B
		end
	end
	dataCache[player.UserId] = data
	if store then
		pcall(function()
			store:SetAsync("u_" .. player.UserId, data)
		end)
	end
end

local function claimPlot(player)
	if playerPlot[player.UserId] then return playerPlot[player.UserId] end
	for i = 1, 8 do
		if not plotOwners[i] then
			plotOwners[i] = player.UserId
			playerPlot[player.UserId] = i
			local plot = Plots:FindFirstChild("Plot" .. i)
			local data = dataCache[player.UserId] or {}
			if plot then
				plot:SetAttribute("LuckLevel", data.LuckLevel or 0)
				plot:SetAttribute("OreLevel", data.OreLevel or 0)
				plot:SetAttribute("ActiveLevel", data.ActiveLevel or 0)
				plot:SetAttribute("RollsLevel", data.RollsLevel or 0)
				plot:SetAttribute("UnlockedRollSlots", 1 + (data.RollsLevel or 0))
				applyStarterRollSlots(plot)
				refreshUpgradeUI(plot)
				setPlotLabelText(plot, player.DisplayName .. "'s Refinery")
			end
			return i
		end
	end
	return nil
end

local function clonePickPart()
	local pick = PickaxeTemplate:Clone()
	if not pick:IsA("BasePart") then
		pick = pick:FindFirstChildWhichIsA("BasePart", true)
	end
	return pick
end

local function applyHoldGrip(tool, handle)
	local half = handle.Size.X * 0.5
	local ox = PickaxeTemplate:GetAttribute("GripOffsetX")
	if typeof(ox) ~= "number" then ox = -(half - 0.12) end
	tool.Grip = CFrame.new(ox, 0, 0) * CFrame.Angles(0, math.rad(90), math.rad(90))
end

local function animateButton(btn)
	if not btn or not btn:IsA("BasePart") then return end
	local start = btn.CFrame
	local pressed = start * CFrame.new(0, -0.14, 0)
	local tw1 = TweenService:Create(btn, TweenInfo.new(0.08, Enum.EasingStyle.Quad, Enum.EasingDirection.Out), { CFrame = pressed })
	tw1:Play()
	tw1.Completed:Wait()
	task.wait(0.06)
	local tw2 = TweenService:Create(btn, TweenInfo.new(0.18, Enum.EasingStyle.Back, Enum.EasingDirection.Out), { CFrame = start })
	tw2:Play()
	tw2.Completed:Wait()
end

-- Back-compat alias
local function animateLever(arm)
	local btn = arm
	if arm and not (arm.Name == "RollButton" or arm.Name == "SideButton") then
		local parent = arm.Parent
		btn = (parent and (parent:FindFirstChild("RollButton", true) or parent:FindFirstChild("SideButton", true))) or arm
	end
	animateButton(btn)
end

local function stopIdleAnim(pick)
	idlePickAnims[pick] = nil
end

local function startIdleAnim(pick, pos, yaw)
	idlePickAnims[pick] = { pos = pos, t0 = os.clock(), yaw = yaw or 0 }
end

local function spawnPickaxeAt(plot, rarity, spawnPt)
	local st = plot:FindFirstChild("RollStation")
	if not st or not spawnPt then return end
	local pick = clonePickPart()
	if not pick then return end
	pick.Name = rarity.Name .. "Pickaxe"
	pick.Anchored = true
	pick.CanCollide = false
	pick.Parent = st
	pick:SetAttribute("IsRolledPickaxe", true)
	pick:SetAttribute("Rarity", rarity.Name)
	pick:SetAttribute("Mult", rarity.Mult)
	local pos = spawnPt.Position + Vector3.new(0, 2.35, 0)
	-- Appear already on pedestal as black silhouette (no drop-in)
	pick.CFrame = worldUpright(pos, 0)
	pick.Color = Color3.new(0.04, 0.04, 0.04)
	pick.Material = Enum.Material.SmoothPlastic
	pick.Transparency = 0
	local prompt = Instance.new("ProximityPrompt")
	prompt.Name = "BuyPrompt"
	prompt.ActionText = "Buy"
	prompt.ObjectText = ""
	prompt.HoldDuration = 0
	prompt.MaxActivationDistance = 10
	prompt.RequiresLineOfSight = false
	prompt.Enabled = false
	prompt.Parent = pick

	task.spawn(function()
		local duration = 1.35 + math.random() * 0.45
		local t0 = os.clock()
		local baseSize = PickaxeTemplate.Size
		while pick.Parent and os.clock() - t0 < duration do
			-- Flash black silhouette between candidates (shape pulse only — no color reveal)
			local pulse = 0.88 + 0.22 * math.random()
			pick.Color = Color3.new(0.02, 0.02, 0.02)
			pick.Material = Enum.Material.SmoothPlastic
			pick.Size = baseSize * pulse
			task.wait(0.06 + math.random() * 0.04)
		end
		if not pick.Parent then return end
		pick.Size = baseSize
		pick.Color = rarity.Color
		pick.Material = Enum.Material.SmoothPlastic
		attachBuyBillboard(pick, rarity)
		prompt.Enabled = true
		startIdleAnim(pick, pos, 0)
	end)
	return pick
end

RunService.Heartbeat:Connect(function()
	for pick, st in pairs(idlePickAnims) do
		if not pick.Parent then
			idlePickAnims[pick] = nil
		else
			local t = os.clock() - st.t0
			local bounce = math.sin(t * 1.35) * 0.14
			-- Float only (no yaw spin)
			pick.CFrame = worldUpright(st.pos + Vector3.new(0, bounce, 0), st.yaw or 0)
		end
	end
end)

local function clearPlotPickaxes(plot)
	local st = plot:FindFirstChild("RollStation")
	if not st then return end
	for _, child in ipairs(st:GetChildren()) do
		if child:GetAttribute("IsRolledPickaxe") then
			stopIdleAnim(child)
			child:Destroy()
		end
	end
end

local function attachBuyBillboard(pick, rarity)
	local old = pick:FindFirstChild("BuyGui")
	if old then old:Destroy() end
	local gui = Instance.new("BillboardGui")
	gui.Name = "BuyGui"
	gui.Size = UDim2.fromOffset(160, 72)
	gui.StudsOffset = Vector3.new(0, 2.8, 0)
	gui.AlwaysOnTop = true
	gui.MaxDistance = 40
	gui.Parent = pick
	local frame = Instance.new("Frame")
	frame.Size = UDim2.fromScale(1, 1)
	frame.BackgroundColor3 = Color3.fromRGB(20, 18, 16)
	frame.BackgroundTransparency = 0.25
	frame.BorderSizePixel = 0
	frame.Parent = gui
	local corner = Instance.new("UICorner")
	corner.CornerRadius = UDim.new(0, 8)
	corner.Parent = frame
	local name = Instance.new("TextLabel")
	name.Name = "Name"
	name.BackgroundTransparency = 1
	name.Size = UDim2.new(1, -8, 0.34, 0)
	name.Position = UDim2.fromOffset(4, 2)
	name.Font = Enum.Font.GothamBold
	name.TextScaled = true
	name.TextColor3 = rarity.Color
	name.Text = rarity.Name
	name.Parent = frame
	local rate = Instance.new("TextLabel")
	rate.Name = "Rate"
	rate.BackgroundTransparency = 1
	rate.Size = UDim2.new(1, -8, 0.28, 0)
	rate.Position = UDim2.new(0, 4, 0.34, 0)
	rate.Font = Enum.Font.Gotham
	rate.TextScaled = true
	rate.TextColor3 = Color3.fromRGB(255, 220, 120)
	rate.Text = tostring(incomePerPick(rarity.Mult, 0)) .. " Ore/s"
	rate.Parent = frame
	local buy = Instance.new("TextLabel")
	buy.Name = "Buy"
	buy.BackgroundTransparency = 1
	buy.Size = UDim2.new(1, -8, 0.3, 0)
	buy.Position = UDim2.new(0, 4, 0.64, 0)
	buy.Font = Enum.Font.GothamBold
	buy.TextScaled = true
	buy.TextColor3 = Color3.fromRGB(120, 255, 140)
	buy.Text = "E Buy"
	buy.Parent = frame
	return gui
end

local function rollPickaxes(plot)
	clearPlotPickaxes(plot)
	local luck = select(1, getLevels(plot))
	local spawned = {}
	for _, pt in ipairs(getActiveSpawnPoints(plot)) do
		local pick = spawnPickaxeAt(plot, rollRarity(luck), pt)
		if pick then table.insert(spawned, pick) end
	end
	return spawned
end

local function giveTool(player, rarity)
	local backpack = player:FindFirstChildOfClass("Backpack")
	local char = player.Character
	if not backpack then return end
	for _, t in ipairs(backpack:GetChildren()) do
		if t:IsA("Tool") and t:GetAttribute("IsPickaxe") then t:Destroy() end
	end
	if char then
		for _, t in ipairs(char:GetChildren()) do
			if t:IsA("Tool") and t:GetAttribute("IsPickaxe") then t:Destroy() end
		end
	end
	local tool = Instance.new("Tool")
	tool.Name = rarity.Name .. " Pickaxe"
	tool.RequiresHandle = true
	tool.CanBeDropped = false
	tool:SetAttribute("IsPickaxe", true)
	tool:SetAttribute("Rarity", rarity.Name)
	tool:SetAttribute("Mult", rarity.Mult)
	local handle = clonePickPart()
	handle.Name = "Handle"
	handle.Anchored = false
	handle.CanCollide = false
	handle.Massless = true
	handle.Color = rarity.Color
	handle.Parent = tool
	applyHoldGrip(tool, handle)
	tool.Parent = backpack
	local data = dataCache[player.UserId] or {}
	data.Rarity = rarity.Name
	data.Mult = rarity.Mult
	data.ColorR, data.ColorG, data.ColorB = rarity.Color.R, rarity.Color.G, rarity.Color.B
	dataCache[player.UserId] = data
	return tool
end

local function stopMining(userId)
	local state = mining[userId]
	if not state then return end
	state.alive = false
	if state.picks then
		for _, p in ipairs(state.picks) do
			if p and p.Parent then p:Destroy() end
		end
	end
	if state.vfx then state.vfx:Destroy() end
	mining[userId] = nil
end

local function pushOrePickaxes(player)
	local state = mining[player.UserId]
	local list = {}
	if state and state.alive and state.pickInfo then
		local plot = state.oreModel and state.oreModel.Parent
		local oreLevel = 0
		if plot then
			local _l, o = getLevels(plot)
			oreLevel = o
		end
		for i, info in ipairs(state.pickInfo) do
			table.insert(list, {
				Index = i,
				Name = info.Name,
				Rarity = info.Name,
				Mult = info.Mult,
				Income = incomePerPick(info.Mult, oreLevel or 0),
			})
		end
	end
	OrePickaxesUpdate:FireClient(player, list)
	return list
end

-- Handle along +Z (up after -90 X). Tip along local +X into ore; pitch swings tip UP/DOWN (not sideways).
local function mineChopCF(orePart, slotIndex, totalSlots, pitch)
	local hit = orePart.Position + Vector3.new(0, orePart.Size.Y * 0.18, 0)
	local angle = ((slotIndex - 1) / math.max(totalSlots, 1)) * math.pi * 2
	local radius = 2.35 + 0.12 * totalSlots
	local orbit = hit + Vector3.new(math.cos(angle) * radius, 0, math.sin(angle) * radius)
	local flat = Vector3.new(hit.X - orbit.X, 0, hit.Z - orbit.Z)
	if flat.Magnitude < 0.05 then
		flat = Vector3.new(0, 0, -1)
	else
		flat = flat.Unit
	end
	local tipReach = 1.55
	-- Raise tip when pitch>0, strike down when pitch<0 (keep tip near hit)
	local lift = math.max(0, pitch) * 0.55
	local pos = hit - flat * tipReach + Vector3.new(0, 0.85 + lift, 0)
	local yaw = math.atan2(-flat.X, -flat.Z)
	local baseDip = math.rad(-58)
	return CFrame.new(pos)
		* CFrame.Angles(0, yaw, 0)
		* CFrame.Angles(math.rad(-90), 0, 0) -- upright handle (+Z up)
		* CFrame.Angles(0, 0, baseDip) -- aim tip (+X) into ore
		* CFrame.Angles(0, pitch, 0) -- up/down chop around sideways axis
end

local function recountIncome(state, plot)
	local _, oreLevel = getLevels(plot)
	local total = 0
	for _, info in ipairs(state.pickInfo) do
		total += incomePerPick(info.Mult, oreLevel)
	end
	setOreIncomeText(plot, total)
	return total
end

local function startOrAddMining(player, oreModel, rarity)
	local userId = player.UserId
	local plot = oreModel.Parent
	local orePart = oreModel.PrimaryPart or oreModel:FindFirstChildWhichIsA("BasePart", true)
	if not orePart then return end
	local cap = pickaxeCapacity(plot)
	local state = mining[userId]
	if state and state.oreModel == oreModel and state.alive then
		if #state.picks >= cap then return false end
	else
		stopMining(userId)
		state = { alive = true, picks = {}, pickInfo = {}, oreModel = oreModel, vfx = nil }
		mining[userId] = state
		local vfx = Instance.new("ParticleEmitter")
		vfx.Color = ColorSequence.new(rarity.Color)
		vfx.Rate = 10
		vfx.Lifetime = NumberRange.new(0.3, 0.7)
		vfx.Speed = NumberRange.new(1, 3)
		vfx.Parent = orePart
		state.vfx = vfx
		local _, oreWallet = ensureCurrencies(player)
		task.spawn(function()
			while state.alive and player.Parent do
				task.wait(TICK)
				if not state.alive then break end
				local gain = recountIncome(state, plot)
				oreWallet.Value += gain
			end
		end)
		task.spawn(function()
			local t0 = os.clock()
			while state.alive do
				local n = #state.picks
				local a = math.sin((os.clock() - t0) * 5.5) * math.rad(38)
				for i, pick in ipairs(state.picks) do
					if pick and pick.Parent then
						local phase = a + (i - 1) * 0.35
						pick.CFrame = mineChopCF(orePart, i, n, phase)
					end
				end
				task.wait()
			end
		end)
	end

	local pick = clonePickPart()
	pick.Name = "MiningPickaxe"
	pick.Anchored = true
	pick.CanCollide = false
	pick.Color = rarity.Color
	pick.Parent = oreModel
	table.insert(state.picks, pick)
	table.insert(state.pickInfo, { Name = rarity.Name, Mult = rarity.Mult or 1, Color = rarity.Color })
	recountIncome(state, plot)
	pushOrePickaxes(player)
	return true
end

local function tryBuyUpgrade(player, plot, plotId, upgradeType)
	claimPlot(player)
	if playerPlot[player.UserId] ~= plotId then return end
	local luck, ore, active, rolls = getLevels(plot)
	local cash = ensureLeaderstats(player)
	if upgradeType == "Luck" then
		if luck >= MAX_LUCK then return end
		local price = LUCK_COST[luck + 1]
		if cash.Value < price then return end
		cash.Value -= price
		plot:SetAttribute("LuckLevel", luck + 1)
	elseif upgradeType == "Active" then
		if active >= MAX_ACTIVE - 1 then return end
		local price = ACTIVE_COST[active + 1]
		if cash.Value < price then return end
		cash.Value -= price
		plot:SetAttribute("ActiveLevel", active + 1)
	elseif upgradeType == "Rolls" then
		if rolls >= MAX_ROLLS then return end
		local price = ROLL_COST[rolls + 1]
		if cash.Value < price then return end
		cash.Value -= price
		plot:SetAttribute("RollsLevel", rolls + 1)
		unlockNextRollSlot(plot)
	end
	refreshUpgradeUI(plot)
	saveData(player)
end

local function bindUpgradeBoard(plot, plotId)
	local board = plot:FindFirstChild("UpgradeBoard")
	if not board then
		board = makeUpgradeBoard(plot)
	end
	-- Ensure UpgradeType attrs match renamed slots
	local map = {
		LuckSlot = "Luck",
		PickaxesSlot = "Active",
		ActivePickaxesSlot = "Active",
		PickaxeLuckSlot = "Luck",
		RollsSlot = "Rolls",
	}
	local oreSlot = board:FindFirstChild("OreSlot")
	if oreSlot then oreSlot:Destroy() end
	for slotName, ut in pairs(map) do
		local slot = board:FindFirstChild(slotName)
		if slot then slot:SetAttribute("UpgradeType", ut) end
	end
	-- Also bind frames named Luck/Pickaxes/Rolls if click slots missing attributes
	refreshUpgradeUI(plot)
	for _, slot in ipairs(board:GetDescendants()) do
		if slot:IsA("BasePart") and slot:FindFirstChild("BuyClick") then
			local click = slot:FindFirstChild("BuyClick")
			if click and not click:GetAttribute("Bound") then
				click:SetAttribute("Bound", true)
				click.MouseClick:Connect(function(player)
					tryBuyUpgrade(player, plot, plotId, slot:GetAttribute("UpgradeType"))
				end)
			end
		end
	end
end

local function tryBuyRolled(player, pick)
	if not pick or not pick.Parent or not pick:GetAttribute("IsRolledPickaxe") then return end
	local plot = pick.Parent and pick.Parent.Parent
	if not plot or not plot:IsA("Model") then return end
	local plotId = plot:GetAttribute("PlotId")
	claimPlot(player)
	if playerPlot[player.UserId] ~= plotId then return end
	local data = dataCache[player.UserId] or {}
	local rarityName = pick:GetAttribute("Rarity") or "Common"
	local mult = pick:GetAttribute("Mult") or 1
	local price = buyPrice(rarityName, data.FreeBuyUsed == true)
	local cash = ensureLeaderstats(player)
	if price > 0 and cash.Value < price then return end
	if price > 0 then cash.Value -= price end
	if not data.FreeBuyUsed then
		data.FreeBuyUsed = true
		dataCache[player.UserId] = data
	end
	stopIdleAnim(pick)
	giveTool(player, { Name = rarityName, Mult = mult, Color = pick.Color })
	pick:Destroy()
	saveData(player)
end

local function makePadLabel(part, text, color)
	local gui = Instance.new("BillboardGui")
	gui.Name = "PadLabel"
	gui.Size = UDim2.fromOffset(160, 40)
	gui.StudsOffset = Vector3.new(0, 2.2, 0)
	gui.AlwaysOnTop = true
	gui.MaxDistance = 60
	gui.Parent = part
	local t = Instance.new("TextLabel")
	t.Size = UDim2.fromScale(1, 1)
	t.BackgroundTransparency = 1
	t.Font = Enum.Font.GothamBold
	t.TextScaled = true
	t.TextColor3 = color
	t.TextStrokeTransparency = 0.4
	t.Text = text
	t.Parent = gui
end

local function playerStandingOn(part, player)
	local char = player.Character
	local hrp = char and char:FindFirstChild("HumanoidRootPart")
	if not hrp then return false end
	local rel = part.CFrame:PointToObjectSpace(hrp.Position)
	local half = part.Size * 0.5
	return math.abs(rel.X) <= half.X + 1.2
		and math.abs(rel.Z) <= half.Z + 1.2
		and rel.Y >= -1
		and rel.Y <= half.Y + 4
end

local function buildMiniRefiner()
	local model = Instance.new("Model")
	model.Name = "MiniOreRefiner"
	local function part(name, size, cf, color, material)
		local p = Instance.new("Part")
		p.Name = name
		p.Anchored = true
		p.CanCollide = false
		p.Size = size
		p.CFrame = cf
		p.Color = color
		p.Material = material or Enum.Material.Metal
		p.Parent = model
		return p
	end
	local body = Color3.fromRGB(46, 36, 31)
	local metal = Color3.fromRGB(90, 88, 85)
	local dark = Color3.fromRGB(22, 20, 18)
	local lava = Color3.fromRGB(255, 90, 20)
	local glass = Color3.fromRGB(120, 180, 255)
	local base = part("Base", Vector3.new(4.2, 0.35, 4.2), CFrame.new(0, 0.175, 0), dark, Enum.Material.Basalt)
	base.CanCollide = true
	part("Tank", Vector3.new(2.8, 2.4, 2.8), CFrame.new(0, 1.55, 0), body, Enum.Material.Metal)
	part("Hopper", Vector3.new(3.2, 1.0, 3.2), CFrame.new(0, 3.05, 0), metal, Enum.Material.Metal)
	part("HopperTop", Vector3.new(1.6, 0.45, 1.6), CFrame.new(0, 3.7, 0), metal, Enum.Material.Metal)
	part("IntakeRim", Vector3.new(1.9, 0.25, 1.9), CFrame.new(0, 3.95, 0), metal, Enum.Material.Metal)
	part("SidePipe", Vector3.new(1.8, 0.4, 0.4), CFrame.new(1.9, 1.6, 0), metal, Enum.Material.Metal)
	part("PipeElbow", Vector3.new(0.55, 0.55, 0.55), CFrame.new(2.7, 1.6, 0), metal, Enum.Material.Metal)
	part("Chimney", Vector3.new(0.7, 1.4, 0.7), CFrame.new(-1.0, 3.2, 1.0), dark, Enum.Material.Metal)
	part("ChimneyCap", Vector3.new(0.95, 0.3, 0.95), CFrame.new(-1.0, 4.0, 1.0), metal, Enum.Material.Metal)
	local w1 = part("LavaWindow", Vector3.new(0.2, 1.2, 1.4), CFrame.new(1.45, 1.6, 0), lava, Enum.Material.Neon)
	local w2 = part("LavaWindow2", Vector3.new(0.2, 1.2, 1.4), CFrame.new(-1.45, 1.6, 0), lava, Enum.Material.Neon)
	part("ControlBox", Vector3.new(1.0, 1.0, 0.8), CFrame.new(1.3, 0.85, -1.3), dark, Enum.Material.Basalt)
	part("Indicator", Vector3.new(0.28, 0.28, 0.28), CFrame.new(1.3, 1.45, -1.3), glass, Enum.Material.Neon)
	for i, off in ipairs({
		Vector3.new(1.4, 0.55, 1.4),
		Vector3.new(1.4, 0.55, -1.4),
		Vector3.new(-1.4, 0.55, 1.4),
		Vector3.new(-1.4, 0.55, -1.4),
	}) do
		part("Leg" .. i, Vector3.new(0.35, 0.9, 0.35), CFrame.new(off), metal, Enum.Material.Metal)
	end
	model.PrimaryPart = base
	return model
end

local function attachRefinerVisual(plot, refinePad)
	local old = plot:FindFirstChild("MiniOreRefiner")
	if old then old:Destroy() end
	local model = buildMiniRefiner()
	model.Parent = plot
	local top = refinePad.Position + Vector3.new(0, refinePad.Size.Y * 0.5, 0)
	model:PivotTo(CFrame.new(top))
	refinePad.Transparency = 1
	refinePad.Material = Enum.Material.SmoothPlastic
	local lbl = refinePad:FindFirstChild("PadLabel")
	if lbl then
		lbl.StudsOffset = Vector3.new(0, 5.4, 0)
		local t = lbl:FindFirstChildOfClass("TextLabel")
		if t then t.Text = "REFINE" end
	end
	return model
end

local function ensureEconomyPads(plot, plotId)
	local pad = plotPad(plot)
	if not pad then return end
	local right = pad.CFrame.RightVector
	local look = pad.CFrame.LookVector

	local function ensurePad(name, offset, color, labelText, labelColor)
		local p = plot:FindFirstChild(name)
		if not p then
			p = Instance.new("Part")
			p.Name = name
			p.Anchored = true
			p.CanCollide = true
			p.Material = Enum.Material.Neon
			p.Size = Vector3.new(5.5, 0.6, 5.5)
			p.Parent = plot
		end
		p.Color = color
		p.CFrame = CFrame.new(pad.Position + offset + Vector3.new(0, 0.85, 0))
		if not p:FindFirstChild("PadLabel") then
			makePadLabel(p, labelText, labelColor)
		end
		return p
	end

	local sell = ensurePad(
		"SellPad",
		-right * 8 + look * 8,
		Color3.fromRGB(40, 120, 55),
		"SELL ORE",
		Color3.fromRGB(140, 255, 160)
	)
	local refine = ensurePad(
		"RefinePad",
		right * 8 + look * 8,
		Color3.fromRGB(70, 90, 160),
		"REFINE",
		Color3.fromRGB(160, 190, 255)
	)
	attachRefinerVisual(plot, refine)

	if not sell:GetAttribute("Bound") then
		sell:SetAttribute("Bound", true)
		task.spawn(function()
			while sell.Parent do
				task.wait(PAD_TICK)
				for _, plr in ipairs(Players:GetPlayers()) do
					if playerPlot[plr.UserId] == plotId and playerStandingOn(sell, plr) then
						local cash, oreV = ensureCurrencies(plr)
						if oreV.Value > 0 then
							local amt = oreV.Value
							oreV.Value = 0
							cash.Value += amt * SELL_RATE
						end
					end
				end
			end
		end)
	end

	if not refine:GetAttribute("Bound") then
		refine:SetAttribute("Bound", true)
		task.spawn(function()
			while refine.Parent do
				task.wait(PAD_TICK)
				for _, plr in ipairs(Players:GetPlayers()) do
					if playerPlot[plr.UserId] == plotId and playerStandingOn(refine, plr) then
						local _, oreV, refinedV = ensureCurrencies(plr)
						if oreV.Value > 0 then
							local take = math.min(oreV.Value, REFINE_RATE)
							oreV.Value -= take
							refinedV.Value += take
						end
					end
				end
			end
		end)
	end
end

local function setupPlot(plot)
	local plotId = plot:GetAttribute("PlotId") or tonumber(string.match(plot.Name, "%d+"))
	plot:SetAttribute("PlotId", plotId)
	if plot:GetAttribute("LuckLevel") == nil then plot:SetAttribute("LuckLevel", 0) end
	if plot:GetAttribute("OreLevel") == nil then plot:SetAttribute("OreLevel", 0) end
	if plot:GetAttribute("ActiveLevel") == nil then plot:SetAttribute("ActiveLevel", 0) end
	if plot:GetAttribute("RollsLevel") == nil then plot:SetAttribute("RollsLevel", 0) end
	applyStarterRollSlots(plot)
	if plotOwners[plotId] then
		local owner = Players:GetPlayerByUserId(plotOwners[plotId])
		if owner then
			setPlotLabelText(plot, owner.DisplayName .. "'s Refinery")
		else
			setPlotLabelText(plot, "Unclaimed Refinery")
		end
	else
		setPlotLabelText(plot, "Unclaimed Refinery")
	end
	local ore = plot:FindFirstChild("Ore")
	if ore then
		ensureOreIncomeGui(ore)
		setOreIncomeText(plot, 0)
		local oldPrompt = (ore.PrimaryPart or ore:FindFirstChildWhichIsA("BasePart", true))
		if oldPrompt then
			local mp = oldPrompt:FindFirstChild("MinePrompt")
			if mp then mp:Destroy() end
		end
	end
	bindUpgradeBoard(plot, plotId)
	ensureEconomyPads(plot, plotId)

	local st = plot:FindFirstChild("RollStation")
	local function bindRollButton(btn)
		if not btn then return end
		local prompt = btn:FindFirstChild("RollPrompt") or btn:FindFirstChild("RollPrompt", true)
		if not prompt then
			prompt = Instance.new("ProximityPrompt")
			prompt.Name = "RollPrompt"
			prompt.HoldDuration = 0
			prompt.MaxActivationDistance = 12
			prompt.RequiresLineOfSight = false
			prompt.Parent = btn
		end
		if prompt:GetAttribute("Bound") then return end
		prompt:SetAttribute("Bound", true)
		prompt.ActionText = "Roll"
		prompt.ObjectText = "Button"
		prompt.Triggered:Connect(function(player)
			claimPlot(player)
			if playerPlot[player.UserId] ~= plotId then return end
			local now = tick()
			if lastRoll[player.UserId] and now - lastRoll[player.UserId] < ROLL_COOLDOWN then return end
			lastRoll[player.UserId] = now
			prompt.Enabled = false
			animateButton(btn)
			local spawned = rollPickaxes(plot)
			prompt.Enabled = true
			for _, pick in ipairs(spawned) do
				local buy = pick:FindFirstChild("BuyPrompt")
				if buy then
					buy.Triggered:Connect(function(p)
						tryBuyRolled(p, pick)
					end)
				end
			end
		end)
	end
	if st then
		bindRollButton(st:FindFirstChild("RollButton", true))
		bindRollButton(st:FindFirstChild("YellowButton", true))
		-- legacy name
		local sideModel = st:FindFirstChild("SideButton")
		if sideModel and sideModel:IsA("Model") then
			bindRollButton(sideModel:FindFirstChild("YellowButton", true) or sideModel:FindFirstChildWhichIsA("BasePart", true))
		end
	end
end

-- Remotes
RequestAttach.OnServerEvent:Connect(function(player)
	claimPlot(player)
	local id = playerPlot[player.UserId]
	local plot = id and Plots:FindFirstChild("Plot" .. id)
	if not plot then return end
	local ore = plot:FindFirstChild("Ore")
	if not ore then return end
	local char = player.Character
	if not char then return end
	local tool = char:FindFirstChildOfClass("Tool")
	if not (tool and tool:GetAttribute("IsPickaxe")) then return end
	local hrp = char:FindFirstChild("HumanoidRootPart")
	local orePart = ore.PrimaryPart or ore:FindFirstChildWhichIsA("BasePart", true)
	if not hrp or not orePart then return end
	if (hrp.Position - orePart.Position).Magnitude > 16 then return end
	local state = mining[player.UserId]
	local cap = pickaxeCapacity(plot)
	if state and state.alive and state.oreModel == ore and #state.picks >= cap then return end
	local rarity = {
		Name = tool:GetAttribute("Rarity") or "Common",
		Mult = tool:GetAttribute("Mult") or 1,
		Color = (tool:FindFirstChild("Handle") and tool.Handle.Color) or Color3.new(1, 1, 1),
	}
	tool:Destroy()
	startOrAddMining(player, ore, rarity)
end)

RemoveOrePickaxe.OnServerEvent:Connect(function(player, index)
	index = tonumber(index)
	if not index then return end
	local state = mining[player.UserId]
	if not (state and state.alive and state.pickInfo[index]) then return end
	local info = state.pickInfo[index]
	local pick = state.picks[index]
	table.remove(state.pickInfo, index)
	table.remove(state.picks, index)
	if pick and pick.Parent then pick:Destroy() end
	giveTool(player, { Name = info.Name, Mult = info.Mult, Color = info.Color })
	local plot = state.oreModel and state.oreModel.Parent
	if #state.picks == 0 then
		stopMining(player.UserId)
		if plot then setOreIncomeText(plot, 0) end
	elseif plot then
		recountIncome(state, plot)
	end
	pushOrePickaxes(player)
	saveData(player)
end)

BuyRolledPickaxe.OnServerEvent:Connect(function(player, pick)
	if typeof(pick) ~= "Instance" then return end
	tryBuyRolled(player, pick)
end)

GetOrePickaxes.OnServerInvoke = function(player)
	return pushOrePickaxes(player)
end

ensureLighting()
if PickaxeTemplate:IsA("BasePart") then
	PickaxeTemplate:SetAttribute("GripOffsetX", -(PickaxeTemplate.Size.X * 0.5 - 0.12))
end
for _, plot in ipairs(Plots:GetChildren()) do
	if plot:IsA("Model") then setupPlot(plot) end
end

Players.PlayerAdded:Connect(function(player)
	local data = loadData(player)
	claimPlot(player)
	if data.Rarity then
		player.CharacterAdded:Once(function()
			task.wait(0.5)
			giveTool(player, {
				Name = data.Rarity,
				Mult = data.Mult or 1,
				Color = Color3.new(data.ColorR or 1, data.ColorG or 1, data.ColorB or 1),
			})
		end)
	end
	player.CharacterAdded:Connect(function()
		local id = playerPlot[player.UserId]
		local plot = id and Plots:FindFirstChild("Plot" .. id)
		local pad = plot and plotPad(plot)
		if pad then
			task.wait(0.25)
			local hrp = player.Character and player.Character:FindFirstChild("HumanoidRootPart")
			if hrp then hrp.CFrame = pad.CFrame * CFrame.new(0, 5, 0) end
		end
	end)
end)

Players.PlayerRemoving:Connect(function(player)
	saveData(player)
	stopMining(player.UserId)
	local id = playerPlot[player.UserId]
	if id then
		plotOwners[id] = nil
		local plot = Plots:FindFirstChild("Plot" .. id)
		if plot then
			setPlotLabelText(plot, "Unclaimed Refinery")
		end
	end
	playerPlot[player.UserId] = nil
	lastRoll[player.UserId] = nil
	dataCache[player.UserId] = nil
end)

game:BindToClose(function()
	for _, p in ipairs(Players:GetPlayers()) do
		saveData(p)
	end
	task.wait(2)
end)
task.spawn(function()
	while true do
		task.wait(SAVE_INTERVAL)
		for _, p in ipairs(Players:GetPlayers()) do
			saveData(p)
		end
	end
end)
for _, p in ipairs(Players:GetPlayers()) do
	loadData(p)
	claimPlot(p)
end
print("[TESTING] upright-Z, inward upgrades, Attach/Manage, paid buys")

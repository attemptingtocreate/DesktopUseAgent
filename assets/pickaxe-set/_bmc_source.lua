-- Rebuilds Blender EditableMesh props on the CLIENT so they render in Play.
-- Empty-MeshId MeshParts from Edit appear as gray boxes; client rebuilds + keeps EditableMesh alive.
-- Build each unique package ONCE (capped to EditableMesh memory budget), then Clone onto plots.
local AS = game:GetService("AssetService")
local RS = game:GetService("ReplicatedStorage")
local Players = game:GetService("Players")

-- Play Solo EditableMesh budget is tiny (~8). Cap total creates across all templates.
local MAX_EDITABLE = 8
local MAX_PARTS_PER_PACKAGE = 2

local KEEP_ALIVE = {} -- [EditableMesh] = true (forever)
local TEMPLATES = {} -- [packageName] = Model template (built once)
local budgetUsed = 0
local budgetExhausted = false

local function keepAliveCount()
	local n = 0
	for _ in pairs(KEEP_ALIVE) do
		n += 1
	end
	return n
end

local function showMeshApiHelp(err)
	warn("[BlenderMeshClient] EditableMesh failed:", err)
	local lp = Players.LocalPlayer
	if not lp then
		return
	end
	local pg = lp:WaitForChild("PlayerGui")
	if pg:FindFirstChild("BlenderMeshHelp") then
		return
	end
	local gui = Instance.new("ScreenGui")
	gui.Name = "BlenderMeshHelp"
	gui.ResetOnSpawn = false
	gui.Parent = pg
	local f = Instance.new("TextLabel")
	f.Size = UDim2.new(0.7, 0, 0, 110)
	f.Position = UDim2.new(0.15, 0, 0.05, 0)
	f.BackgroundColor3 = Color3.fromRGB(40, 20, 20)
	f.TextColor3 = Color3.fromRGB(255, 220, 120)
	f.TextWrapped = true
	f.Font = Enum.Font.GothamBold
	f.TextSize = 16
	f.Text =
		"Blender meshes blocked / limited in Play.\n"
		.. "1) File → Experience Settings → Security → Allow Mesh / Image APIs\n"
		.. "2) EditableMesh memory budget (~8 meshes) — using capped templates + Clone.\n"
		.. "Stop + Play again after fixing."
	f.Parent = gui
end

local function getPackages()
	local packages = {}
	local function merge(mod, overwrite)
		if not mod then
			return
		end
		local ok, pkg = pcall(require, mod)
		if not ok or typeof(pkg) ~= "table" then
			warn("[BlenderMeshClient] require failed:", mod.Name, pkg)
			return
		end
		local function put(name, data)
			if not name or typeof(data) ~= "table" or not data.parts then
				return
			end
			if packages[name] and not overwrite then
				return
			end
			packages[name] = data
		end
		if pkg.parts and pkg.name then
			put(pkg.name, pkg)
		else
			for k, v in pairs(pkg) do
				if typeof(v) == "table" and v.parts then
					put(k, v)
				end
			end
		end
	end
	merge(RS:FindFirstChild("MeshPackagesV2") or RS:FindFirstChild("MeshPackages"), true)
	merge(RS:FindFirstChild("PropsOnlyMeshV2") or RS:FindFirstChild("PropsOnlyMesh"), true)
	merge(
		RS:FindFirstChild("UpgradeSignMeshV3")
			or RS:FindFirstChild("UpgradeSignMeshV2")
			or RS:FindFirstChild("UpgradeSignMesh"),
		true
	)
	return packages
end

local PART_PRIORITY = {
	RollButton = 100,
	YellowButton = 100,
	Button = 99,
	Board = 95,
	Header = 90,
	Rock = 88,
	RefineText = 85,
	HeaderText = 84,
	Base = 80,
	Pedestal = 78,
	Column = 70,
	Top = 68,
	LuckPanel = 60,
	PickaxesPanel = 59,
	RollsPanel = 58,
	SignFace = 50,
	Lump = 40,
}

local function partPriority(pd)
	return PART_PRIORITY[pd.name] or 10
end

local function selectPartsForBudget(pkg)
	local ranked = {}
	for _, pd in ipairs(pkg.parts) do
		table.insert(ranked, pd)
	end
	table.sort(ranked, function(a, b)
		return partPriority(a) > partPriority(b)
	end)
	local selected = {}
	for _, pd in ipairs(ranked) do
		if budgetExhausted or budgetUsed >= MAX_EDITABLE then
			break
		end
		if #selected >= MAX_PARTS_PER_PACKAGE then
			break
		end
		table.insert(selected, pd)
	end
	return selected
end

local function buildPart(partData)
	if budgetExhausted or budgetUsed >= MAX_EDITABLE then
		budgetExhausted = true
		error("EditableMesh budget exhausted")
	end
	task.wait(0.05)
	local okEm, em = pcall(function()
		return AS:CreateEditableMesh()
	end)
	if not okEm then
		budgetExhausted = true
		if keepAliveCount() == 0 then
			showMeshApiHelp(em)
		end
		error(em)
	end
	if em == nil then
		budgetExhausted = true
		local msg = "CreateEditableMesh returned nil (memory budget limits?)"
		if keepAliveCount() == 0 then
			showMeshApiHelp(msg)
		else
			warn("[BlenderMeshClient]", msg, "(continuing with", keepAliveCount(), "kept meshes)")
		end
		error(msg)
	end
	local ids = {}
	for i, v in ipairs(partData.verts) do
		ids[i] = em:AddVertex(Vector3.new(v[1], v[2], v[3]))
	end
	for _, f in ipairs(partData.faces) do
		em:AddTriangle(ids[f[1] + 1], ids[f[2] + 1], ids[f[3] + 1])
	end
	local mp = AS:CreateMeshPartAsync(Content.fromObject(em))
	KEEP_ALIVE[em] = true
	budgetUsed += 1
	mp.Name = partData.name
	mp.Anchored = true
	mp.CanCollide = false
	mp.CanQuery = false
	mp.CanTouch = false
	local c = partData.color
	mp.Color = Color3.new(c[1], c[2], c[3])
	mp.Material = ((partData.emit or 0) > 0.5) and Enum.Material.Neon or Enum.Material.SmoothPlastic
	mp.TopSurface = Enum.SurfaceType.Smooth
	mp.BottomSurface = Enum.SurfaceType.Smooth
	mp:SetAttribute("BlenderVisual", true)
	return mp
end

local function decorateUpgrade(model)
	local board = model:FindFirstChild("Board")
	if board then
		for _, ch in ipairs(board:GetChildren()) do
			if ch:IsA("SurfaceGui") or ch:IsA("BillboardGui") then
				ch:Destroy()
			end
		end
	end
	local slotMap = {
		{ "LuckPanel", "LuckSlot", "Luck", "Luck" },
		{ "PickaxesPanel", "PickaxesSlot", "Active", "Pickaxes" },
		{ "RollsPanel", "RollsSlot", "Rolls", "Rolls" },
	}
	for _, info in ipairs(slotMap) do
		local p = model:FindFirstChild(info[1]) or model:FindFirstChild(info[2])
		if p then
			p.Name = info[2]
			p:SetAttribute("UpgradeType", info[3])
			for _, ch in ipairs(p:GetChildren()) do
				if ch:IsA("SurfaceGui") then
					ch:Destroy()
				end
			end
			local gui = Instance.new("SurfaceGui")
			gui.Name = "SlotGui"
			gui.Face = Enum.NormalId.Front
			gui.SizingMode = Enum.SurfaceGuiSizingMode.PixelsPerStud
			gui.PixelsPerStud = 50
			gui.Parent = p
			local f = Instance.new("Frame")
			f.Name = info[4]
			f.Size = UDim2.fromScale(1, 1)
			f.BackgroundTransparency = 1
			f.Parent = gui
			local function lab(n, y, h, txt, col)
				local l = Instance.new("TextLabel")
				l.Name = n
				l.BackgroundTransparency = 1
				l.Size = UDim2.new(1, -12, h, 0)
				l.Position = UDim2.new(0, 6, y, 0)
				l.Font = Enum.Font.GothamBold
				l.TextScaled = true
				l.TextColor3 = col
				l.Text = txt
				l.Parent = f
			end
			lab("Title", 0.06, 0.22, info[4], Color3.new(1, 1, 1))
			lab("Level", 0.34, 0.30, "Lv 0", Color3.fromRGB(180, 210, 255))
			lab("Cost", 0.70, 0.22, "$", Color3.fromRGB(255, 220, 120))
		end
	end
end

local function buildModel(pkg)
	local model = Instance.new("Model")
	model.Name = pkg.name or "BlenderModel"
	local primary = nil
	local selected = selectPartsForBudget(pkg)
	if #selected == 0 then
		warn("[BlenderMeshClient] no parts selected for", pkg.name, "budget=", budgetUsed)
		return model
	end
	for _, pd in ipairs(selected) do
		local ok, mp = pcall(buildPart, pd)
		if ok and mp then
			mp.Parent = model
			if
				pd.name == "Base"
				or pd.name == "Board"
				or pd.name == "Pedestal"
				or pd.name == "Rock"
				or pd.name == "RollButton"
				or pd.name == "YellowButton"
			then
				primary = mp
			end
			if not primary then
				primary = mp
			end
		else
			warn("[BlenderMeshClient] part failed:", pkg.name, pd.name, mp)
			break
		end
	end
	model.PrimaryPart = primary or model:FindFirstChildWhichIsA("BasePart")
	if model.Name == "UpgradeSign" or model.Name == "UpgradeBoard" then
		decorateUpgrade(model)
	end
	return model
end

local function getTemplate(pkg)
	if not pkg then
		return nil
	end
	local key = pkg.name or "unnamed"
	if TEMPLATES[key] then
		return TEMPLATES[key]
	end
	if budgetExhausted or budgetUsed >= MAX_EDITABLE then
		warn("[BlenderMeshClient] skip template (budget):", key)
		return nil
	end
	task.wait(0.15)
	local ok, model = pcall(buildModel, pkg)
	if not ok or not model then
		warn("[BlenderMeshClient] template build failed:", key, model)
		return nil
	end
	if not model.PrimaryPart then
		warn("[BlenderMeshClient] template has no PrimaryPart:", key)
		model:Destroy()
		return nil
	end
	TEMPLATES[key] = model
	print("[BlenderMeshClient] built template", key, "parts=", #model:GetChildren(), "keepalive=", keepAliveCount())
	return model
end

local function hideServerMeshes(model)
	for _, d in ipairs(model:GetDescendants()) do
		if d:IsA("BasePart") and not d:GetAttribute("BlenderVisual") then
			d.LocalTransparencyModifier = 1
		end
	end
end

local function attachVisual(host, pkg)
	if not host or not pkg then
		return false
	end
	local prev = host:FindFirstChild("BlenderVisualModel")
	if prev then
		prev:Destroy()
	end
	hideServerMeshes(host)
	local template = getTemplate(pkg)
	if not template then
		return false
	end
	local visual = template:Clone()
	visual.Name = "BlenderVisualModel"
	visual.Parent = host
	visual:PivotTo(host:GetPivot())
	local visBtn = visual:FindFirstChild("RollButton", true)
		or visual:FindFirstChild("YellowButton", true)
		or visual:FindFirstChild("Button", true)
	if visBtn then
		for _, d in ipairs(host:GetDescendants()) do
			if d:IsA("ProximityPrompt") and d.Parent ~= visBtn then
				d.Parent = visBtn
			end
		end
	end
	return true
end

local function applyPlot(plot, packages)
	local st = plot:FindFirstChild("RollStation")
	if st then
		attachVisual(st:FindFirstChild("RollButtonSystem"), packages.RollButtonSystem)
		attachVisual(st:FindFirstChild("SideButton"), packages.SideButton)
		for _, c in ipairs(st:GetChildren()) do
			if c.Name == "PickaxePedestal" then
				attachVisual(c, packages.PickaxePedestal)
			end
		end
	end
	attachVisual(plot:FindFirstChild("RefineSign"), packages.RefineSign)
	local ub = plot:FindFirstChild("UpgradeBoard") or plot:FindFirstChild("UpgradeSign")
	if ub then
		attachVisual(ub, packages.UpgradeSign)
	end
	local orePkg = packages.OreSimple or packages.OreTier1
	if orePkg then
		attachVisual(plot:FindFirstChild("Ore"), orePkg)
	end
end

local function plotLooksLocal(plot, lp)
	if not lp or not plot then
		return false
	end
	local uid = lp.UserId
	local name = lp.Name
	local display = lp.DisplayName
	for _, attr in ipairs({
		"Owner",
		"OwnerName",
		"OwnerUserId",
		"ClaimedBy",
		"PlayerName",
		"Player",
		"UserId",
	}) do
		local v = plot:GetAttribute(attr)
		if v == uid or v == name or v == display or tostring(v) == tostring(uid) then
			return true
		end
	end
	local ownerVal = plot:FindFirstChild("Owner") or plot:FindFirstChild("OwnerName")
	if ownerVal then
		if ownerVal:IsA("StringValue") and (ownerVal.Value == name or ownerVal.Value == display) then
			return true
		end
		if ownerVal:IsA("IntValue") or ownerVal:IsA("NumberValue") then
			if ownerVal.Value == uid then
				return true
			end
		end
		if ownerVal:IsA("ObjectValue") and ownerVal.Value == lp then
			return true
		end
	end
	for _, d in ipairs(plot:GetDescendants()) do
		if d:IsA("TextLabel") or d:IsA("TextBox") then
			local t = d.Text
			if typeof(t) == "string" and #t > 0 then
				if string.find(t, name, 1, true) or string.find(t, display, 1, true) then
					if string.find(string.lower(t), "refinery", 1, true) then
						return true
					end
				end
			end
		end
	end
	return false
end

local applied = false
local function run()
	if applied then
		return
	end
	local packages = getPackages()
	local count = 0
	for _ in pairs(packages) do
		count += 1
	end
	if count == 0 then
		warn("[BlenderMeshClient] no mesh packages in ReplicatedStorage yet")
		return
	end
	local plotsFolder = workspace:FindFirstChild("Plots") or workspace:WaitForChild("Plots", 30)
	if not plotsFolder then
		warn("[BlenderMeshClient] Plots folder missing")
		return
	end

	local lp = Players.LocalPlayer
	local ordered = {}
	local localPlot = nil
	for _, plot in ipairs(plotsFolder:GetChildren()) do
		if plot:IsA("Model") then
			if plotLooksLocal(plot, lp) then
				localPlot = plot
			else
				table.insert(ordered, plot)
			end
		end
	end
	if localPlot then
		table.insert(ordered, 1, localPlot)
	end

	-- Highest-value packages first within the ~8 EditableMesh budget (2 parts each → 4 packages).
	local needed = {
		packages.RollButtonSystem,
		packages.RefineSign,
		packages.UpgradeSign,
		packages.SideButton,
		packages.PickaxePedestal,
		packages.OreSimple or packages.OreTier1,
	}
	for _, pkg in ipairs(needed) do
		if pkg and not budgetExhausted then
			getTemplate(pkg)
		end
	end

	local okCount = 0
	for _, plot in ipairs(ordered) do
		local ok, err = pcall(applyPlot, plot, packages)
		if ok then
			okCount += 1
		else
			warn("[BlenderMeshClient] plot failed:", plot.Name, err)
		end
	end
	applied = okCount > 0
	local templateCount = 0
	for _ in pairs(TEMPLATES) do
		templateCount += 1
	end
	print(
		"[BlenderMeshClient] applied plots=",
		okCount,
		"packages=",
		count,
		"templates=",
		templateCount,
		"keepalive=",
		keepAliveCount(),
		"budgetUsed=",
		budgetUsed,
		"exhausted=",
		budgetExhausted
	)
end

task.defer(run)
task.delay(0.5, run)
task.delay(1.5, run)
task.delay(3, run)

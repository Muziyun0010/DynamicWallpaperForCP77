local videoWidgets = {}
local videoIndexes = {}
local videoAspects = {}
local total = 1

registerForEvent('onInit', function()
    math.randomseed(os.time())

    local function parseAspectName(value)
        local text = tostring(value or "")
        local w, h = text:match("(%d+)%s*[xX]%s*(%d+)")
        if not w then
            w, h = text:match("(%d+)[_:/](%d+)")
        end
        w = tonumber(w)
        h = tonumber(h)
        if w and h and h ~= 0 then
            return w / h
        end
        return nil
    end

    local function getScreenAspect(gameController)
        local screenDef = gameController:GetScreenDefinition()
        if not screenDef or not screenDef.screenDefinition then
            return nil
        end

        local screenType = screenDef.screenDefinition:ComputerScreenType()
        if not screenType then
            return nil
        end

        local wallpaperDef = TweakDBInterface.GetWidgetDefinitionRecord(
            TweakDBID.new("DevicesUIDefinitions.ComputerWallpaperWidget"))
        local ratioRecord = nil

        if wallpaperDef and wallpaperDef:UseContentRatio() then
            ratioRecord = screenType:ContentRatio()
        end
        if not ratioRecord then
            ratioRecord = screenType:Ratio()
        end

        if not ratioRecord then
            return nil
        end
        return parseAspectName(ratioRecord:EnumName())
    end

    local file = io.open("count.json", "r")
    if not file then
        print("count.json 读取失败")
        return
    end
    local config = file:read("*a")
    file:close()
    total = tonumber(config:match('"count"%s*:%s*(%d+)')) or tonumber(config:match('%d+')) or 1

    local aspectList = config:match('"aspects"%s*:%s*%[([^%]]*)%]')
    if aspectList then
        for value in aspectList:gmatch("[%d%.]+") do
            local aspect = tonumber(value)
            if aspect then
                table.insert(videoAspects, aspect)
            end
        end
    end

    Observe('ComputerInkGameController', 'ShowMainMenu', function(self)
        print("进入主菜单")
        if not self.mainLayout or not self.mainLayout.logicController then
            return
        end
        local wallpaper = self.mainLayout.logicController.wallpaper
        if wallpaper then
            local key = Game.NameToString(wallpaper.name)
            if key then
                local video = videoWidgets[key]
                if video then
                    video:Play()
                    local index = videoIndexes[key]
                    local screenAspect = getScreenAspect(self)
                    local sourceAspect = index and videoAspects[index + 1] or nil
                    local scaleX = 1.0
                    local scaleY = 1.0

                    if screenAspect and sourceAspect and sourceAspect > 0 then
                        if screenAspect > sourceAspect then
                            scaleY = screenAspect / sourceAspect
                        else
                            scaleX = sourceAspect / screenAspect
                        end
                    end

                    video:SetRenderTransformPivot(Vector2.new({ X = 0.5, Y = 0.5 }))
                    video:SetScale(Vector2.new({ X = scaleX, Y = scaleY }))
                    print(string.format(
                        "DynamicWallpaper aspect: screen=%.4f source=%.4f scale=%.4f,%.4f",
                        screenAspect or 0.0,
                        sourceAspect or 0.0,
                        scaleX,
                        scaleY))
                end
            end
        end
    end)

    Observe('ComputerInkGameController', 'OnUninitialize', function(self)
        print("电脑脱离加载")
        if not self.mainLayout or not self.mainLayout.logicController then
            return
        end
        local wallpaper = self.mainLayout.logicController.wallpaper
        if wallpaper then
            local key = Game.NameToString(wallpaper.name)
            if key then
                local video = videoWidgets[key]
                if video then
                    video:Stop()
                    videoWidgets[key] = nil
                    videoIndexes[key] = nil
                end
            end
        end
    end)

   local idCounter = 0
local function getNextId()
    idCounter = idCounter + 1
    return "Device_" .. idCounter
end

    ObserveAfter('ComputerMainLayoutWidgetController', 'OnWallpaperSpawned', function(self, widget, userdata)
        print("壁纸生成")
        local w0 = widget.children.children[1]
        w0:SetVisible(false)

        local v = inkVideo.new()
        v:SetName('wallpaperVideo')
        local index = math.random(0,total-1)
        print(index)
        v:SetVideoPath(ResRef.FromString(string.format('dynamicwallpaper\\video\\wallpaper_%02d.bk2',index)))
        v:Reparent(widget.parentWidget, -1)
        v:SetAnchor(inkEAnchor.Fill)
        v:SetRenderTransformPivot(Vector2.new({ X = 0.5, Y = 0.5 }))
        v:SetScale(Vector2.new({ X = 1.0, Y = 1.0 }))
        v:SetLoop(true)
        v:ForceVideoFrameRate(true)
        v:SetAudioEvent(Game.StringToName(string.format("wallpaper_%02d",index)))
        v:SetSyncToAudio(true)
        
        local key = getNextId()
        self.wallpaper.name = Game.StringToName(key)
        videoWidgets[key] = v
        videoIndexes[key] = index
    end)
end)



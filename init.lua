local videoWidgets = {}
local total = 1

registerForEvent('onInit', function()
    math.randomseed(os.time())

    local file = io.open("count.json", "r")
    if not file then
        print("count.json 读取失败")
        return
    end
    total = file:read("*a"):match('%d+')
    file:close()

    Observe('ComputerInkGameController', 'ShowMainMenu', function(self)
        print("进入主菜单")
        local wallpaper = self.mainLayout.logicController.wallpaper
        if wallpaper then
            local key = Game.NameToString(wallpaper.name)
            if key then
                local video = videoWidgets[key]
                if video then
                    video:Play()   
                end
            end
        end
    end)

    Observe('ComputerInkGameController', 'OnUninitialize', function(self)
        print("电脑脱离加载")
        local wallpaper = self.mainLayout.logicController.wallpaper
        if wallpaper then
            local key = Game.NameToString(wallpaper.name)
            if key then
                local video = videoWidgets[key]
                if video then
                    video:Stop()
                    videoWidgets[key] = nil
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
        v:SetAnchor(inkEAnchor.Fill)
        local index = math.random(0,total-1)
        print(index)
        v:SetVideoPath(ResRef.FromString(string.format('dynamicwallpaper\\video\\wallpaper_%02d.bk2',index)))
        v:Reparent(widget.parentWidget, -1)
        v:SetLoop(true)
        v:ForceVideoFrameRate(true)
        v:SetAudioEvent(Game.StringToName(string.format("wallpaper_%02d",index)))
        v:SetSyncToAudio(true)
        
        local key = getNextId()
        self.wallpaper.name = Game.StringToName(key)
        videoWidgets[key] = v
    end)
end)



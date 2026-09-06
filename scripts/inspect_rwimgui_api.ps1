param(
    [string]$RainWorldDir = "D:\Application\Steam\steamapps\common\Rain World",
    [string]$RWImGuiPluginDir = ""
)

$ErrorActionPreference = "Stop"

function Resolve-RWImGuiPluginDir {
    param([string]$GameDir, [string]$Explicit)

    if ($Explicit -and (Test-Path (Join-Path $Explicit "rain-world-imgui-api.dll"))) {
        return (Resolve-Path $Explicit).Path
    }

    $steamApps = Split-Path (Split-Path $GameDir -Parent) -Parent
    $candidates = @(
        (Join-Path $steamApps "workshop\content\312520\3417372413\plugins"),
        (Join-Path $steamApps "workshop\content\312520\3417372413\rwimgui\plugins"),
        (Join-Path $GameDir "RainWorld_Data\StreamingAssets\mods\rwimgui\plugins")
    )

    foreach ($candidate in $candidates) {
        if (Test-Path (Join-Path $candidate "rain-world-imgui-api.dll")) {
            return (Resolve-Path $candidate).Path
        }
    }

    throw "RWImGUI plugin directory not found. Pass -RWImGuiPluginDir explicitly."
}

$RainWorldDir = (Resolve-Path $RainWorldDir).Path
$RWImGuiPluginDir = Resolve-RWImGuiPluginDir -GameDir $RainWorldDir -Explicit $RWImGuiPluginDir

$probeDirs = @(
    $RWImGuiPluginDir,
    (Join-Path $RainWorldDir "RainWorld_Data\Managed"),
    (Join-Path $RainWorldDir "BepInEx\core"),
    (Join-Path $RainWorldDir "BepInEx\plugins"),
    (Join-Path $RainWorldDir "BepInEx\utils")
) | Where-Object { Test-Path $_ }

$resolveHandler = [ResolveEventHandler] {
    param($sender, $args)
    $simple = ([Reflection.AssemblyName]$args.Name).Name + ".dll"
    foreach ($dir in $probeDirs) {
        $candidate = Join-Path $dir $simple
        if (Test-Path $candidate) {
            try { return [Reflection.Assembly]::LoadFrom($candidate) } catch { }
        }
    }
    return $null
}

[AppDomain]::CurrentDomain.add_AssemblyResolve($resolveHandler)

try {
    $apiPath = Join-Path $RWImGuiPluginDir "rain-world-imgui-api.dll"
    $imguiPath = Join-Path $RWImGuiPluginDir "ImGui.NET.dll"

    if (-not (Test-Path $imguiPath)) {
        throw "RWImGUI modified ImGui.NET.dll not found: $imguiPath"
    }

    # Load the exact modified binding before loading the API assembly so reflection cannot
    # accidentally bind to a NuGet/global ImGui.NET copy.
    $imgui = [Reflection.Assembly]::LoadFrom($imguiPath)
    $api = [Reflection.Assembly]::LoadFrom($apiPath)

    Write-Host "============================================================"
    Write-Host "RWImGUI API Inventory"
    Write-Host "============================================================"
    Write-Host "Plugin dir : $RWImGuiPluginDir"
    Write-Host "API        : $($api.GetName().Name) $($api.GetName().Version)"
    Write-Host "ImGui.NET  : $($imgui.GetName().Name) $($imgui.GetName().Version)"
    Write-Host ""

    $types = @()
    try {
        $types = $api.GetTypes()
    }
    catch [Reflection.ReflectionTypeLoadException] {
        $types = @($_.Exception.Types | Where-Object { $_ -ne $null })
        Write-Warning "Some RWImGUI types could not be loaded:"
        foreach ($loaderError in $_.Exception.LoaderExceptions) {
            Write-Warning ("  " + $loaderError.Message)
        }
    }

    Write-Host "[BepInPlugin attributes]"
    foreach ($type in $types) {
        foreach ($attribute in $type.CustomAttributes) {
            if ($attribute.AttributeType.FullName -eq "BepInEx.BepInPlugin") {
                $args = @($attribute.ConstructorArguments | ForEach-Object { $_.Value })
                Write-Host ("  Type={0} GUID={1} Name={2} Version={3}" -f $type.FullName, $args[0], $args[1], $args[2])
            }
        }
    }

    Write-Host ""
    Write-Host "[ImGUIAPI candidates]"
    $apiTypes = @($types | Where-Object { $_.Name -eq "ImGUIAPI" -or $_.FullName -match "ImGUIAPI" })
    if ($apiTypes.Count -eq 0) {
        Write-Warning "No type named ImGUIAPI was found."
    }
    foreach ($type in $apiTypes) {
        Write-Host ("  Type: " + $type.FullName)
        foreach ($method in ($type.GetMethods([Reflection.BindingFlags] "Public,NonPublic,Static") | Sort-Object Name)) {
            Write-Host ("    " + $method.ToString())
        }
    }

    Write-Host ""
    Write-Host "[Context-related public types]"
    foreach ($type in ($types | Where-Object { $_.IsPublic -and $_.Name -match "Context" } | Sort-Object FullName)) {
        Write-Host ("  " + $type.FullName)
        foreach ($method in ($type.GetMethods([Reflection.BindingFlags] "Public,Static,Instance") | Where-Object { $_.DeclaringType -eq $type } | Sort-Object Name)) {
            Write-Host ("    " + $method.ToString())
        }
    }

    Write-Host ""
    Write-Host "[Callback/removal/font/texture/docking keyword methods]"
    foreach ($type in $types) {
        $matches = @($type.GetMethods([Reflection.BindingFlags] "Public,Static,Instance") |
            Where-Object { $_.Name -match "Callback|Remove|Context|Font|Texture|Dock" })
        if ($matches.Count -eq 0) { continue }
        Write-Host ("  " + $type.FullName)
        foreach ($method in ($matches | Sort-Object Name)) {
            Write-Host ("    " + $method.ToString())
        }
    }

    Write-Host ""
    Write-Host "Inventory complete. Copy this output into the development log if the bridge API differs from the current proof code."
}
finally {
    [AppDomain]::CurrentDomain.remove_AssemblyResolve($resolveHandler)
}

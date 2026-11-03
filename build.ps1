param(
    [switch]$Publish,
    [switch]$SkipNative
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

function Build-NativeHook {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        throw 'vswhere.exe not found. Install Visual Studio Build Tools with the C++ workload.'
    }

    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) {
        throw 'MSVC x64 tools not found (Microsoft.VisualStudio.Component.VC.Tools.x86.x64).'
    }

    $vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
    $obj = Join-Path $env:TEMP 'inputtranslate-imehook.obj'
    $dll = Join-Path $root 'native\imehook.dll'
    $source = Join-Path $root 'native\imehook.cpp'

    Write-Host 'Building native\imehook.dll ...'
    $command = "`"$vcvars`" >nul 2>&1 && cl /nologo /LD /O2 /EHsc /DUNICODE /D_UNICODE /Fo:`"$obj`" `"$source`" /link /OUT:`"$dll`" ole32.lib user32.lib imm32.lib"
    cmd /c $command
    if ($LASTEXITCODE -ne 0) {
        throw "native build failed with exit code $LASTEXITCODE"
    }

    Remove-Item (Join-Path $root 'imehook.lib'), (Join-Path $root 'imehook.exp'),
        (Join-Path $root 'inputtranslate-imehook.lib'), (Join-Path $root 'inputtranslate-imehook.exp') `
        -Force -ErrorAction SilentlyContinue
    Write-Host ('native build ok: ' + (Get-Item $dll).Length + ' bytes')
}

if (-not $SkipNative) {
    Build-NativeHook
}

function Get-UiaRuntimeDir {
    $best = $null
    foreach ($line in (& dotnet --list-runtimes)) {
        if ($line -match '^Microsoft\.WindowsDesktop\.App\s+(9\.\d+\.\d+)\s+\[(.+)\]$') {
            $candidate = [pscustomobject]@{
                Version = [version]$matches[1]
                Dir = Join-Path $matches[2] $matches[1]
            }
            if (-not $best -or $candidate.Version -gt $best.Version) {
                $best = $candidate
            }
        }
    }
    if (-not $best) {
        throw 'Microsoft.WindowsDesktop.App 9.x runtime not found.'
    }
    return $best.Dir
}

$uiaRuntime = Get-UiaRuntimeDir
Write-Host ('UI Automation runtime: ' + $uiaRuntime)

Write-Host 'Building managed app ...'
dotnet build InputTranslate.csproj -c Release "-p:UiaRuntimeDir=$uiaRuntime"
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

if ($Publish) {
    Write-Host 'Publishing single-file build to publish\ ...'
    dotnet publish InputTranslate.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None -p:DebugSymbols=false "-p:UiaRuntimeDir=$uiaRuntime" -o publish
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    # Older builds kept imehook.dll next to the exe; it now lives in hook\.
    Remove-Item (Join-Path $root 'publish\imehook.dll'), (Join-Path $root 'publish\InputTranslate.pdb'),
        (Join-Path $root 'publish\UIAutomationClient.dll'), (Join-Path $root 'publish\UIAutomationTypes.dll'),
        (Join-Path $root 'publish\WindowsBase.dll') `
        -Force -ErrorAction SilentlyContinue
    Write-Host 'Done: publish\InputTranslate.exe'

    $version = (Select-String -Path (Join-Path $root 'InputTranslate.csproj') -Pattern '<Version>([^<]+)</Version>').Matches.Groups[1].Value
    if (-not $version) { $version = '1.0.0' }

    Write-Host 'Packing installer payload ...'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $setupDir = Join-Path $root 'setup'
    $payload = Join-Path $setupDir 'payload.zip'
    if (Test-Path $payload) { Remove-Item $payload -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        (Join-Path $root 'publish'),
        $payload,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    Write-Host 'Building installer ...'
    $installerDir = Join-Path $root 'installer'
    New-Item -ItemType Directory -Force $installerDir | Out-Null
    Get-ChildItem $installerDir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.msi', '.wxs', '.rtf' -or $_.Name -like 'InputTranslateSetup*' -or $_.Name -like 'InputTranslate-Setup-*' } |
        Remove-Item -Force

    dotnet publish (Join-Path $setupDir 'InputTranslateSetup.csproj') -c Release -r win-x64 --self-contained false `
        -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false "-p:Version=$version" -o $installerDir
    if ($LASTEXITCODE -ne 0) {
        throw "setup publish failed with exit code $LASTEXITCODE"
    }

    $setupExe = Join-Path $installerDir 'InputTranslateSetup.exe'
    $finalExe = Join-Path $installerDir ('InputTranslate-Setup-' + $version + '.exe')
    Move-Item $setupExe $finalExe -Force
    Remove-Item $payload -Force -ErrorAction SilentlyContinue
    Write-Host ('Done: ' + $finalExe)
}
else {
    Write-Host 'Done: bin\Release\net9.0-windows10.0.19041.0\InputTranslate.exe'
}

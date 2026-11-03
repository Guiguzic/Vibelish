param(
    [switch]$Publish,
    [switch]$SkipNative
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

# 新开的终端里 dotnet 不一定在 PATH 上。
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $dotnetDir = Join-Path $env:ProgramFiles 'dotnet'
    if (Test-Path (Join-Path $dotnetDir 'dotnet.exe')) {
        $env:PATH = $dotnetDir + ';' + $env:PATH
    }
    else {
        throw 'dotnet not found. Install the .NET 9 SDK: https://dotnet.microsoft.com/download/dotnet/9.0'
    }
}

$version = (Select-String -Path (Join-Path $root 'InputTranslate.csproj') -Pattern '<Version>([^<]+)</Version>').Matches.Groups[1].Value
if (-not $version) { $version = '1.0.0' }
Write-Host ('Version ' + $version)

function Find-VcVars {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) {
        return $null
    }

    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) {
        return $null
    }

    $vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
    if (Test-Path $vcvars) { return $vcvars }
    return $null
}

function Build-NativeHook {
    $dll = Join-Path $root 'native\imehook.dll'
    $vcvars = Find-VcVars
    if (-not $vcvars) {
        $message = 'MSVC x64 tools not found (Visual Studio Build Tools, "Desktop development with C++").'
        if (Test-Path $dll) {
            Write-Warning ($message + ' Using the existing native\imehook.dll.')
            return
        }
        if ($Publish) {
            throw ($message + ' The installer needs native\imehook.dll.')
        }
        Write-Warning ($message + ' Building without the IME hook; IME text capture will be unavailable.')
        return
    }

    $temp = Join-Path $env:TEMP 'inputtranslate-native'
    New-Item -ItemType Directory -Force $temp | Out-Null
    $obj = Join-Path $temp 'imehook.obj'
    $lib = Join-Path $temp 'imehook.lib'
    $source = Join-Path $root 'native\imehook.cpp'

    Write-Host 'Building native\imehook.dll ...'
    $command = "`"$vcvars`" >nul 2>&1 && cl /nologo /LD /O2 /EHsc /DUNICODE /D_UNICODE /Fo:`"$obj`" `"$source`" /link /OUT:`"$dll`" /IMPLIB:`"$lib`" ole32.lib user32.lib imm32.lib"
    cmd /c $command
    if ($LASTEXITCODE -ne 0) {
        throw "native build failed with exit code $LASTEXITCODE"
    }

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
    $publishDir = Join-Path $root 'publish'
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
    dotnet publish InputTranslate.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None -p:DebugSymbols=false "-p:UiaRuntimeDir=$uiaRuntime" -o publish
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    Remove-Item (Join-Path $publishDir 'InputTranslate.pdb'),
        (Join-Path $publishDir 'UIAutomationClient.dll'), (Join-Path $publishDir 'UIAutomationTypes.dll'),
        (Join-Path $publishDir 'WindowsBase.dll') `
        -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path (Join-Path $publishDir 'hook\imehook.dll'))) {
        throw 'publish\hook\imehook.dll is missing.'
    }
    Write-Host 'Done: publish\InputTranslate.exe'

    Write-Host 'Packing installer payload ...'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $setupDir = Join-Path $root 'setup'
    $payload = Join-Path $setupDir 'payload.zip'
    if (Test-Path $payload) { Remove-Item $payload -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $publishDir,
        $payload,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    # 安装程序编到 .NET Framework 4.8（Windows 10/11 自带），用户不用先装 .NET 运行时。
    Write-Host 'Building installer ...'
    $installerDir = Join-Path $root 'installer'
    New-Item -ItemType Directory -Force $installerDir | Out-Null
    Get-ChildItem $installerDir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.msi', '.wxs', '.rtf' -or $_.Name -like 'InputTranslateSetup*' -or $_.Name -like 'InputTranslate-Setup-*' } |
        Remove-Item -Force

    $setupOut = Join-Path $env:TEMP 'inputtranslate-setup-build'
    if (Test-Path $setupOut) { Remove-Item $setupOut -Recurse -Force }
    try {
        dotnet build (Join-Path $setupDir 'InputTranslateSetup.csproj') -c Release `
            -p:DebugType=None -p:DebugSymbols=false "-p:Version=$version" -o $setupOut
        if ($LASTEXITCODE -ne 0) {
            throw "setup build failed with exit code $LASTEXITCODE"
        }

        $finalExe = Join-Path $installerDir ('InputTranslate-Setup-' + $version + '.exe')
        Copy-Item (Join-Path $setupOut 'InputTranslateSetup.exe') $finalExe -Force
        Write-Host ('Done: ' + $finalExe + ' (' + [math]::Round((Get-Item $finalExe).Length / 1MB, 1) + ' MB)')
    }
    finally {
        Remove-Item $payload -Force -ErrorAction SilentlyContinue
        Remove-Item $setupOut -Recurse -Force -ErrorAction SilentlyContinue
    }
}
else {
    Write-Host 'Done: bin\Release\net9.0-windows10.0.19041.0\InputTranslate.exe'
}

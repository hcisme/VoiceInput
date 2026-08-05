param(
    [string]$Version = "0.0.0"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$ProjectDir = Join-Path $RepoRoot "VoiceInput"
$PublishDir = Join-Path $ProjectDir "bin/Release/net10.0-windows10.0.17763.0/win-x64/publish"
$OutputName = "VoiceInput_Setup_v${Version}.exe"

Write-Host "=== 1. dotnet publish ==="
dotnet publish "$ProjectDir/VoiceInput.csproj" `
    -f net10.0-windows10.0.17763.0 `
    -r win-x64 `
    -c Release `
    -p:DebugType=none `
    -p:Version=$Version `
    --self-contained true

Write-Host "=== 2. 生成 Inno Setup 脚本 ==="
$IssPath = Join-Path $RepoRoot "VoiceInput.iss"
$WorkIss = Join-Path $RepoRoot "build.iss"

(Get-Content $IssPath -Raw -Encoding UTF8) `
    -replace '#define MyAppVersion ".*"', "#define MyAppVersion ""${Version}""" `
    -replace '#define MyPublishDir ".*"', "#define MyPublishDir ""${PublishDir}\""" `
    -replace 'OutputDir=.*', "OutputDir=${RepoRoot}" `
    -replace 'OutputBaseFilename=.*', "OutputBaseFilename=VoiceInput_Setup_v${Version}" |
    Set-Content $WorkIss -NoNewline -Encoding UTF8

Write-Host "=== 3. 编译安装包 ==="
$Iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $Iscc)) {
    # 也检查 choco 安装的位置
    $Iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
}
if (-not $Iscc) {
    Write-Host "ISCC not found, trying choco install..."
    choco install innosetup -y --no-progress
    $Iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
}
& $Iscc $WorkIss

Write-Host "=== 完成: ${OutputName} ==="

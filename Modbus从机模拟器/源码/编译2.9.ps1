param([switch]$BuildOnly)
$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $PSScriptRoot
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$verifyDir = Join-Path $projectDir '验证'
New-Item -ItemType Directory -Path $verifyDir -Force | Out-Null
$iconPath = Join-Path $projectDir 'assets\app.ico'
$releaseExe = Join-Path $projectDir 'Modbus从机模拟器2.9.exe'
$coreFiles = @((Join-Path $PSScriptRoot 'Protocol.cs'), (Join-Path $PSScriptRoot 'Configuration.cs'), (Join-Path $PSScriptRoot 'ValueCodec.cs'), (Join-Path $PSScriptRoot 'Presentation.cs'), (Join-Path $PSScriptRoot 'CardLayout.cs'), (Join-Path $PSScriptRoot 'RawPayload.cs'))
& $compilerPath /nologo /target:winexe "/win32icon:$iconPath" "/resource:$iconPath,app.ico" /optimize+ /warnaserror+ /platform:anycpu /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll "/out:$releaseExe" @coreFiles (Join-Path $PSScriptRoot 'SerialDevices.cs') (Join-Path $PSScriptRoot 'Server.cs') (Join-Path $PSScriptRoot 'AsciiProtocol.cs') (Join-Path $PSScriptRoot 'NetworkFrames.cs') (Join-Path $PSScriptRoot 'RawSendPanel.cs') (Join-Path $PSScriptRoot 'EnhancedControls.cs') (Join-Path $PSScriptRoot 'CardView.cs') (Join-Path $PSScriptRoot 'BitControls.cs') (Join-Path $PSScriptRoot 'SidebarLayout.cs') (Join-Path $PSScriptRoot 'TransportCatalog.cs') (Join-Path $PSScriptRoot 'AreaView.cs') (Join-Path $PSScriptRoot 'DataViews.cs') (Join-Path $PSScriptRoot 'TrafficWorkspace.cs') (Join-Path $PSScriptRoot 'UiLanguage.cs') (Join-Path $PSScriptRoot 'UiTheme.cs') (Join-Path $PSScriptRoot 'CompactStatusLabel.cs') (Join-Path $PSScriptRoot 'CommunicationPreferences.cs') (Join-Path $PSScriptRoot 'AppAssets.cs') (Join-Path $PSScriptRoot 'ProjectInfo.cs') (Join-Path $PSScriptRoot 'UiLayoutPersistence.cs') (Join-Path $PSScriptRoot 'UiLayout.cs') (Join-Path $PSScriptRoot 'AppV2.cs')
if ($LASTEXITCODE -ne 0) { throw '2.9 编译失败；请确认没有运行该 EXE。' }
if ($BuildOnly) { Get-Item -LiteralPath $releaseExe | Select-Object FullName,Length; return }
$env:MODBUS_DISABLE_UI_STATE = '1'
foreach ($testName in @('ProtocolTests', 'ModelChecks', 'CodecChecks', 'PresentationChecks', 'CardLayoutChecks', 'MetadataChecks', 'RawPayloadChecks')) {
    $testExe = Join-Path $verifyDir ($testName + '.exe')
    & $compilerPath /nologo /target:exe /warnaserror+ /reference:System.Web.Extensions.dll "/out:$testExe" @coreFiles (Join-Path $PSScriptRoot ($testName + '.cs'))
    if ($LASTEXITCODE -ne 0) { throw ($testName + ' 编译失败') }
    & $testExe $verifyDir | Tee-Object -FilePath (Join-Path $verifyDir ('2.9-' + $testName + '.txt'))
    if ($LASTEXITCODE -ne 0) { throw ($testName + ' 测试失败') }
}
# 把同一发行文件复制到验证目录，集成测试加载实际发行程序集，交付目录只保留用户程序。
Copy-Item -LiteralPath $releaseExe -Destination (Join-Path $verifyDir 'Modbus从机模拟器2.9.exe') -Force
foreach ($testName in @('VerifyV2', 'ImmediateReadChecks', 'AsciiChecks', 'Verify21Checks', 'NetworkChecks', 'VerifyNetworkUiChecks', 'Verify22Checks', 'Verify23Checks', 'SerialDevicesChecks', 'VerifyZeroUiChecks', 'Settings24Checks', 'Lamp24Checks', 'Verify24Checks', 'ScrollPaintChecks', 'ScrollReuseChecks', 'ScrollCoalesceChecks')) {
    $testExe = Join-Path $verifyDir ($testName + '.exe')
    & $compilerPath /nologo /target:exe /warnaserror+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/reference:$releaseExe" "/out:$testExe" (Join-Path $PSScriptRoot ($testName + '.cs'))
    if ($LASTEXITCODE -ne 0) { throw ($testName + ' 编译失败') }
    & $testExe $verifyDir | Tee-Object -FilePath (Join-Path $verifyDir ('2.9-' + $testName + '.txt'))
    if ($LASTEXITCODE -ne 0) { throw ($testName + ' 测试失败') }
}
# 实际发行程序集的侧栏几何及缩放切换回归；不打开COM或现场端口。
$alignmentExe = Join-Path $verifyDir 'SidebarAlignmentChecks.exe'
& $compilerPath /nologo /target:exe /warnaserror+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$alignmentExe" (Join-Path $PSScriptRoot 'SidebarAlignmentChecks.cs')
if ($LASTEXITCODE -ne 0) { throw '侧栏检查编译失败' }
& $alignmentExe $releaseExe $verifyDir | Tee-Object -FilePath (Join-Path $verifyDir '2.9-SidebarAlignmentChecks.txt')
if ($LASTEXITCODE -ne 0) { throw '侧栏对齐检查失败' }
foreach ($specialName in @('QuickCardChecks','MetadataGridChecks','RawSendOrderChecks','CompactUi28Checks','LayoutMemory281Checks','CycleSendChecks','UiLanguageChecks','UiThemeChecks','AppearanceMemoryChecks','RawSendMemoryChecks','ConnectionMemoryChecks','SystemSettingsChecks','IconResourceChecks')) {
    $specialExe = Join-Path $verifyDir ($specialName + '.exe')
    & $compilerPath /nologo /target:exe /warnaserror+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$specialExe" (Join-Path $PSScriptRoot ($specialName + '.cs'))
    if ($LASTEXITCODE -ne 0) { throw ($specialName + ' 编译失败') }
    & $specialExe $releaseExe $verifyDir | Tee-Object -FilePath (Join-Path $verifyDir ('2.9-' + $specialName + '.txt'))
    if ($LASTEXITCODE -ne 0) { throw ($specialName + ' 检查失败') }
}
$digest = Get-FileHash -LiteralPath $releaseExe -Algorithm SHA256
($digest.Hash + '  Modbus从机模拟器2.9.exe') | Set-Content -LiteralPath (Join-Path $projectDir 'SHA256-2.9.txt') -Encoding UTF8
Get-Item -LiteralPath $releaseExe | Select-Object FullName,Length

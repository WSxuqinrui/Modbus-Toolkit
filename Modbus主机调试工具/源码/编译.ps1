param([switch]$BuildOnly)
$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $PSScriptRoot
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$verifyDir = Join-Path $projectDir '验证'
New-Item -ItemType Directory -Path $verifyDir -Force | Out-Null
$iconPath = Join-Path $projectDir 'assets\app.ico'
$releaseExe = Join-Path $projectDir 'Modbus主机调试工具2.9.exe'
$coreNames = @('Protocol.cs','Configuration.cs','ValueCodec.cs','Presentation.cs','CardLayout.cs','AsciiProtocol.cs','SerialDevices.cs','MasterProtocol.cs','MasterClient.cs','MasterSession.cs','MasterConfiguration.cs','RawPayload.cs')
$coreFiles = $coreNames | ForEach-Object { Join-Path $PSScriptRoot $_ }
$uiNames = @('RawSendPanel.cs','SidebarLayout.cs','TransportCatalog.cs','EnhancedControls.cs','BitControls.cs','CardView.cs','AreaView.cs','DataViews.cs','TrafficWorkspace.cs','UiLanguage.cs','UiTheme.cs','CompactStatusLabel.cs','CommunicationPreferences.cs','AppAssets.cs','ProjectInfo.cs','UiLayoutPersistence.cs','UiLayout.cs','WriteDialog.cs','MasterApp.cs')
$uiFiles = $uiNames | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compilerPath /nologo /target:winexe "/win32icon:$iconPath" "/resource:$iconPath,app.ico" /optimize+ /warnaserror+ /platform:anycpu /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll "/out:$releaseExe" @coreFiles @uiFiles
if ($LASTEXITCODE -ne 0) { throw '主机编译失败' }
Copy-Item -LiteralPath $releaseExe -Destination (Join-Path $verifyDir 'Modbus主机调试工具2.9.exe') -Force
if ($BuildOnly) { Get-Item -LiteralPath $releaseExe | Select-Object FullName,Length; return }
$env:MODBUS_DISABLE_UI_STATE = '1'
$slaveExe = Join-Path (Split-Path -Parent $projectDir) 'Modbus从机模拟器\Modbus从机模拟器2.9.exe'
foreach ($testName in @('MasterClientChecks','MasterWireChecks','SessionCheckRunner','RawPayloadChecks')) {
    $testExe = Join-Path $verifyDir ($testName + '.exe')
    $testSources = @((Join-Path $PSScriptRoot ($testName + '.cs')))
    if ($testName -eq 'SessionCheckRunner') { $testSources += (Join-Path $PSScriptRoot 'MasterSessionChecks.cs') }
    & $compilerPath /nologo /target:exe /warnaserror+ /reference:System.Web.Extensions.dll "/out:$testExe" @coreFiles @testSources
    if ($LASTEXITCODE -ne 0) { throw ($testName + ' 编译失败') }
    & $testExe $slaveExe | Tee-Object -FilePath (Join-Path $verifyDir ($testName + '.txt'))
    if ($LASTEXITCODE -ne 0) { throw ($testName + ' 测试失败') }
}
Copy-Item -LiteralPath $slaveExe -Destination (Join-Path $verifyDir 'Modbus从机模拟器2.9.exe') -Force
foreach ($testName in @('MasterViewChecks','MasterUiChecks','NewUi27Checks')) {
    $testExe = Join-Path $verifyDir ($testName + '.exe')
    & $compilerPath /nologo /target:exe /warnaserror+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/reference:$releaseExe" "/reference:$slaveExe" "/out:$testExe" (Join-Path $PSScriptRoot ($testName + '.cs'))
    if ($LASTEXITCODE -ne 0) { throw ($testName + ' 编译失败') }
    & $testExe $verifyDir $slaveExe | Tee-Object -FilePath (Join-Path $verifyDir ($testName + '.txt'))
    if ($LASTEXITCODE -ne 0) { throw ($testName + ' 测试失败') }
}
# 实际发行程序集的侧栏几何及缩放切换回归；不打开COM或现场端口。
$alignmentExe = Join-Path $verifyDir 'SidebarAlignmentChecks.exe'
& $compilerPath /nologo /target:exe /warnaserror+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$alignmentExe" (Join-Path $PSScriptRoot 'SidebarAlignmentChecks.cs')
if ($LASTEXITCODE -ne 0) { throw '侧栏检查编译失败' }
& $alignmentExe $releaseExe $verifyDir | Tee-Object -FilePath (Join-Path $verifyDir '2.9-SidebarAlignmentChecks.txt')
if ($LASTEXITCODE -ne 0) { throw '侧栏对齐检查失败' }
foreach ($specialName in @('QuickCardChecks','MetadataGridChecks','CompactUi28Checks','LayoutMemory281Checks','CycleSendChecks','UiLanguageChecks','UiThemeChecks','AppearanceMemoryChecks','RawSendMemoryChecks','ConnectionMemoryChecks','SystemSettingsChecks','IconResourceChecks')) {
    $specialExe = Join-Path $verifyDir ($specialName + '.exe')
    & $compilerPath /nologo /target:exe /warnaserror+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll "/out:$specialExe" (Join-Path $PSScriptRoot ($specialName + '.cs'))
    if ($LASTEXITCODE -ne 0) { throw ($specialName + ' 编译失败') }
    & $specialExe $releaseExe $verifyDir | Tee-Object -FilePath (Join-Path $verifyDir ($specialName + '.txt'))
    if ($LASTEXITCODE -ne 0) { throw ($specialName + ' 检查失败') }
}
# 同时编译双方纯通信核心以验证原始发送目标、队列隔离和发送锁。
$slaveSource = Join-Path (Split-Path -Parent $projectDir) 'Modbus从机模拟器\源码'
$rawBackendSlaveFiles = @('Protocol.cs','Configuration.cs','ValueCodec.cs','Presentation.cs','CardLayout.cs','AsciiProtocol.cs','NetworkFrames.cs','Server.cs') | ForEach-Object { Join-Path $slaveSource $_ }
$rawBackendExe = Join-Path $verifyDir 'RawBackendChecks.exe'
& $compilerPath /nologo /target:exe /warnaserror+ /reference:System.Web.Extensions.dll "/out:$rawBackendExe" @coreFiles @rawBackendSlaveFiles (Join-Path $PSScriptRoot 'RawBackendChecks.cs')
if ($LASTEXITCODE -ne 0) { throw 'RawBackendChecks 编译失败' }
& $rawBackendExe | Tee-Object -FilePath (Join-Path $verifyDir 'RawBackendChecks.txt')
if ($LASTEXITCODE -ne 0) { throw 'RawBackendChecks 检查失败' }
$digest = Get-FileHash -LiteralPath $releaseExe -Algorithm SHA256
($digest.Hash + '  Modbus主机调试工具2.9.exe') | Set-Content -LiteralPath (Join-Path $projectDir 'SHA256.txt') -Encoding UTF8
Get-Item -LiteralPath $releaseExe | Select-Object FullName,Length

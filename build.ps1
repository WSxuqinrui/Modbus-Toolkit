param([switch]$BuildOnly)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Modbus从机模拟器\源码\编译.ps1') -BuildOnly:$BuildOnly
& (Join-Path $PSScriptRoot 'Modbus主机调试工具\源码\编译.ps1') -BuildOnly:$BuildOnly

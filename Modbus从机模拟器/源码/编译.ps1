param([switch]$BuildOnly)
# 兼容入口：当前源码统一构建2.9，保留旧版发行EXE。
& (Join-Path $PSScriptRoot '编译2.9.ps1') -BuildOnly:$BuildOnly

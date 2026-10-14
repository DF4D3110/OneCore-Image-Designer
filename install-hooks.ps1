# 启用产品线边界守卫（本地提交前检查）
#
# 本脚本**不修改**你的全局 git 配置；它只对**当前仓库**设置钩子目录。
# 想撤销：git config --unset core.hooksPath
#
# 之所以不做成 MSBuild 目标：构建目标每台机器都要跑，而部分机器的
# ExecutionPolicy 会拦住 .ps1，会让构建无故失败。钩子只拦提交，不拦构建。

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrEmpty($root)) { $root = (Get-Location).Path }

Push-Location $root
try {
    git config core.hooksPath .githooks
    Write-Host "已启用：core.hooksPath = .githooks" -ForegroundColor Green
    Write-Host "现在每次 git commit 前会自动跑 check-product-lines.ps1"
    Write-Host "跳过单次检查：git commit --no-verify"
    Write-Host ""
    Write-Host "先手动验证一遍："
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'check-product-lines.ps1')
} finally { Pop-Location }

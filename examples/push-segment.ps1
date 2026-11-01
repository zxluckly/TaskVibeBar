# push-segment.ps1 —— 把一段状态推到 VibeBar 任务栏（外部段）
#
# 用法：
#   .\examples\push-segment.ps1 -Id demo.ps -Text "来自脚本"
#   .\examples\push-segment.ps1 -Id demo.build -Text "通过 12s" -Label 构建 -Color success -Ttl 60
#   .\examples\push-segment.ps1 -Id demo.ps -Text "3 条" -Detail "第一行|第二行"
#
# 退出码与 VibeBar.Cli.exe 一致：0 成功 / 2 参数错误 / 3 未运行 / 4 服务端拒绝。

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Id,
    [Parameter(Mandatory = $true)][string]$Text,
    [string]$Label = '',
    [string]$Color = '',
    [int]$Ttl = 0,
    [string]$Url = '',
    [string]$Detail = '',
    [string]$Cli = '',
    [switch]$ShowReply
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ($Cli) {
    $cliPath = $Cli
} else {
    $cliPath = Join-Path (Split-Path -Parent $scriptDir) 'bin\VibeBar.Cli.exe'
}

if (-not (Test-Path -LiteralPath $cliPath)) {
    [Console]::Error.WriteLine("找不到 VibeBar.Cli.exe：$cliPath（先运行 build.bat）")
    exit 3
}

$cliArgs = @('--segment', "id=$Id", "text=$Text")
if ($Label) { $cliArgs += "label=$Label" }
if ($Color) { $cliArgs += "color=$Color" }
if ($Ttl -gt 0) { $cliArgs += "ttl=$Ttl" }
if ($Url) { $cliArgs += "url=$Url" }
if ($Detail) { $cliArgs += "detail=$Detail" }
if ($ShowReply) { $cliArgs += '--verbose' }

& $cliPath @cliArgs
exit $LASTEXITCODE

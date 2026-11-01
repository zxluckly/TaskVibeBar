# napcat-qq.ps1 —— 把 Napcat（OneBot v11）事件里的未读数推到 VibeBar 任务栏
#
# 用法（事件 JSON 走参数）：
#   .\examples\napcat-qq.ps1 -EventJson '{"post_type":"message","message_type":"group","group_id":123,"sender":{"card":"小明"},"raw_message":"hi"}'
#
# 用法（事件 JSON 走管道，适合 Napcat 的 HTTP 上报转发）：
#   '{"post_type":"message","message_type":"private","sender":{"nickname":"小明"}}' | .\examples\napcat-qq.ps1
#
# 未读数取值顺序：
#   1. 事件里的 unread / unread_count / unreadCount 字段（推荐：由你的上报端聚合后带上）
#   2. 否则 post_type=message 的普通消息事件按 1 条未读计
# 事件不是消息类、没有未读数、JSON 解析失败时安静退出 0，绝不报错。

[CmdletBinding()]
param(
    [string]$EventJson = '',
    [string]$Id = 'napcat.qq',
    [int]$Ttl = 30,
    [string]$Cli = ''
)

$ErrorActionPreference = 'Stop'

if (-not $EventJson) {
    try {
        if ([Console]::IsInputRedirected) {
            $EventJson = [Console]::In.ReadToEnd()
        }
    } catch {
        exit 0
    }
}

if (-not $EventJson) { exit 0 }

$event = $null
try {
    $event = $EventJson | ConvertFrom-Json
} catch {
    exit 0
}

if ($null -eq $event) { exit 0 }

$postType = [string]$event.post_type
$messageType = [string]$event.message_type

# 心跳、通知、请求这类事件不带未读，直接安静退出。
if ($postType -and $postType -ne 'message') { exit 0 }

$unread = -1
foreach ($name in @('unread', 'unread_count', 'unreadCount')) {
    $value = $event.$name
    if ($null -eq $value) { continue }

    $parsed = 0
    if ([int]::TryParse([string]$value, [ref]$parsed)) {
        $unread = $parsed
        break
    }
}

if ($unread -lt 0) {
    if ($postType -eq 'message') {
        $unread = 1
    } else {
        exit 0
    }
}

if ($unread -le 0) { exit 0 }

$detailLines = @()
if ($messageType -eq 'group') {
    $who = [string]$event.sender.card
    if (-not $who) { $who = [string]$event.sender.nickname }
    if (-not $who) { $who = '有人' }
    $detailLines += ("群 " + [string]$event.group_id + "：" + $who)
} elseif ($messageType -eq 'private') {
    $who = [string]$event.sender.nickname
    if (-not $who) { $who = [string]$event.sender.card }
    if (-not $who) { $who = '有人' }
    $detailLines += ("私聊：" + $who)
}

if ($event.raw_message) {
    $detailLines += ([string]$event.raw_message)
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ($Cli) {
    $cliPath = $Cli
} else {
    $cliPath = Join-Path (Split-Path -Parent $scriptDir) 'bin\VibeBar.Cli.exe'
}

if (-not (Test-Path -LiteralPath $cliPath)) { exit 0 }

$cliArgs = @(
    '--segment',
    "id=$Id",
    ("text=" + $unread + " 条未读"),
    'label=QQ',
    'color=accent',
    "ttl=$Ttl"
)

if ($detailLines.Count -gt 0) {
    $cliArgs += ('detail=' + ($detailLines -join '|'))
}

& $cliPath @cliArgs
exit $LASTEXITCODE

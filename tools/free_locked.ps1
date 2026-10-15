# 사용 중이라 덮어쓸 수 없는 파일의 이름을 바꿔 길을 비운다 (모든 앱 빌드 공통 도구).
# Windows는 실행 중인 exe/dll을 덮어쓰거나 지우지는 못하지만 이름 바꾸기는 허용한다.
# 사용: powershell -NoProfile -ExecutionPolicy Bypass -File free_locked.ps1 <폴더> <파일이름> [<파일이름> ...]
#   - 쓸 수 있는 파일은 그대로 둔다. 잠긴 파일만 <이름>.old (이미 있으면 .old2, .old3...)로 바꾼다.
#   - 이전에 남은 *.old 는 지울 수 있으면 지운다 (앱이 닫혀 있을 때 정리된다).
# 종료 코드: 0 = 정상, 1 = 잠긴 파일의 이름을 바꾸지 못함
param([Parameter(Mandatory=$true)][string]$Dir, [Parameter(ValueFromRemainingArguments=$true)][string[]]$Names)
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'SilentlyContinue'
if (-not (Test-Path -LiteralPath $Dir)) { exit 0 }

# 지난번에 남은 .old 정리 (아직 실행 중이라 못 지우면 그냥 둔다)
Get-ChildItem -LiteralPath $Dir -File | Where-Object { $_.Name -match '\.old\d*$' } | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

$failed = $false; $moved = 0
foreach ($name in $Names) {
    $path = Join-Path $Dir $name
    if (-not (Test-Path -LiteralPath $path)) { continue }
    $locked = $false
    try { $fs = [System.IO.File]::Open($path, 'Open', 'ReadWrite', 'None'); $fs.Close() } catch { $locked = $true }
    if (-not $locked) { continue }
    $n = 0; $done = $false
    while (-not $done -and $n -lt 20) {
        $suffix = if ($n -eq 0) { '.old' } else { ".old$($n + 1)" }
        $target = $path + $suffix
        if (-not (Test-Path -LiteralPath $target)) {
            try { [System.IO.File]::Move($path, $target); $done = $true; $moved++ } catch { }
        }
        $n++
    }
    if (-not $done) { $failed = $true; Write-Host "이름을 바꾸지 못했습니다: $path" }
}
if ($moved -gt 0) { Write-Host "사용 중인 파일 $moved 개는 이름을 바꿔 두고 새로 만듭니다. 실행 중인 앱은 그대로 쓰이고, 다시 시작하면 새 버전이 적용됩니다." }
if ($failed) { exit 1 }
exit 0

# 몽 시리즈 공용 자동 배포 스크립트 (GitHub Actions 전용). 원본은 브랜딩\앱연동\mong_publish.ps1, 각 앱 저장소 tools\ 에 복사해 쓴다.
#
# 소스가 푸시되면 .github\workflows\build.yml 이 빌드한 뒤 이 스크립트를 부른다.
# 소스의 버전이 지금 배포된 버전(<앱ID>-latest.json)보다 높을 때만, 그 앱 저장소의 Release(태그 v1)에
# 업데이트 파일과 <앱ID>-latest.json 을 올린다. 버전이 같거나 낮으면 아무것도 올리지 않는다.
# 변경 내용(note)은 푸시한 마지막 커밋 제목을 쓴다.
#
# 예)  ./tools/mong_publish.ps1 -App mongzip -VersionFile src/AssemblyInfo.cs -VersionPattern 'AssemblyFileVersion\("(\d+\.\d+\.\d+)' `
#        -File 'dist/*-Setup.exe' -AssetName MongZip-Setup.exe -Kind setup
#      -Extra 'setup/*.exe=MongExplorer-Setup.exe' : 업데이트 파일 말고 함께 올릴 파일(새 설치용 등). 여러 개 가능.
# 필요: 환경변수 GH_TOKEN(워크플로의 github.token, contents: write). 로컬에서 쓰지 않는다.
param(
  [Parameter(Mandatory)][string]$App,
  [Parameter(Mandatory)][string]$VersionFile,
  [Parameter(Mandatory)][string]$VersionPattern,
  [Parameter(Mandatory)][string]$File,
  [Parameter(Mandatory)][string]$AssetName,
  [ValidateSet('exe','setup')][string]$Kind = 'setup',
  [string[]]$Extra = @(),
  [string]$Repo = $env:GITHUB_REPOSITORY,
  [string]$Tag = 'v1'
)
$ErrorActionPreference = 'Stop'

function Norm([string]$v) { $p = @($v.Split('.')); while ($p.Count -lt 2) { $p += '0' }; [version]($p -join '.') }

function Newest([string]$glob) {
  $f = Get-ChildItem -Path $glob -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
  if (-not $f) { throw "빌드 결과 파일이 없습니다: $glob" }
  $f
}

# 1) 소스 버전
$m = Select-String -LiteralPath $VersionFile -Pattern $VersionPattern | Select-Object -First 1
if (-not $m) { throw "버전을 찾을 수 없습니다: $VersionFile / $VersionPattern" }
$Version = $m.Matches[0].Groups[1].Value
Write-Host "앱: $App   소스 버전: $Version   저장소: $Repo ($Tag)"

# 2) 지금 배포된 버전
$work = Join-Path ([IO.Path]::GetTempPath()) ("mong-publish-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
$published = '0'
$hasRelease = $true
& gh release view $Tag -R $Repo --json tagName 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) { $hasRelease = $false }
if ($hasRelease) {
  & gh release download $Tag -R $Repo -p "$App-latest.json" -D $work --clobber 2>$null
  $cur = Join-Path $work "$App-latest.json"
  if ($LASTEXITCODE -eq 0 -and (Test-Path $cur)) {
    $published = (Get-Content -LiteralPath $cur -Raw | ConvertFrom-Json).version
    Remove-Item -LiteralPath $cur
  }
}
Write-Host "배포된 버전: $published"
$global:LASTEXITCODE = 0
if ((Norm $Version) -le (Norm $published)) {
  Write-Host "버전이 올라가지 않아 배포하지 않습니다."
  exit 0
}

# 3) 올릴 파일과 latest.json
$main = Newest $File
$assetPath = Join-Path $work $AssetName
Copy-Item -LiteralPath $main.FullName $assetPath
$sha = (Get-FileHash -LiteralPath $assetPath -Algorithm SHA256).Hash.ToLower()
$note = (& git log -1 --format=%s).Trim()
$manifest = [ordered]@{ version = $Version; kind = $Kind; file = $AssetName; sha256 = $sha; size = [int64]$main.Length; note = $note }
$jsonPath = Join-Path $work ($App + '-latest.json')
[IO.File]::WriteAllText($jsonPath, ($manifest | ConvertTo-Json -Compress), (New-Object Text.UTF8Encoding($false)))
Write-Host "json: $(Get-Content -LiteralPath $jsonPath -Raw)"

$extraPaths = @()
foreach ($e in $Extra) {
  $i = $e.LastIndexOf('=')
  if ($i -lt 1) { throw "-Extra 형식은 '경로패턴=올릴이름' 입니다: $e" }
  $src = Newest $e.Substring(0, $i)
  $dst = Join-Path $work $e.Substring($i + 1)
  Copy-Item -LiteralPath $src.FullName $dst
  $extraPaths += $dst
}

# 4) 올리기 (Release 가 없으면 만든다. v1 이 아닌 태그는 시험용이라 '최신'이 되지 않게 미리보기로 만든다)
if (-not $hasRelease) {
  $flags = @('--title', $Tag, '--notes', '')
  if ($Tag -ne 'v1') { $flags += '--prerelease' } else { $flags += '--latest' }
  & gh release create $Tag -R $Repo @flags
  if ($LASTEXITCODE -ne 0) { throw "Release 를 만들 수 없습니다: $Tag" }
}
# 새 설치용 파일과 업데이트 파일을 먼저, latest.json 을 마지막에 올린다 (앱이 json 을 먼저 보고 옛 파일을 받지 않게)
& gh release upload $Tag -R $Repo @extraPaths $assetPath --clobber
if ($LASTEXITCODE -ne 0) { throw "파일 업로드에 실패했습니다" }
& gh release upload $Tag -R $Repo $jsonPath --clobber
if ($LASTEXITCODE -ne 0) { throw "latest.json 업로드에 실패했습니다" }
Write-Host "배포했습니다: $App $Version  https://github.com/$Repo/releases/tag/$Tag"

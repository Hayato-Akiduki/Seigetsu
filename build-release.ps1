<#
  霽月のインストーラーと更新ファイルを作る。

  使い方（SeigetsuCS フォルダで PowerShell を開いて）:
    .\build-release.ps1 -Version 1.0.0            インストーラーを作るだけ（Releases フォルダにできる）
    .\build-release.ps1 -Version 1.0.1 -Upload    作って GitHub Releases に公開する

  -Upload のときは、GitHub のトークンを環境変数 GITHUB_TOKEN に入れておく（このファイルには書かない）。
    $env:GITHUB_TOKEN = "（自分で作ったトークン）"

  版の番号は、前に出した版より大きくする（例: 1.0.0 → 1.0.1）。
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$Upload
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$project = "src\Seigetsu\Seigetsu.csproj"
$publishDir = "publish"
$releasesDir = "Releases"

# 新しい版を置くリポジトリは csproj の UpdateRepositoryUrl から読む（アプリと同じ値を使うため）
$repoUrl = ([xml](Get-Content $project -Encoding UTF8)).Project.PropertyGroup.UpdateRepositoryUrl | Where-Object { $_ } | Select-Object -First 1
$repoUrl = "$repoUrl".Trim()

if ($Upload) {
    if (-not $repoUrl) { throw "Seigetsu.csproj の UpdateRepositoryUrl が空です。先に GitHub のリポジトリの URL を書いてください。" }
    if (-not $env:GITHUB_TOKEN) { throw "環境変数 GITHUB_TOKEN が空です。GitHub のトークンを入れてから実行してください。" }
}

Write-Host "== 1/4 アプリを組み立てる（.NET を中に含める）"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true -p:Version=$Version -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish に失敗しました。" }

dotnet tool restore | Out-Null

if ($repoUrl) {
    Write-Host "== 2/4 前の版を取ってくる（差分の更新ファイルを作るため。初めてのときは無くてよい）"
    dotnet vpk download github --repoUrl $repoUrl --outputDir $releasesDir
    if ($LASTEXITCODE -ne 0) { Write-Host "   前の版は見つかりませんでした（初めての公開なら問題ありません）。" }
} else {
    Write-Host "== 2/4 リポジトリが未設定なので、前の版の取得は飛ばします"
}

Write-Host "== 3/4 インストーラーと更新ファイルを作る"
dotnet vpk pack `
    --packId Seigetsu `
    --packVersion $Version `
    --runtime win-x64 `
    --packDir $publishDir `
    --mainExe Seigetsu.exe `
    --packTitle "霽月 - Seigetsu" `
    --packAuthors "Akiduki" `
    --icon "src\Seigetsu\Assets\seigetsu.ico" `
    --outputDir $releasesDir
if ($LASTEXITCODE -ne 0) { throw "vpk pack に失敗しました。" }

if ($Upload) {
    Write-Host "== 4/4 GitHub Releases に公開する"
    dotnet vpk upload github `
        --repoUrl $repoUrl `
        --token $env:GITHUB_TOKEN `
        --outputDir $releasesDir `
        --publish `
        --tag "v$Version" `
        --releaseName "霽月 $Version"
    if ($LASTEXITCODE -ne 0) { throw "vpk upload に失敗しました。" }
    Write-Host "公開しました: $repoUrl/releases/tag/v$Version"
} else {
    Write-Host "== 4/4 公開はしません（-Upload を付けると GitHub に公開します）"
}

Write-Host ""
Write-Host "できたファイル:"
Get-ChildItem $releasesDir | Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize

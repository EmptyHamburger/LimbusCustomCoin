# StrikeCoin —— 构建 / 部署
#
# 用法（PowerShell）：
#   .\build.ps1               # 编译
#   .\build.ps1 -Deploy       # 编译 + 部署到游戏的 BepInEx\plugins
#
# 说明：本工程零 NuGet 依赖（只 <Reference> 游戏自带的 interop 程序集），
# 所以用 --no-restore 构建，绕开本机 dotnet restore 的环境问题。
# obj/ 里的 project.assets.json 是从同仓库其它工程复制改造来的，
# 勿删 —— 删了就再也 restore 不出来了。

param(
    [switch]$Deploy,
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Limbus Company"
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $here "StrikeCoin.csproj"
$out  = Join-Path $here "bin\Release\net6.0\StrikeCoin.dll"

Write-Host "[1/2] 编译 ..." -ForegroundColor Cyan
& dotnet build $proj -c Release --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw "编译失败" }
if (-not (Test-Path $out)) { throw "找不到产物: $out" }
Write-Host "      产物: $out" -ForegroundColor Green

if ($Deploy) {
    if (-not (Test-Path $GameDir)) { throw "游戏目录不存在: $GameDir" }

    $running = Get-Process -Name "LimbusCompany" -ErrorAction SilentlyContinue
    if ($running) {
        throw "游戏正在运行，DLL 会被占用（Device or resource busy）。请先关掉游戏再部署。"
    }

    $plugins = Join-Path $GameDir "BepInEx\plugins"
    if (-not (Test-Path $plugins)) { New-Item -ItemType Directory -Path $plugins | Out-Null }

    Write-Host "[2/2] 部署 ..." -ForegroundColor Cyan
    Copy-Item $out $plugins -Force
    Write-Host "      -> $plugins\StrikeCoin.dll" -ForegroundColor Green
    Write-Host ""
    Write-Host "      （美术已内嵌进 DLL，插件目录不需要放任何 PNG）" -ForegroundColor Green
    Write-Host "首次运行会在 BepInEx\config 生成 com.limbusmods.strikecoin.cfg。" -ForegroundColor Yellow
    Write-Host "静态数据里把硬币的 color 写成 ORANGE / BLACK / BLUE / CYAN / WHITE（见 README.md）。" -ForegroundColor Yellow
    Write-Host "只有 ORANGE 带摧毁效果，其余四色目前是占位（配置 Effect.ActiveEffectIds）。" -ForegroundColor Yellow
}

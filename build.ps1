$ErrorActionPreference = 'Stop'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '请先安装 .NET 8 SDK 或更新版本。'
}

foreach ($name in @('App', 'Cli')) {
    $project = Join-Path $PSScriptRoot "src\CodexReset.$name\CodexReset.$name.csproj"
    $output = Join-Path $PSScriptRoot "publish\$($name.ToLowerInvariant())"

    Write-Host "正在生成 CodexReset.$name.exe ..."
    & dotnet publish $project -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None -p:DebugSymbols=false -o $output

    if ($LASTEXITCODE -ne 0) {
        throw "CodexReset.$name 发布失败，退出码：$LASTEXITCODE"
    }

    Write-Host "已生成：$(Join-Path $output "CodexReset.$name.exe")"
}

$ErrorActionPreference = 'Stop'

function Get-DotnetSdkVersion {
    param([string]$Executable)

    if (!(Test-Path -LiteralPath $Executable -PathType Leaf -ErrorAction SilentlyContinue)) { return }
    $reader = $null
    try {
        $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($Executable))
        if ($reader.ReadUInt16() -ne 0x5a4d) { return }
        $reader.BaseStream.Position = 0x3c
        $reader.BaseStream.Position = $reader.ReadUInt32()
        if ($reader.ReadUInt32() -ne 0x4550 -or $reader.ReadUInt16() -ne 0x8664) { return }
    } catch { return }
    finally { if ($reader) { $reader.Dispose() } }

    $PSNativeCommandUseErrorActionPreference = $false
    try {
        $reportedVersion = & $Executable --version 2>$null
        if ($LASTEXITCODE -eq 0) { return [version]$reportedVersion }
    } catch { return }
}

function Resolve-DotnetSdk {
    param([string]$RepositoryRoot)

    $sdkVersion = [version]'10.0.403'
    $sdkFolder = "dotnet-$sdkVersion-x64"
    $toolsRoot = $env:DEVTOOLS_ROOT
    if (!$toolsRoot) { $toolsRoot = [Environment]::GetEnvironmentVariable('DEVTOOLS_ROOT', 'User') }
    if ($toolsRoot) {
        $sharedDotnet = [IO.Path]::Combine($toolsRoot, $sdkFolder, 'dotnet.exe')
        if ((Get-DotnetSdkVersion $sharedDotnet) -eq $sdkVersion) { return $sharedDotnet }
    }
    foreach ($command in @(Get-Command dotnet.exe -CommandType Application -All -ErrorAction SilentlyContinue)) {
        $version = Get-DotnetSdkVersion $command.Source
        if ($version -ge $sdkVersion -and $version.Major -eq $sdkVersion.Major) { return $command.Source }
    }

    $toolchain = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'artifacts/toolchain'))
    $localSdk = Join-Path $toolchain $sdkFolder
    $localDotnet = Join-Path $localSdk 'dotnet.exe'
    if (Test-Path -LiteralPath $localSdk) {
        if ((Get-DotnetSdkVersion $localDotnet) -eq $sdkVersion) { return $localDotnet }
        throw "项目 SDK 目录已存在但不可用，请改名后重试。$localSdk"
    }
    foreach ($directory in @((Split-Path -Parent $toolchain), $toolchain)) {
        if ((Test-Path -LiteralPath $directory) -and ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "项目 SDK 下载目录不能是链接。$directory"
        }
    }

    $stage = New-Item -ItemType Directory -Path (Join-Path $toolchain ('sdk-' + [guid]::NewGuid().ToString('N'))) -Force
    try {
        $archive = Join-Path $stage.FullName 'sdk.zip'
        $expanded = Join-Path $stage.FullName 'sdk'
        $ProgressPreference = 'SilentlyContinue'
        Write-Host "正在下载并校验 .NET SDK $sdkVersion x64。"
        Invoke-WebRequest -Uri "https://dotnetcli.blob.core.windows.net/dotnet/Sdk/$sdkVersion/dotnet-sdk-$sdkVersion-win-x64.zip" -OutFile $archive -UseBasicParsing
        $expectedHash = '7a114c318fa7f1d9822991b534b2814d0e78386ceda816b371f5141acefe741d87ccb606ede06cadac8f9ac5070e32b708a67bbd1619ff5e5a7858a27874ef48'
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $expectedHash) {
            throw '.NET SDK ZIP 的 SHA512 校验失败。'
        }
        Expand-Archive -LiteralPath $archive -DestinationPath $expanded
        if ((Get-DotnetSdkVersion (Join-Path $expanded 'dotnet.exe')) -ne $sdkVersion) { throw '下载的 .NET SDK 不可用。' }

        $resolvedSdk = (Resolve-Path -LiteralPath $expanded).ProviderPath
        if ((Split-Path -Parent $resolvedSdk) -ne $stage.FullName -or (Split-Path -Parent $localSdk) -ne $toolchain) {
            throw '项目 SDK 安装路径超出工具目录。'
        }
        try { [IO.Directory]::Move($resolvedSdk, $localSdk) }
        catch { if ((Get-DotnetSdkVersion $localDotnet) -ne $sdkVersion) { throw } }
        return $localDotnet
    } finally {
        $directory = Get-Item -LiteralPath $stage.FullName -Force
        if ($directory.Parent.FullName -ne $toolchain -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw '项目 SDK 临时目录超出工具目录。' }
        Remove-Item -LiteralPath $directory.FullName -Recurse -Force
    }
}

Push-Location -LiteralPath $PSScriptRoot
try {
    $dotnetExecutable = Resolve-DotnetSdk $PSScriptRoot
    Write-Host "使用 .NET SDK：$dotnetExecutable"
    foreach ($name in @('App', 'Cli')) {
        $project = Join-Path $PSScriptRoot "src\CodexReset.$name\CodexReset.$name.csproj"
        $output = Join-Path $PSScriptRoot "publish\$($name.ToLowerInvariant())"

        Write-Host "正在生成 CodexReset.$name.exe ..."
        & $dotnetExecutable publish $project -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:DebugType=None -p:DebugSymbols=false -o $output

        if ($LASTEXITCODE -ne 0) {
            throw "CodexReset.$name 发布失败，退出码：$LASTEXITCODE"
        }

        Write-Host "已生成：$(Join-Path $output "CodexReset.$name.exe")"
    }
} finally {
    Pop-Location
}

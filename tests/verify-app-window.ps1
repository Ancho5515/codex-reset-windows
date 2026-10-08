param(
    [string]$Executable = (Join-Path $PSScriptRoot '..\publish\app\CodexReset.App.exe')
)

$ErrorActionPreference = 'Stop'
$tempRoot = [IO.Path]::GetTempPath().TrimEnd('\')
$testDirectory = Join-Path $tempRoot ('codex-reset-window-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDirectory | Out-Null
$process = $null

try {
    # 使用无法连接的测试后台，验证窗口可在连接失败时正常出现。
    $stub = Join-Path $testDirectory 'codex.cmd'
    Set-Content -LiteralPath $stub -Value '@exit /b 1' -Encoding ASCII
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = (Resolve-Path -LiteralPath $Executable).Path
    $start.WorkingDirectory = $testDirectory
    $start.UseShellExecute = $false
    $start.EnvironmentVariables['CODEX_HOME'] = $testDirectory
    $start.EnvironmentVariables['CODEX_CLI_PATH'] = $stub
    $process = [Diagnostics.Process]::Start($start)

    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    do {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
    } while (-not $process.HasExited -and $process.MainWindowHandle -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)

    if ($process.HasExited -or $process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw 'FAIL: 启动后未出现主窗口。'
    }

    Write-Output "PASS: 后台不可用时主窗口仍正常出现，标题：$($process.MainWindowTitle)"
} finally {
    if ($process -and -not $process.HasExited) {
        & "$env:SystemRoot\System32\taskkill.exe" /PID $process.Id /T /F | Out-Null
        if ($LASTEXITCODE -ne 0 -and -not $process.HasExited) { throw '测试进程未能退出。' }
    }
    if ($process) { $process.WaitForExit(); $process.Dispose() }
    $resolved = (Resolve-Path -LiteralPath $testDirectory).Path
    if (-not $resolved.StartsWith($tempRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolved) -like 'codex-reset-window-*')) {
        throw '测试临时目录路径不符合清理范围。'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

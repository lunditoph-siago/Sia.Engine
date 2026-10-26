[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$sourceDirectory = [System.IO.Path]::GetFullPath($PSScriptRoot)
$repositoryDirectory = [System.IO.Path]::GetFullPath((Join-Path $sourceDirectory '../../..'))
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
if ($output.Equals($repositoryDirectory, [System.StringComparison]::OrdinalIgnoreCase) -or
    $output.StartsWith($repositoryDirectory + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'Choose an explicit build output directory outside the Engine checkout.'
}
foreach ($path in @($sourceDirectory, $output)) {
    if ($path.IndexOfAny([char[]]@('"', '%', "`r", "`n")) -ge 0) {
        throw 'Windows compiler command paths must not contain quotes, percent signs or line breaks.'
    }
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
if ((Get-Item -LiteralPath $output).Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
    throw 'The build output directory must not be a directory link.'
}
$objectDirectory = Join-Path $output 'obj'
New-Item -ItemType Directory -Path $objectDirectory -Force | Out-Null

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'Visual Studio Installer vswhere.exe was not found.' }
$discoveryOutput = Join-Path $output 'vswhere.stdout.log'
$discoveryError = Join-Path $output 'vswhere.stderr.log'
$discovery = Start-Process -FilePath $vswhere -ArgumentList @('-latest', '-products', '*', '-requires', 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64', '-property', 'installationPath') `
    -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $discoveryOutput -RedirectStandardError $discoveryError
if ($discovery.ExitCode -ne 0) { throw "Visual Studio discovery failed with exit code $($discovery.ExitCode)." }
$installation = (Get-Content -LiteralPath $discoveryOutput -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($installation) -or $installation.IndexOfAny([char[]]@('"', '%', "`r", "`n")) -ge 0) {
    throw 'A single Visual Studio C++ installation path was not found.'
}
$developerCommand = Join-Path $installation 'Common7/Tools/VsDevCmd.bat'
if (!(Test-Path -LiteralPath $developerCommand -PathType Leaf)) { throw 'The Visual Studio C++ developer command was not found.' }

$executable = Join-Path $output 'mikk_source.exe'
$responsePath = Join-Path $output 'mikk-compiler.rsp'
$commandPath = Join-Path $output 'build-mikk.cmd'
$objectArgument = $objectDirectory.Replace('\', '/') + '/'
$response = @(
    '/nologo', '/TC', '/std:c11', '/O2', '/fp:strict', '/W4', '/D_CRT_SECURE_NO_WARNINGS',
    ('/I"' + (Join-Path $sourceDirectory 'vendor') + '"'),
    ('/Fo"' + $objectArgument + '"'), ('/Fe"' + $executable + '"'),
    ('"' + (Join-Path $sourceDirectory 'mikk_source.c') + '"'),
    ('"' + (Join-Path $sourceDirectory 'vendor/mikktspace.c') + '"'),
    '/link', '/INCREMENTAL:NO'
)
[System.IO.File]::WriteAllLines($responsePath, $response, [System.Text.UTF8Encoding]::new($false))
$command = @(
    '@echo off', 'setlocal DisableDelayedExpansion',
    ('call "' + $developerCommand + '" -no_logo -arch=x64 -host_arch=x64'),
    'if errorlevel 1 exit /b %errorlevel%',
    ('cl.exe @"' + $responsePath + '"'), 'exit /b %errorlevel%'
)
[System.IO.File]::WriteAllLines($commandPath, $command, [System.Text.UTF8Encoding]::new($false))
$stdout = Join-Path $output 'build.stdout.log'
$stderr = Join-Path $output 'build.stderr.log'
$arguments = '/d /s /c ""' + $commandPath + '" > "' + $stdout + '" 2> "' + $stderr + '""'
$start = [System.Diagnostics.ProcessStartInfo]::new($env:ComSpec, $arguments)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WorkingDirectory = $output
$compiler = [System.Diagnostics.Process]::new()
$compiler.StartInfo = $start
try {
    # Own the root command handle and status. Start-Process -Wait also waits for
    # persistent telemetry descendants; its detached handle can lose ExitCode.
    if (!$compiler.Start()) { throw 'Mikk compiler command did not start.' }
    $compiler.WaitForExit()
    if ($compiler.ExitCode -ne 0) { throw "Mikk compiler failed with exit code $($compiler.ExitCode); see the build output logs." }
}
finally { $compiler.Dispose() }
if (!(Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'The compiler did not produce mikk_source.exe.' }
Write-Output $executable

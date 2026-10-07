[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$CompilerPath,
    [string]$VCRedistPath,
    [string]$PackageCacheDirectory,
    [long]$BuildNumber = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds(),
    [string]$GitHubRepository = ''
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$PSBoundParameters.ContainsKey('GitHubRepository')) {
    $GitHubRepository = (Get-Content -LiteralPath (Join-Path $projectRoot 'AzurAssistant/update-source.json') -Raw | ConvertFrom-Json).GitHubRepository
}
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'out/v0.1.0' }
if (!$CompilerPath) { $CompilerPath = Join-Path $projectRoot '.tools/installer/inno/ISCC.exe' }
if (!$VCRedistPath) { $VCRedistPath = Join-Path $projectRoot '.tools/installer/vc_redist.x64.exe' }
if (!$PackageCacheDirectory) { $PackageCacheDirectory = Join-Path $projectRoot '.packages' }
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if ($BuildNumber -le 0) { throw 'BuildNumber must be a positive, increasing release identifier.' }
if ($GitHubRepository -and $GitHubRepository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid GitHub repository.' }
if ((Test-Path -LiteralPath $outputRoot) -and (Get-ChildItem -LiteralPath $outputRoot -Force | Select-Object -First 1)) {
    throw "Output directory must be empty; existing files are protected: $outputRoot"
}
if (!(Test-Path -LiteralPath $CompilerPath)) { throw 'Install Inno Setup 6.7.3 or supply -CompilerPath.' }
$signature = Get-AuthenticodeSignature -LiteralPath $VCRedistPath
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*Microsoft Corporation*') {
    throw 'VCRedistPath must be the digitally signed Microsoft x64 redistributable.'
}
# Build from an isolated snapshot so release metadata never modifies the source checkout.
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$stage = [IO.Path]::GetFullPath((Join-Path $temporaryRoot ('AzurAssistant-release-' + [Guid]::NewGuid().ToString('N'))))
$source = Join-Path $stage 'source'
$program = Join-Path $stage 'program'
$releaseDirectory = Join-Path $stage 'release'
New-Item -ItemType Directory -Path $source,$program,$releaseDirectory -Force | Out-Null
try {
    $applicationRoot = Join-Path $projectRoot 'AzurAssistant'
    foreach ($file in Get-ChildItem -LiteralPath $applicationRoot -Recurse -File) {
        $relative = [IO.Path]::GetRelativePath($applicationRoot, $file.FullName)
        if ($relative -match '^(bin|obj)[\\/]' -or $file.Extension -eq '.md' -or
            $file.Name -like '*-source.png' -or $file.Extension -in @('.pdb','.user','.suo')) { continue }
        $destination = Join-Path $source (Join-Path 'AzurAssistant' $relative)
        New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($destination)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'global.json') -Destination $source
    $buildSource = Join-Path $source 'AzurAssistant'
    @{ SchemaVersion = 1; GitHubRepository = $GitHubRepository } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $buildSource 'update-source.json') -Encoding utf8
$releaseId = "v0.1.0-beta.$BuildNumber"
$metadata = [ordered]@{ SchemaVersion=1; Product='AzurAssistant'; ProductVersion='0.1.0-beta'; BuildNumber=$BuildNumber; ReleaseId=$releaseId; Architecture='win-x64' }
$metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $buildSource 'release.json') -Encoding utf8
$cache = [IO.Path]::GetFullPath($PackageCacheDirectory)
& dotnet publish (Join-Path $buildSource 'AzurAssistant.csproj') -c Release -r win-x64 --self-contained true -o $program '-p:DebugType=None' "-p:RestorePackagesPath=$cache" "-p:RestoreConfigFile=$(Join-Path $projectRoot 'NuGet.config')"
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed; original workspace and output are unchanged.' }
# Publish contains only runtime files, no source guides or native import libraries.
foreach ($file in Get-ChildItem -LiteralPath $program -Recurse -File) {
    if ($file.Extension -in @('.pdb','.lib','.md') -or $file.Name -like '*-source.png') { Remove-Item -LiteralPath $file.FullName }
}
$programReadme = @'
蔚蓝助手 v0.1.0 beta
蓝色星原：旅谣自动化辅助

Windows 10 2004+ x64。启动 AzurAssistant.exe 时允许管理员权限；首次使用建议运行完整 EXE 安装包，以安装必要的 Visual C++ 依赖。.NET 运行环境已经内置。
在设置中选择游戏启动文件，选择需要的功能。暂停/继续默认 F9；停止默认 F10。
设置页支持自动更新：开启并重启后每天首次启动检查 GitHub，有更新时提醒。关闭提醒后可点击检查更新。当前仓库尚未配置。
手动更新：关闭助手，运行最新完整 EXE，沿用原安装目录。用户配置、委托记录、周本记录、routes 和 logs 保留。
安装后首次启动跳过自动连接/一条龙；以后正常启动按保存的设置执行。
故障日志位于 logs。配置、路线保存在本程序目录；卸载不会主动删除用户生成的数据。
当前游戏识别支持中文 1920×1080 客户区；其他分辨率、HDR、独占全屏与长期无人值守待验证，部分真实游戏流程尚待验收。
第三方许可与通知见 licenses。assets、models、各DLL为运行所需，请勿删除。
'@
[IO.File]::WriteAllText((Join-Path $program 'README.txt'), $programReadme)
$managedFiles = Get-ChildItem -LiteralPath $program -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($program, $_.FullName) } | Sort-Object
$managedFiles | Set-Content -LiteralPath (Join-Path $program 'payload-files.txt') -Encoding utf8
$installerName = "AzurAssistant-0.1.0-beta.$BuildNumber-win-x64-setup"
& $CompilerPath "/DPayloadDir=$program" "/DReleaseOutput=$releaseDirectory" "/DBuildNumber=$BuildNumber" "/DInstallerName=$installerName" "/DVCRedist=$([IO.Path]::GetFullPath($VCRedistPath))" (Join-Path $projectRoot 'installer/AzurAssistant.iss')
if ($LASTEXITCODE -ne 0) { throw 'EXE installer compilation failed.' }
$exe = Get-Item -LiteralPath (Join-Path $releaseDirectory "$installerName.exe")
$metadata.InstallerFile = $exe.Name
$metadata.InstallerSha256 = (Get-FileHash -LiteralPath $exe.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$metadata.InstallerSize = $exe.Length
$metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $releaseDirectory 'release-manifest.json') -Encoding utf8
"$($metadata.InstallerSha256)  $($exe.Name)" | Set-Content -LiteralPath (Join-Path $releaseDirectory 'SHA256SUMS.txt') -Encoding utf8
@"
蔚蓝助手 v0.1.0 beta · 构建 $BuildNumber
program/：自包含完整程序；识别资源、地图与模型为运行必需。
release/：完整 EXE 安装包、GitHub 发布清单和 SHA256。
GitHub Release 标签：$releaseId。上传 release 中 EXE 和 release-manifest.json。
GitHub 仓库：$(if ($GitHubRepository) {$GitHubRepository} else {'尚未配置'})。
不要将 program、out、个人配置或开发工作区上传到源码仓库。
"@ | Set-Content -LiteralPath (Join-Path $stage '发布说明.txt') -Encoding utf8
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
# The source tree is a build intermediate; only binaries and release assets are delivered to out.
foreach ($name in @('program','release','发布说明.txt')) {
    Move-Item -LiteralPath (Join-Path $stage $name) -Destination $outputRoot
}
Write-Output "Release completed: $outputRoot"

} finally {
    $verifiedStage = [IO.Path]::GetFullPath($stage)
    $temporaryPrefix = $temporaryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (!$verifiedStage.StartsWith($temporaryPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($verifiedStage) -notmatch '^AzurAssistant-release-[0-9a-f]{32}$') {
        throw 'Temporary build cleanup path validation failed.'
    }
    if (Test-Path -LiteralPath $verifiedStage) { Remove-Item -LiteralPath $verifiedStage -Recurse -Force }
}

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Version = "1.0.0.0",
    [string]$Publisher = "CN=Snipping Development",
    [string]$OutputDirectory = "",
    [switch]$Sign,
    [switch]$GenerateDevelopmentCertificate,
    [switch]$ForceNewDevelopmentCertificate,
    [string]$CertificatePath = "",
    [string]$CertificatePassword = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\Snipping.App\Snipping.App.csproj"
$manifestTemplate = Join-Path $repoRoot "src\Snipping.Package\AppxManifest.xml"
$artifactRoot = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $repoRoot "artifacts\msix" }
$publishDirectory = Join-Path $artifactRoot "publish"
$packageDirectory = Join-Path $artifactRoot "package"
$msixPath = Join-Path $artifactRoot "Snipping-$Version-$RuntimeIdentifier.msix"

$architecture = switch ($RuntimeIdentifier) {
    "win-arm64" { "arm64"; break }
    "win-x86" { "x86"; break }
    default { "x64" }
}
$sdkBinRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
$makeAppx = Get-ChildItem $sdkBinRoot -Recurse -Filter makeappx.exe |
    Where-Object { $_.DirectoryName -match "\\$architecture$" } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
$signTool = Get-ChildItem $sdkBinRoot -Recurse -Filter signtool.exe |
    Where-Object { $_.DirectoryName -match "\\$architecture$" } |
    Sort-Object FullName -Descending |
    Select-Object -First 1

if ($null -eq $makeAppx) { throw "未找到 makeappx.exe。请安装 Windows SDK。" }
if ($Sign -and $null -eq $signTool) { throw "未找到 signtool.exe。请安装 Windows SDK。" }

New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
if (Test-Path $publishDirectory) { Remove-Item -LiteralPath $publishDirectory -Recurse -Force }
if (Test-Path $packageDirectory) { Remove-Item -LiteralPath $packageDirectory -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publishDirectory, $packageDirectory | Out-Null

dotnet restore $project --runtime $RuntimeIdentifier
if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败。" }

dotnet publish $project `
    --configuration $Configuration `
    --runtime $RuntimeIdentifier `
    --self-contained false `
    --no-restore `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -o $publishDirectory

Copy-Item (Join-Path $publishDirectory "*") $packageDirectory -Recurse -Force
# PDB files are useful for debugging but are not runtime dependencies.
Get-ChildItem $packageDirectory -Recurse -Filter *.pdb | Remove-Item -Force
$assetsDirectory = Join-Path $packageDirectory "Assets"
New-Item -ItemType Directory -Force -Path $assetsDirectory | Out-Null

Add-Type -AssemblyName System.Drawing

function New-AppLogo {
    param([string]$Path, [int]$Size)

    $bitmap = New-Object System.Drawing.Bitmap -ArgumentList @($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $scale = $Size / 32.0

        $cardBrush = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(242, 242, 242))
        $graphics.FillRectangle($cardBrush, [int](4 * $scale), [int](4 * $scale), [int](24 * $scale), [int](24 * $scale))
        $cardBrush.Dispose()

        $accent = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(232, 83, 30))
        foreach ($point in @(@(8, 9), @(8, 15), @(8, 21), @(13, 9))) {
            $graphics.FillRectangle($accent, [int]($point[0] * $scale), [int]($point[1] * $scale), [int](4 * $scale), [int](4 * $scale))
        }
        $accent.Dispose()

        $panel = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(160, 168, 172))
        $graphics.FillRectangle($panel, [int](13 * $scale), [int](11 * $scale), [int](11 * $scale), [int](12 * $scale))
        $panel.Dispose()

        $blue = New-Object System.Drawing.Pen -ArgumentList ([System.Drawing.Color]::FromArgb(0, 132, 211)), ([float](2.2 * $scale))
        $graphics.DrawLine($blue, [float](22 * $scale), [float](22 * $scale), [float](25 * $scale), [float](25 * $scale))
        $graphics.DrawEllipse($blue, [int](24 * $scale), [int](23 * $scale), [int](6 * $scale), [int](6 * $scale))
        $blue.Dispose()

        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

New-AppLogo (Join-Path $assetsDirectory "StoreLogo.png") 50
New-AppLogo (Join-Path $assetsDirectory "Square44x44Logo.png") 44
New-AppLogo (Join-Path $assetsDirectory "Square150x150Logo.png") 150

$manifest = Get-Content -Raw $manifestTemplate
$manifest = $manifest.Replace("__PUBLISHER__", $Publisher).Replace("__VERSION__", $Version)
Set-Content -LiteralPath (Join-Path $packageDirectory "AppxManifest.xml") -Value $manifest -Encoding UTF8

if (Test-Path $msixPath) { Remove-Item -LiteralPath $msixPath -Force }
& $makeAppx.FullName pack /d $packageDirectory /p $msixPath /o
if ($LASTEXITCODE -ne 0) { throw "makeappx 打包失败。" }

if ($Sign) {
    if (-not $CertificatePath -and $GenerateDevelopmentCertificate) {
        if (-not $CertificatePassword) { $CertificatePassword = "SnippingDev-ChangeMe" }
        $CertificatePath = Join-Path $artifactRoot "Snipping-Development.pfx"
        $password = ConvertTo-SecureString $CertificatePassword -AsPlainText -Force
        $certificateFile = Join-Path $artifactRoot "Snipping-Development.cer"

        if ((Test-Path -LiteralPath $CertificatePath) -and -not $ForceNewDevelopmentCertificate) {
            try {
                $pfxData = Get-PfxData -FilePath $CertificatePath -Password $password
                $certificate = $pfxData.EndEntityCertificates | Select-Object -First 1
                if ($null -eq $certificate -or $certificate.Subject -ne $Publisher) {
                    throw "开发证书 Publisher 与当前 MSIX Publisher 不一致。"
                }
                if (-not (Test-Path -LiteralPath $certificateFile)) {
                    Export-Certificate -Cert $certificate -FilePath $certificateFile | Out-Null
                }
                Write-Host "复用现有开发证书：$CertificatePath"
            }
            catch {
                throw "无法复用现有开发证书。若需要更换证书，请删除 $CertificatePath，或使用 -ForceNewDevelopmentCertificate。原始错误：$($_.Exception.Message)"
            }
        }
        else {
            $certificate = New-SelfSignedCertificate `
                -Type CodeSigningCert `
                -Subject $Publisher `
                -FriendlyName "Snipping Development" `
                -CertStoreLocation "Cert:\CurrentUser\My"
            Export-PfxCertificate -Cert $certificate -FilePath $CertificatePath -Password $password | Out-Null
            Export-Certificate -Cert $certificate -FilePath $certificateFile | Out-Null
            Write-Host "开发证书已生成：$CertificatePath"
        }
    }
    if (-not $CertificatePath) { throw "签名需要 -CertificatePath，或同时指定 -GenerateDevelopmentCertificate。" }
    $signArguments = @("sign", "/fd", "SHA256", "/f", $CertificatePath)
    if ($CertificatePassword) {
        $signArguments += @("/p", $CertificatePassword)
    }
    $signArguments += $msixPath
    & $signTool.FullName @signArguments
    if ($LASTEXITCODE -ne 0) { throw "signtool 签名失败。" }
}

Write-Host "MSIX 已生成：$msixPath"
if (-not $Sign) { Write-Host "当前包未签名；安装前请使用 -Sign。" }

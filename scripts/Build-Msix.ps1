[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$Version = "1.0.13.0",
    [string]$Publisher = "CN=Snipping Development",
    [string]$PackageName = "Snipping.Capture",
    [string]$PublisherDisplayName = "Snipping",
    [string]$DisplayName = "Snipping",
    [string]$Description = "Lightweight Windows screenshot tool",
    [string]$StoreIconPath = "src\Snipping.Package\StoreAssets\Snipping-StoreIcon-Layers.png",
    [string]$OutputDirectory = "",
    [string]$PackageFileName = "",
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
$msixFileName = if ($PackageFileName) { $PackageFileName } else { "Snipping-$Version-$RuntimeIdentifier.msix" }
$msixPath = Join-Path $artifactRoot $msixFileName
$resolvedStoreIconPath = ""
if ($StoreIconPath) {
    $resolvedStoreIconPath = if ([IO.Path]::IsPathRooted($StoreIconPath)) {
        $StoreIconPath
    }
    else {
        Join-Path $repoRoot $StoreIconPath
    }
    if (-not (Test-Path -LiteralPath $resolvedStoreIconPath)) {
        throw "找不到商店图标源文件：$resolvedStoreIconPath"
    }
}

$architecture = switch ($RuntimeIdentifier) {
    "win-arm64" { "arm64"; break }
    "win-x86" { "x86"; break }
    default { "x64" }
}
$windowsKitBase = ${env:ProgramFiles(x86)}
if (-not $windowsKitBase) {
    $windowsKitBase = $env:ProgramFiles
}
if (-not $windowsKitBase) {
    throw "找不到 Windows SDK 安装根目录。请安装 Windows SDK 后重试。"
}

$sdkBinRoot = Join-Path $windowsKitBase "Windows Kits\10\bin"
if (-not (Test-Path -LiteralPath $sdkBinRoot)) {
    throw "找不到 Windows SDK 工具目录：$sdkBinRoot。请安装 Windows SDK。"
}

$makeAppx = Get-ChildItem $sdkBinRoot -Recurse -Filter makeappx.exe |
    Where-Object { $_.DirectoryName -match "\\$architecture$" } |
    Sort-Object FullName -Descending |
    Select-Object -First 1
$signTool = if ($Sign) {
    Get-ChildItem $sdkBinRoot -Recurse -Filter signtool.exe |
        Where-Object { $_.DirectoryName -match "\\$architecture$" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
}

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
    -p:Version=$Version `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -o $publishDirectory

Copy-Item (Join-Path $publishDirectory "*") $packageDirectory -Recurse -Force
# PDB files are useful for debugging but are not runtime dependencies.
Get-ChildItem $packageDirectory -Recurse -Filter *.pdb | Remove-Item -Force
$assetsDirectory = Join-Path $packageDirectory "Assets"
New-Item -ItemType Directory -Force -Path $assetsDirectory | Out-Null

Add-Type -AssemblyName System.Drawing

function Get-TransparentContentBounds {
    param([System.Drawing.Bitmap]$Bitmap)

    # Transparent artwork is already framed by its canvas. The old source
    # artwork is opaque, so keep its legacy crop path below.
    $corners = @(
        $Bitmap.GetPixel(0, 0),
        $Bitmap.GetPixel($Bitmap.Width - 1, 0),
        $Bitmap.GetPixel(0, $Bitmap.Height - 1),
        $Bitmap.GetPixel($Bitmap.Width - 1, $Bitmap.Height - 1)
    )
    if (-not ($corners | Where-Object { $_.A -lt 255 })) {
        return $null
    }

    $left = $Bitmap.Width
    $top = $Bitmap.Height
    $right = -1
    $bottom = -1

    for ($y = 0; $y -lt $Bitmap.Height; $y++) {
        for ($x = 0; $x -lt $Bitmap.Width; $x++) {
            if ($Bitmap.GetPixel($x, $y).A -le 8) {
                continue
            }

            $left = [Math]::Min($left, $x)
            $top = [Math]::Min($top, $y)
            $right = [Math]::Max($right, $x)
            $bottom = [Math]::Max($bottom, $y)
        }
    }

    if ($right -lt $left -or $bottom -lt $top) {
        return $null
    }

    return [System.Drawing.Rectangle]::new(
        $left,
        $top,
        $right - $left + 1,
        $bottom - $top + 1)
}

function New-ResizedPng {
    param([string]$SourcePath, [string]$TargetPath, [int]$Size)

    $source = New-Object System.Drawing.Bitmap($SourcePath)
    $target = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($target)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $transparentBounds = Get-TransparentContentBounds $source
        if ($null -ne $transparentBounds) {
            # Newer artwork is already composed on a transparent canvas. Fit
            # the complete alpha content into a square crop with a small safe
            # margin so Windows' small tile sizes never clip an edge.
            $contentSide = [Math]::Max($transparentBounds.Width, $transparentBounds.Height)
            $padding = [Math]::Max(4, [int][Math]::Ceiling($contentSide * 0.08))
            $cropSide = [int][Math]::Min(
                $contentSide + ($padding * 2),
                [Math]::Min($source.Width, $source.Height))
            $contentCenterX = $transparentBounds.X + ($transparentBounds.Width / 2.0)
            $contentCenterY = $transparentBounds.Y + ($transparentBounds.Height / 2.0)
            $cropX = [Math]::Max(0, [Math]::Min(
                $source.Width - $cropSide,
                [int][Math]::Round($contentCenterX - ($cropSide / 2.0))))
            $cropY = [Math]::Max(0, [Math]::Min(
                $source.Height - $cropSide,
                [int][Math]::Round($contentCenterY - ($cropSide / 2.0))))
        }
        else {
            # The original opaque artwork has a large outer margin and is
            # slightly offset toward the lower-right. Preserve its established
            # crop behavior while supporting transparent artwork above.
            $cropSide = [Math]::Max(1, [int][Math]::Round([Math]::Min($source.Width, $source.Height) * 0.66))
            $cropCenterX = [int][Math]::Round($source.Width * 0.546)
            $cropCenterY = [int][Math]::Round($source.Height * 0.516)
            $cropX = [Math]::Max(0, [Math]::Min($source.Width - $cropSide, $cropCenterX - [int]($cropSide / 2)))
            $cropY = [Math]::Max(0, [Math]::Min($source.Height - $cropSide, $cropCenterY - [int]($cropSide / 2)))
        }
        $sourceRect = [System.Drawing.Rectangle]::new($cropX, $cropY, $cropSide, $cropSide)
        $targetRect = [System.Drawing.Rectangle]::new(0, 0, $Size, $Size)
        $graphics.DrawImage($source, $targetRect, $sourceRect, [System.Drawing.GraphicsUnit]::Pixel)
        $target.Save($TargetPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $target.Dispose()
        $source.Dispose()
    }
}

New-ResizedPng $resolvedStoreIconPath (Join-Path $assetsDirectory "StoreLogo.png") 50
New-ResizedPng $resolvedStoreIconPath (Join-Path $assetsDirectory "Square44x44Logo.png") 44
New-ResizedPng $resolvedStoreIconPath (Join-Path $assetsDirectory "Square150x150Logo.png") 150

$manifest = Get-Content -Raw $manifestTemplate
$manifest = $manifest.Replace("__PACKAGE_NAME__", $PackageName).
    Replace("__PUBLISHER__", $Publisher).
    Replace("__PUBLISHER_DISPLAY_NAME__", $PublisherDisplayName).
    Replace("__DISPLAY_NAME__", $DisplayName).
    Replace("__DESCRIPTION__", $Description).
    Replace("__VERSION__", $Version)
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

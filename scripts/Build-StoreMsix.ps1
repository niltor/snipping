[CmdletBinding()]
param(
    [string]$ConfigurationPath = "",
    [string]$Version = "",
    [string]$PackageName = "",
    [string]$Publisher = "",
    [switch]$Sign,
    [switch]$GenerateDevelopmentCertificate,
    [switch]$ForceNewDevelopmentCertificate,
    [string]$CertificatePath = "",
    [string]$CertificatePassword = ""
)

$ErrorActionPreference = "Stop"

if (-not $ConfigurationPath) {
    $ConfigurationPath = Join-Path $PSScriptRoot "Store-Publishing.psd1"
}

if (-not (Test-Path -LiteralPath $ConfigurationPath)) {
    throw "找不到商店发布配置：$ConfigurationPath"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$config = Import-PowerShellDataFile -LiteralPath $ConfigurationPath

function Get-ConfiguredValue {
    param([hashtable]$Values, [string]$Name, [string]$Override)

    if ($Override) { return $Override }
    if (-not $Values.ContainsKey($Name) -or -not $Values[$Name]) {
        throw "商店发布配置缺少：$Name"
    }
    return [string]$Values[$Name]
}

$packageName = Get-ConfiguredValue $config "PackageName" $PackageName
$publisher = Get-ConfiguredValue $config "Publisher" $Publisher
$version = Get-ConfiguredValue $config "Version" $Version
$runtimeIdentifier = Get-ConfiguredValue $config "RuntimeIdentifier" ""

if ($packageName -like "REPLACE_WITH_*" -or $publisher -like "REPLACE_WITH_*") {
    throw "请先在 $ConfigurationPath 中填入 Partner Center 分配的 PackageName 和 Publisher。"
}

$buildScript = Join-Path $PSScriptRoot "Build-Msix.ps1"
$storeIconPath = [string]$config.StoreIconPath
if (-not $storeIconPath) { throw "商店发布配置缺少：StoreIconPath" }

$buildParameters = @{
    Configuration = Get-ConfiguredValue $config "Configuration" ""
    RuntimeIdentifier = $runtimeIdentifier
    Version = $version
    PackageFileName = "EasySnipping-Store-$version-$runtimeIdentifier.msix"
    PackageName = $packageName
    Publisher = $publisher
    PublisherDisplayName = Get-ConfiguredValue $config "PublisherDisplayName" ""
    DisplayName = Get-ConfiguredValue $config "DisplayName" ""
    Description = Get-ConfiguredValue $config "Description" ""
    StoreIconPath = Join-Path $repoRoot $storeIconPath
    OutputDirectory = Join-Path $repoRoot (Get-ConfiguredValue $config "OutputDirectory" "")
}

if ($Sign) { $buildParameters.Sign = $true }
if ($GenerateDevelopmentCertificate) { $buildParameters.GenerateDevelopmentCertificate = $true }
if ($ForceNewDevelopmentCertificate) { $buildParameters.ForceNewDevelopmentCertificate = $true }
if ($CertificatePath) { $buildParameters.CertificatePath = $CertificatePath }
if ($CertificatePassword) { $buildParameters.CertificatePassword = $CertificatePassword }

& $buildScript @buildParameters
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

# Fail the build if the package that is about to be uploaded is not the
# Partner Center package described by the publishing configuration. This
# prevents accidentally uploading the local development package from
# artifacts\msix, which has a different identity and may be stale.
$outputDirectory = Join-Path $repoRoot (Get-ConfiguredValue $config "OutputDirectory" "")
$packageDirectory = Join-Path $outputDirectory "package"
$msixPath = Join-Path $outputDirectory ([string]$buildParameters.PackageFileName)
$manifestPath = Join-Path $packageDirectory "AppxManifest.xml"
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Store 包生成后找不到清单：$manifestPath"
}
if (-not (Test-Path -LiteralPath $msixPath)) {
    throw "Store 包生成后找不到 MSIX：$msixPath"
}

[xml]$storeManifest = Get-Content -LiteralPath $manifestPath -Raw
$actualPackageName = [string]$storeManifest.Package.Identity.Name
$actualPublisher = [string]$storeManifest.Package.Identity.Publisher
$actualPublisherDisplayName = [string]$storeManifest.Package.Properties.PublisherDisplayName
$actualLanguages = @($storeManifest.Package.Resources.Resource | ForEach-Object { [string]$_.Language })

if ($actualPackageName -ne $packageName) {
    throw "Store 包 Package/Identity/Name 不匹配：$actualPackageName；期望：$packageName"
}
if ($actualPublisher -ne $publisher) {
    throw "Store 包 Publisher 不匹配：$actualPublisher；期望：$publisher"
}
$expectedPublisherDisplayName = [string]$buildParameters.PublisherDisplayName
if ($actualPublisherDisplayName -ne $expectedPublisherDisplayName) {
    throw "Store 包 PublisherDisplayName 不匹配：$actualPublisherDisplayName；期望：$expectedPublisherDisplayName"
}
if ($actualLanguages.Count -eq 0 -or $actualLanguages -notcontains "en-US") {
    throw "Store 包未声明 en-US 资源语言。"
}

foreach ($assetName in @("StoreLogo.png", "Square44x44Logo.png", "Square150x150Logo.png")) {
    $assetPath = Join-Path $packageDirectory (Join-Path "Assets" $assetName)
    if (-not (Test-Path -LiteralPath $assetPath)) {
        throw "Store 包缺少清单引用的图像：Assets\$assetName"
    }
}

Write-Host "Store 包清单验证通过：$manifestPath"
Write-Host "Store MSIX：$msixPath"
Write-Host "请上传该文件，不要上传 artifacts\msix 下的开发包。"
exit 0

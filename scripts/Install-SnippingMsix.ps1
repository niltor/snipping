[CmdletBinding()]
param(
    [string]$MsixPath = "",
    [string]$CertificatePath = "",
    [switch]$InstallMachineCertificate
)

$ErrorActionPreference = "Stop"

if (-not $MsixPath) {
    $artifactRoot = Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\msix"
    $latestPackage = Get-ChildItem -LiteralPath $artifactRoot -Filter "Snipping-*.msix" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $latestPackage) {
        throw "未找到 MSIX。请先运行 scripts\Build-Msix.ps1 生成安装包，或通过 -MsixPath 指定文件。"
    }
    $MsixPath = $latestPackage.FullName
}

if (-not $CertificatePath) {
    $defaultCertificatePath = Join-Path (Split-Path -Parent $MsixPath) "Snipping-Development.cer"
    if (Test-Path -LiteralPath $defaultCertificatePath) {
        $CertificatePath = $defaultCertificatePath
    }
}

if (-not (Test-Path -LiteralPath $MsixPath)) { throw "找不到 MSIX：$MsixPath" }

if ($CertificatePath) {
    if (-not (Test-Path -LiteralPath $CertificatePath)) { throw "找不到证书：$CertificatePath" }
    if ([IO.Path]::GetExtension($CertificatePath) -ne ".cer") {
        throw "安装信任证书时请传入 .cer 公钥文件，而不是 .pfx 私钥文件。"
    }
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$needsCertificateInstall = $InstallMachineCertificate

if ($CertificatePath -and -not $InstallMachineCertificate) {
    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        (Resolve-Path -LiteralPath $CertificatePath).Path)
    $isSelfSigned = $certificate.Subject -eq $certificate.Issuer
    $machineCertificate = Get-ChildItem Cert:\LocalMachine\TrustedPeople |
        Where-Object Thumbprint -eq $certificate.Thumbprint |
        Select-Object -First 1
    $needsCertificateInstall = $isSelfSigned -and $null -eq $machineCertificate
}

if ($needsCertificateInstall) {
    if (-not $CertificatePath) {
        throw "首次安装开发证书时必须传入 -CertificatePath .cer 文件。"
    }

    if (-not $isAdministrator) {
        $sudo = Get-Command sudo.exe -ErrorAction SilentlyContinue
        if ($null -eq $sudo) {
            throw "当前 PowerShell 不是管理员，且找不到 sudo.exe。请手动以管理员身份运行脚本。"
        }

        # Re-run elevated only when the signing certificate is not trusted by
        # the device yet. Once imported, normal updates stay per-user.
        $childArguments = @(
            "-NoProfile",
            "-ExecutionPolicy", "Bypass",
            "-File", $PSCommandPath,
            "-MsixPath", (Resolve-Path -LiteralPath $MsixPath).Path,
            "-CertificatePath", (Resolve-Path -LiteralPath $CertificatePath).Path,
            "-InstallMachineCertificate"
        )
        & $sudo.Source powershell.exe @childArguments
        exit $LASTEXITCODE
    }

    # Windows App Installer checks the device-level Trusted People store for
    # package signing certificates. The certificate is imported once and is
    # reused by subsequent builds.
    Import-Certificate -FilePath $CertificatePath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
}

try {
    Add-AppxPackage -Path (Resolve-Path -LiteralPath $MsixPath).Path
}
catch {
    if ($_.Exception.Message -match "0x80073CFB") {
        throw "MSIX 版本号与已安装版本相同但内容不同。请提高 Build-Msix.ps1 的 -Version 后重新打包。原始错误：$($_.Exception.Message)"
    }
    if (-not $InstallMachineCertificate) {
        throw "MSIX 安装失败。若这是该开发证书首次安装，请传入 -CertificatePath .cer；脚本会通过 sudo 自动提权并只在首次导入证书时需要管理员权限。原始错误：$($_.Exception.Message)"
    }
    throw
}
Write-Host "MSIX 安装完成。"

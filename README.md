# Snipping

一个面向 Windows 的轻量截图工具，支持区域截图、智能选区、标注、OCR、导出、复制和置顶贴图。

## 安装

优先推荐从 Microsoft Store 安装，商店版本由 Microsoft 签名并可通过商店更新：

[从 Microsoft Store 安装 Snipping](https://apps.microsoft.com/detail/9PMB0CL15X1B?hl=en-us&gl=SG&ocid=pdpshare)

如果需要测试当前源码或进行离线部署，请按下面的本地 MSIX 流程操作。

## 项目结构

```text
src/
  Snipping.Core/       与平台无关的核心模型和逻辑：截图结果、设置、OCR、导出
  Snipping.App/        当前正式桌面入口：WinForms 托盘、截图覆盖层、编辑器和 OCR
  Snipping.WinUI/      独立的 WinUI 版本，使用 Snipping.WinUI.slnx 构建
  Snipping.Package/    MSIX 清单和商店图标等打包资源
tests/
  Snipping.Core.Tests/ 核心逻辑测试
  Snipping.App.Tests/  Windows 桌面应用相关测试
scripts/
  Build-Msix.ps1       生成本地开发 MSIX
  Install-SnippingMsix.ps1
                       安装本地 MSIX，并在需要时导入开发证书
  Build-StoreMsix.ps1  生成并校验提交 Microsoft Partner Center 的商店包
  Store-Publishing.psd1
                       商店包身份、版本和资源配置
docs/                  功能说明、产品设计和商店发布资料
```

默认解决方案 `Snipping.slnx` 包含正式桌面应用、核心库和测试；`Snipping.WinUI.slnx` 只包含核心库和独立的 WinUI 项目。

## 环境要求

- Windows 10 1809 或更高版本；
- .NET 10 SDK；
- Windows SDK，其中需要 `makeappx.exe`；使用签名参数时还需要 `signtool.exe`；
- PowerShell 5.1 或 PowerShell 7。

脚本默认生成 `win-x64` 包。其他架构可通过 `-RuntimeIdentifier` 指定，但应分别验证对应设备上的运行效果。

## 编译和测试

在仓库根目录执行：

```powershell
dotnet restore .\Snipping.slnx
dotnet build .\Snipping.slnx --configuration Release --no-restore
dotnet test .\Snipping.slnx --configuration Release --no-restore
```

`dotnet build` 只负责编译，不会生成可双击安装的 MSIX。需要本地安装包时使用下面的打包脚本。

## 生成并安装本地 MSIX

### 1. 生成开发包

下面的命令会发布 `Snipping.App`、生成 MSIX，并创建用于本机测试的开发证书：

```powershell
pwsh -ExecutionPolicy Bypass -File .\scripts\Build-Msix.ps1 `
  -Configuration Release `
  -RuntimeIdentifier win-x64 `
  -Version 1.0.13.1 `
  -Sign `
  -GenerateDevelopmentCertificate
```

输出位于 `artifacts/msix/`，主要文件包括：

- `Snipping-1.0.13.1-win-x64.msix`：本地安装包；
- `Snipping-Development.cer`：用于信任开发签名的公钥证书；
- `Snipping-Development.pfx`：开发签名私钥文件，仅供本机使用，不要分发或提交到仓库。

版本号必须高于已安装版本，或者先卸载旧开发包，否则 Windows 可能返回“版本相同但内容不同”。

### 2. 安装开发包

```powershell
pwsh -ExecutionPolicy Bypass -File .\scripts\Install-SnippingMsix.ps1 `
  -MsixPath .\artifacts\msix\Snipping-1.0.13.1-win-x64.msix `
  -CertificatePath .\artifacts\msix\Snipping-Development.cer
```

首次安装自签名开发包时，脚本会请求管理员权限，把证书导入本机 `TrustedPeople`。之后更新同一开发证书签名的包通常不需要再次导入证书。

也可以让安装脚本自动选择 `artifacts/msix/` 中最近生成的 `Snipping-*.msix`：

```powershell
pwsh -ExecutionPolicy Bypass -File .\scripts\Install-SnippingMsix.ps1
```

### 3. 直接发布目录（可选）

如果只需要一个可复制的发布目录，不需要 MSIX 安装和开始菜单注册，可以执行：

```powershell
dotnet publish .\src\Snipping.App\Snipping.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained false `
  -o .\artifacts\publish
```

将 `artifacts/publish/` 整个目录复制到目标 Windows 设备后运行 `Snipping.App.exe`。这种方式要求目标设备已安装相应的 .NET 运行时；需要标准安装、卸载和开始菜单集成时，应使用 MSIX。

## 相关说明

- 正式桌面应用入口是 `src/Snipping.App/Program.cs`；
- 当前功能和已知限制以 [`docs/feature-list-zh-CN.md`](docs/feature-list-zh-CN.md) 为准；
- `artifacts/`、`bin/` 和 `obj/` 是构建输出，不应提交到仓库；
- Windows UI、捕获性能、多显示器和 DPI 场景仍需要在真实 Windows 环境中进行手工验收，单元测试不能替代这些验证。

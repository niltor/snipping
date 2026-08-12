# Microsoft Store 发布准备

当前项目使用命令行生成 MSIX，商店发布不需要把开发自签名证书提交给用户。微软商店会在审核通过后重新签名 MSIX。

发布资料已经整理到：

- [中文商店文案](StoreListing-zh-CN.md)
- [English Store listing](StoreListing-en-US.md)
- [发布清单](StoreSubmissionChecklist.md)
- [中文隐私政策草稿](PrivacyPolicy-zh-CN.md)
- [English privacy policy draft](PrivacyPolicy-en-US.md)

## 资源

商店图标位于：

- `src/Snipping.Package/StoreAssets/Snipping-StoreIcon-Layers.png`

构建脚本会从 `scripts/Store-Publishing.psd1` 的 `StoreIconPath` 读取该图标。

## 首次配置

1. 在 Partner Center 注册开发者账号并保留应用名称。
2. 获取 Partner Center 分配的 `Package/Identity Name` 和 `Publisher`。
3. 编辑 `scripts/Store-Publishing.psd1`，替换：

   - `PackageName`
   - `Publisher`

   两个值必须与 Partner Center 提供的值完全一致。

## 生成上传包

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build-StoreMsix.ps1
```

输出目录为 `artifacts/store`。该包适合提交到 Partner Center；不要把开发证书、PFX 或私钥放入包内。

如果是本地安装测试，可以继续使用现有开发证书流程；开发证书不适合公开分发。

## Partner Center 侧还需要准备

- 应用名称和发布者显示名；
- 中英文应用描述；
- 应用图标；
- 至少一张应用截图，建议准备 1366×768 或更高分辨率；
- 隐私政策 URL；
- 支持页面或支持邮箱；
- 年龄分级和分类；
- Windows 10 1809 及以上、x64 架构的兼容性说明。

商店列表中的宣传截图和包内图标是两类资源：本目录中的图标用于包资源和商店图标，实际应用截图需要在 Partner Center 的商店列表中单独上传。

# Pcl2Unlocker — PCL2 发行版 MC 下载限制自动解除工具

自动反编译 Plain Craft Launcher 2 发行版（编译后的 exe），定位所有引用
`ModSecret.RestrictMcDownload` 常量的代码位置，取反/恢复相关判断逻辑，
输出解除 MC 下载限制后的可执行文件。**原始文件不会被修改**，输出为新文件。

## 写在前面

这个项目纯属无聊丢给AI跑出来的，传github上留个档，不会再维护的。

## 快速开始

本仓库只包含源码，单文件版通过构建获得（自包含 win-x64，约 70MB，无需安装 .NET）：

**方式一：GitHub Actions（推荐）**

推送代码后，在 Actions 页面手动触发 **Build Pcl2Unlocker**，在运行详情页下载
Artifact `Pcl2Unlocker-win-x64`，其中即包含单文件版 `Pcl2Unlocker.exe`。

**方式二：本地构建**

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
# 产物为 publish/Pcl2Unlocker.exe
```

**双击运行（自动模式）**：把构建出的 `Pcl2Unlocker.exe` 放到 PCL2 发行版所在的文件夹，双击运行——
工具会自动找到同目录下的 PCL2 发行版 exe 并解除限制，生成 `<原名>.unlocked.exe`（原文件不改）。
运行结束后会暂停等待按键，方便查看结果。

**命令行用法**（可拷贝到任何地方使用）：

```bash
Pcl2Unlocker.exe "Plain Craft Launcher 2.exe"                # 解除限制
Pcl2Unlocker.exe "Plain Craft Launcher 2.exe" --dry-run      # 只分析不修改
Pcl2Unlocker.exe "Plain Craft Launcher 2.exe" --sites C      # 只恢复下载列表加载器
```

两种模式完全等价：不带参数 = 自动模式（处理本工具所在目录下的 PCL2），带参数 = 命令行模式。

从源码直接运行（需要 .NET 8 SDK）：

```bash
dotnet run -- "Plain Craft Launcher 2.exe"
```

## 输出

| 文件 | 说明 |
|------|------|
| `<原名>.unlocked.exe` | 补丁后的可执行文件（默认输出路径） |
| `<输出>.report.md` | 分析报告：每个站点的编译后形态、处理动作、恢复后的 IL 反汇编、验证结果 |

## 工作原理

`RestrictMcDownload` 是 VB `Const`，编译时被**内联**到每个引用点，且发行版以优化模式构建，
`If <Const> Then ...` 被常量折叠：

- 判断为**真**的分支被保留；判断为**假**的分支（即解除限制后本应执行的代码）被作为
  **不可达代码整体消除**；
- 三元运算符 `If(RestrictMcDownload, Collapsed, Visible)` 被折叠为单一常量；
- `RestrictMcDownload AndAlso X` 被折叠为 `X`。

因此无法简单翻转某个开关——工具的做法是：

1. **反编译定位**：解析 CLI 元数据，按方法名 + 锚点字符串（如限制提示
   "为遵守相关法律法规…"）定位每个站点在编译后的实际形态；
2. **IL 重写补回**：对被消除的逻辑，用 IL 重写恢复（复用程序集中仍然存活的现有引用，
   例如幸存的 `Dialogs.SelectFile` 调用点、现存的 `InstallPack*` 方法）；
3. **取反幸存分支**：对折叠成常量的位置做常量取反（如 `Visibility.Collapsed` → `Visible`）；
4. **验证**：重新读取输出文件，逐站点校验 IL 形态（含参数类型检查），并可用 ilspycmd
   等工具反编译复核。

## 处理的站点（以 2.99.0.0 为例）

| # | 源码位置 | 编译后形态 | 处理 |
|---|----------|-----------|------|
| A | `ModModpack.vb:11` | `If True Then Hint+Return`，文件对话框代码被消除 | 合成 SelectFile 对话框 + RunInThread |
| B | `ModModpack.vb:88` | 无条件 Throw，Select Case 分发被消除 | 恢复 switch 分发到 7 个 InstallPack* 方法 |
| C | `PageDownloadInstall.xaml.vb:17` | 仅保留"关闭下载"LoaderTask | 替换为 `DlClientListLoader`（**下载功能核心**） |
| D | `PageDownloadModify.vb:315` | 三元运算符折叠为 Collapsed | 取反为 Visible |
| E | `PageDownloadCompDetail.xaml.vb:275` | `Not True AndAlso ...` 恒 False，安装按钮分支被消除 | 恢复条件分支与两种委托构造 |
| F | `FormMain.xaml.vb:1079` 等 | `True AndAlso` 已折叠为直接比较 | 无需处理（补丁后该异常不再产生） |

## 设计要点

- **不依赖硬编码偏移**：按元数据名 + 锚点定位，换版本（如 2.14 → 2.99）同样适用；
  形态不符时会跳过对应站点并在报告中说明，不会静默产出错误结果。
- **复用现存引用**：合成 IL 时使用的类型/方法引用全部取自程序集中幸存的调用点，
  不引入对外部 DLL 的解析依赖。
- **内存桩解析器**：Cecil 写回时需解析个别可选参数的默认枚举（PCLCS.ModLoaders、
  MeloongCore.LogLevel），工具提供仅含枚举桩的内存程序集，保证常量字节与原文件一致。
- **幂等**：对已补丁的文件重复运行会识别为"已恢复"，输出字节级一致。
- **PE 完整性**：图标、清单、版本信息（.rsrc）字节级保留；仅主程序集的 IL 被改写，
  不触碰引用的外部程序集。

## 局限

- 被消除的代码是依据本仓库同步的源码语义重放的；若发行版与源码偏差较大，
  对应站点会被跳过（报告中会注明原因）。
- 更新日志中的"删除：为遵守相关法律法规…"仅为展示文本，不影响功能，未做修改。
- 建议在完整发行版目录（含全部文件）中替换原 exe 后使用。

## 开发

```bash
dotnet build          # 构建
dotnet run -- <exe>   # 运行
```

源码结构：`Program.cs`（CLI/报告/验证）、`Unlocker.cs`（站点定位与补丁）、
`IlTools.cs`（IL 工具）、`StubAssemblyResolver.cs`（内存桩解析器）。
依赖：Mono.Cecil（IL 读写）、ICSharpCode.Decompiler（反编译片段）。

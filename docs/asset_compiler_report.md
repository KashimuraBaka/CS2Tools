# CS2 资产编译 WPF 工具与编译调用链结论（t13）

> 交付：`projects\CS2-Tools\AssetCompiler\`（WPF 工具本体）+ `projects\CS2-Tools\CompileHost\`（进程外编译 worker）+ `projects\CS2-Tools\SDK\`（DLL 调用层；`Data\AssetTypesIndex.cs` 按用户终态指示只保留四个分类枚举）。
> 依赖结论：t11（KV3 解析 API 定案）、t12（编译调用链实证：CreateInterface 跑通 / AssetConfig_Init 与 GenerateResourceFile 在裸进程 AV）。
> 关联：`assettypes_common_compileridentifier_report.md`（t24）· `assettypes_common_config_report.md`（A 的 t10）· `kv3_parse_api_report.md`（t11）。

## 1. 核心结论（与 t24/t25 同级的真实逆向结论）

**裸进程无法驱动 `resourcecompiler.dll` 的编译路径——编译器缺引擎服务图。**

实证链（t12 复验 + t13 复现）：

| 步骤 | 结果 |
|---|---|
| `LoadLibraryW(resourcecompiler.dll)` | 成功，base `0x7FFC144A0000` |
| `GetProcAddress("CreateInterface")` | 成功，`0x7FFC163655B0` |
| `CreateInterface("ResourceCompilerSystem001", null)` | **成功** → `0x7FFC17522360`（t12/t13 两次一致） |
| 注册表只读解析（表头 RVA `0x3078730`） | **成功**，43 条，index0 = `CompileWorldNode` |
| `AssetConfig_Init` | **AV `0xC0000005`**——配置加载器 `LoadAssetTypesCommonTxt` 内部经间接槽调用引擎服务 |
| `GenerateResourceFile` | **AV `0xC0000005`**——内部按扩展名取 assettype 需要 `this+88` 的配置对象（上一步没加载出来） |
| `CreateInterface` 对其余 22 个引擎接口名 | 全部返回 NULL ⇒ 缺失服务必须由引擎宿主提供 |

⇒ 这不是失败，而是一条可断言的边界：**本 DLL 只能被引擎宿主驱动**；外部要么用官方
`resourcecompiler.exe` 配完整 mod 上下文，要么自己在进程里把服务图搭起来。

## 2. 交付物与自检结果

### 2.1 工程

```
projects\CS2-Tools\AssetCompiler\
├── AssetCompiler.csproj   net10.0-windows + UseWPF + x64 + AllowUnsafeBlocks
├── App.xaml(.cs)             + --selftest 无头自检
├── MainWindow.xaml(.cs)      配置加载 / 类型表 / subassettypes / 编译器解析 / 编译 / 日志
├── Models.cs / Kv3ConfigService.cs / RegistryService.cs / CompileService.cs
├── CompileHost\              进程外编译 worker（独立工程）
└── README.md
```

`dotnet build -c Release` 结果为 **0 warning / 0 error**。

### 2.2 无头自检（`AssetCompiler.exe --selftest`，exit 0，ALL PASS）

> **诚实性补注（t15 追加）**：本节与 §2.1 的结论（build 0 警告 0 错误 + 自检 ALL PASS）只覆盖
> **非渲染路径**；`--selftest` 提前 `Shutdown()`、不创建 MainWindow，因此**不触发 WPF 文本排版**。
> **GUI 启动路径的验证在此之后才补充**（见 §6，t15 修复了启动崩溃）。

```
PASS  KV3 header  ::  encoding=text(e21c7f3c-...) format=generic(7412167c-...)
PASS  file size  ::  32212 B / 1374 lines
PASS  assettypes rows  ::  actual 71 (expect 71, incl. quoted key 3dsmax)
PASS  subassettypes rows  ::  actual 8 (expect 8, separate section)
PASS  asset_warnings  ::  1 group(s), title [Legacy Compiled Data]
PASS  entries with compiler  ::  actual 29 (expect 29 = 28 resolvable + 1 miss)
PASS  quoted key present  ::  3dsmax is in the table
      _class distribution: CResourceAssetTypeInfo 35(28 w/ compiler) | CSimpleAssetTypeInfo 27 |
                            CBitmapAssetTypeInfo 8 | CMapAssetTypeInfo 1(1 w/ compiler)
PASS  self-check  ::  names 71/71, compilers 71/71, only-CSV 0, only-KV3 0, value-diff 0, CSV=29 KV3=29
PASS  registry entries  ::  actual 43 (table RVA 0x3078730, index0 self-check passed)
PASS  CreateInterface + RTTI  ::  sys=0x7FFC17522360
PASS  index0 identifier  ::  idx 0 = CompileWorldNode
PASS  resolvable entries  ::  hit 28 / 29 (expect 28)
PASS  missed entries  ::  miss 1 / 29 (expect 1)
      missed: resource_remap_table -> CompileResourceRemapTable
      distinct level: 26/27 hit (CompilePanorama 3 entries -> 1 distinct)
===== SELFTEST: ALL PASS =====
```

### 2.3 进程外 worker（CompileHost）两级实测

| 模式 | 退出码 | 结果 |
|---|---|---|
| `--mode init` | **0** | `CreateInterface -> 0x7FFC17522360`；注册表 43 条，index0 = CompileWorldNode |
| `--mode compile`（probe_minimal.vdata） | **9** | 如实回报阻塞：编译路径需引擎服务图；建议改用官方 exe + 完整 mod 上下文 |

原始输出存档于 `temp\t49-probes\assetcompilerwpf-selftest.out.txt`、`compilehost-init.out.txt`、
`compilehost-compile.out.txt`（temp 可被清空；结论已固化在本报告与工具 README）。

## 3. 关键设计决策

### 3.1 编译执行必须进程外

.NET 对 `AccessViolationException` 是 **fail-fast（不可 catch）**。t12 实证编译路径在裸进程必 AV，
若在 UI 进程内直调会连 GUI 一起崩。编译一律走 `CompileHost` 子进程：它崩了只是退出码，
UI 照常存活并把「子进程被原生异常终止」作为**真实结果**上报，不隐藏。

### 3.2 注册表：禁用锚点库旧 RVA，工厂指针实时读

* 锚点库 `build_sha256=d1115f9f` 不等于盘上 `204E16E5`（游戏 10-17 更新）⇒ helper 的 `ValidateBuild`
  会抛异常（这正是它该做的），故不用 `ReadCompilerRegistry` / `ResolveCompilerFactoryByString`。
* 表头 RVA **实测未变**（`0x3078730`，43 条标识符与 t24 逐位相同）；
* 但**工厂指针非均匀平移**：实测 +0x1C0 / +0x90 / +0x110 / 0 混存（idx0 工厂 `0x180162CF0` vs
  t24 `0x180162B30` = +0x1C0；idx2 `0x180109B50` vs `0x180109AC0` = +0x90；
  idx40 `0x1800F8690` vs `0x1800F8690` = 0）⇒ **绝不可硬编码工厂 RVA**，必须从表里实时读。
* 表头 RVA 仍做**运行时自校验**（index0 必须读到 `CompileWorldNode`），不符即明确报错。
* 进程内**不做** RTTI 全链读取：t13 首版按 x64 COL/TD 偏移直读导致 `AccessViolationException`
  打穿了 GUI（自检进程崩）。偏移口径随构建漂移太危险，已移除；RTTI 验证由 t12 在隔离探针完成。

### 3.3 UI 计数口径

* 主表 **71** 行（含引号键 `3dsmax`）；`subassettypes` **8** 条独立标签页，不并入主表、不隐藏。
* 带编译器 **29** = 可解析 **28** + 未命中 **1**（`resource_remap_table` → `CompileResourceRemapTable`）。
* 28 个可解析条目跨 **26 distinct**（`CompilePanorama` 3→1）。UI 用**条目级（28/29）**，
  distinct 级（26/27）另列，两口径不混用。
* 未命中时优雅降级：显示「Resource type compiler identifier not found」语义 + 未命中原因，
  不崩溃、不显示空指针；编译面板对该类型禁用或给出未命中原因。

## 4. 踩坑记录（后续复用）

1. `EnableDefaultCompileItems=false` 会连带关掉 ImplicitUsings 的 GlobalUsings——
   `NativeStringFunctionInvoker.cs` 自身没写 `using System.IO`，会报一堆 CS0103。
   解法：显式 `<Using Include="System.IO" />` 等（不改共享 helper、不分叉）。
2. WPF 工程的默认 `**/*.cs` glob 会把子工程 `CompileHost\obj\` 下 SDK 生成的
   `AssemblyInfo.cs` / `.NETCoreApp,Version=v10.0.AssemblyAttributes.cs` 一并编入，
   导致 TargetFramework/AssemblyVersion 等特性重复（CS0579 × 9）。
3. `Application` 没有 `ExitCode` 属性（那是 `Environment.ExitCode`）。
4. `CompileHost.exe` 单独跑要连带 `runtimeconfig.json` / `deps.json`，否则
   报 `hostpolicy.dll required to execute the application was not found`。
5. 从探针输出反统计时必须锚定条目头特征（t11 教训）：把 `m_Blocks` 数组元素行
   `[1] key=<null:array-element> Collection(2)` 误当条目头，会从 71 虚报成 209。

## 5. 复现

```powershell
dotnet build projects\CS2-Tools\AssetCompiler\AssetCompiler.csproj -c Release
projects\CS2-Tools\AssetCompiler\bin\Release\net10.0-windows\AssetCompiler.exe --selftest

# 进程外 worker
$dll  = "F:\SteamLibrary\...\game\bin\win64\resourcecompiler.dll"
$anch = "tools\anchors\compiler-identifier-anchors.json"
.\CompileHost.exe --dll $dll --anchors $anch --mode init
.\CompileHost.exe --dll $dll --anchors $anch --source <file.vdata> --mode compile
```

Tier2（官方 CLI）需完整 mod 上下文：`resourcecompiler.exe -i <file> -game <moddir> -nop4 -v`；
t12 实测 `GAME` 与 `MOD` 同目录会报 `Failed to split full path`，须 **GAME 不等于 MOD** 布局。

---

## 6. 修订记录：t15 修复 GUI 启动崩溃（InvariantGlobalization x WPF 字体缓存）

### 6.1 现象

队长 2026-10-19 复现：**不带 `--selftest` 的 GUI 启动模式**进程以 `exit=-532462766`（`0xE0434352` 未处理异常）退出；
`--selftest` 模式仍 ALL PASS——因为后者不走 WPF 渲染路径，所以 t13 交付时未暴露。
B 复现确认（修复前）：exit code 完全一致，stderr 9719 B。

### 6.2 根因

```
System.TypeInitializationException: The type initializer for 'MS.Internal.FontCache.MajorLanguages' threw an exception.
 ---> System.Globalization.CultureNotFoundException: Only the invariant culture is supported in globalization-invariant mode.
en is an invalid culture identifier.
   at System.Globalization.CultureInfo..ctor(String name, Boolean useUserOverride)
   at MS.Internal.FontCache.MajorLanguages..cctor()
   at MS.Internal.FontCache.MajorLanguages.Contains(ScriptTags script, LanguageTags langSys)
   at MS.Internal.FontCache.FontFaceLayoutInfo.ComputeTypographyAvailabilities()
   at System.Windows.Media.Typeface.CheckFastPathNominalGlyphs(...)
   at MS.Internal.TextFormatting.SimpleRun.CreateSimpleTextRun(...)
   at MS.Internal.TextFormatting.TextFormatterImp.FormatLine(...)
   at System.Windows.Controls.TextBoxView.MeasureOverride(Size constraint)
```

触发链：WPF 文本排版（`TextFormatterImp -> SimpleRun -> FontFaceLayoutInfo.ComputeTypographyAvailabilities`
-> `FontCache.MajorLanguages.Contains`）需要**真实 `CultureInfo`**；而工程设置了
`<InvariantGlobalization>true</InvariantGlobalization>` ⇒ 布局测量期间每个文本元素都抛一次。
实际触发点是日志面板那个 `TextBox`（`TextWrapping=Wrap` + `FontFamily=Consolas`）的 `MeasureOverride`。

**背景**：该配置项是 t13 从 `tools\csharp\InvokerSmokeTest`（控制台工程，那里安全）照搬来的。
WPF（`net10.0-windows` + `UseWPF`）与 `InvariantGlobalization=true` 不兼容是 .NET 已知问题。

### 6.3 修法

`projects\CS2-Tools\AssetCompiler\AssetCompiler.csproj`：

```xml
<!-- WPF 必须关闭 globalization-invariant 模式（t15 修复，勿改回 true）： ... -->
<InvariantGlobalization>false</InvariantGlobalization>
```

显式写 `false` 而**不是删行**，并附注释说明理由（WPF 字体/文本排版需要全局化；该配置只适用于控制台工程），
让后来者看到原因。

**CompileHost 保持不变（仍为 `InvariantGlobalization=true`）**，判断依据：

1. 它是**控制台工程**（`OutputType=Exe`、无 `UseWPF`），不触发布局/字体路径；
2. 队长与 B 均已实测 `init` exit 0 / `compile` exit 9 正常（t13 + t15 两次）；
3. 共享 helper `NativeStringFunctionInvoker.cs` 的文化相关调用全部与文化无关：
   `Marshal.PtrToStringAnsi`（字节直解）、`Convert.ToHexString`（十六进制直出）、
   `Convert.ToUInt64(s, 16)`（显式基数）、`SHA256.HashData`（纯字节）——没有依赖当前区域性的格式转换；
4. invariant 模式对 helper 是一层**更严格**的文化无关性回归保护，保留它只有收益、没有风险。

`tools\csharp\InvokerSmokeTest\InvokerSmokeTest.csproj` **未动**（控制台回归门禁，invariant 是其既定配置且工作正常）。

### 6.4 验证（全部亲自跑）

| # | 验证项 | 结果 |
|---|---|---|
| 1 | `dotnet build -c Release`（两个工程） | **0 警告 / 0 错误** |
| 2 | GUI 启动模式（`Start-Process`，8 秒后由我杀掉） | **进程存活**，`MainWindowTitle = CS2 Asset Compiler (ValveKeyValues + resourcecompiler.dll)`；stdout 0 B、stderr 0 B，无未处理异常 |
| 3 | `--selftest` | **ALL PASS，exit 0**（71 类型 / 43 注册表 / 28+1 解析 / CSV 自校验 71-71） |
| 4 | `CompileHost --mode init` | **exit 0**，CreateInterface -> 系统对象、注册表 43 条 |
| 5 | `CompileHost --mode compile`（probe_minimal.vdata） | **exit 9**，`stage=compile-path`，错误串与修复前逐字一致 |

原始输出存档：`temp\t49-probes\wpf-gui-before.{out,err}.txt`、`wpf-gui-after.{out,err}.txt`、
`wpf-selftest-after-fix.out.txt`、`compilehost-{init,compile}-after-fix.out.txt`。

### 6.5 教训

**「控制台自检 ALL PASS」不等于「GUI 能起来」**——WPF 的渲染/排版路径只在窗口真正创建时才被执行。
以后交付 WPF 类工具，必须额外做一次「无参启动 + 进程存活」验证，不能只跑无头自检。

---

## 7. 修订记录（t16）：移除三方库 ValveKeyValues，改用 tier0.dll 原生 KV3 API

### 7.1 变更

| 项 | 前（t13/t15） | 后（t16） |
|---|---|---|
| KV3 解析 | NuGet `ValveKeyValue` 0.72.0.534（仅 net10.0） | `Tier0Kv3Service`：真实调用 tier0.dll `?LoadKV3Text_NoHeader` + 自包含 KV3 读取器 |
| csproj | `<PackageReference Include="ValveKeyValue" … />` | **已删除**；无任何三方 KV 库 |
| CompileHost 定位 | 只在自己输出目录找 ⇒ Debug 下 Tier1 不可用 | `ResolveHostExe()` 候选路径 + csproj `ProjectReference` + 构建后复制 ⇒ **Debug/Release 均可用** |

### 7.2 架构图

```
assettypes_common.txt (KV3 text)
        |
        v
 Tier0Kv3Service.ProbeNative()  -----> tier0.dll
        |   ?LoadKV3Text_NoHeader          (LoadLibraryW + GetProcAddress)
        |   bit0=1 对象 + 全文             返回 False + 引擎错误串
        |   （只读探针，不建完整树）        "Line 1: Invalid value <!--"
        v
 Tier0Kv3Service.Parse()  ------------>  自包含 KV3 文本读取器
        |   key = value / [ ] / flag:value / 三引号多行串
        v
 Dictionary<string, Kv3Value>  -------->  Models（AssetTypeRow / SubAssetTypeRow）
        |
        v
 （t22 终态：索引数据与自校验已按用户指示移除——配置自身即正本）
```

### 7.3 阻塞点（供 t18）

完整原生建树需要 `CKV3Arena`，其构造函数尚未 RE 到。已确认：`Root()=arena+120`（需 `arena[4400] & 4`）、
对象头 `bit0=1` 无 arena / 否则 `arena=*(obj-(2*N+6)*8)`、`CUtlBuffer` 布局、`CUtlString` 首 qword=char*。
零填充 arena + 上述两处写入能让 Root 与头解码通过，但建树分配仍 `0xC0000005` ⇒ arena 内还有必填分配器状态。
详见 `kv3_parse_api_report.md` §8.2。

### 7.4 验证（全部亲自跑）

| 项 | 结果 |
|---|---|
| `dotnet build -c Release` / `Debug` | 均 **0 警告 0 错误** |
| `--selftest` | **ALL PASS exit 0**；71 类型 / 3 顶层键 / subassettypes 8 / asset_warnings 1 / 29 带编译器（28+1）/ distinct 26-27 |
| GUI 启动冒烟 | 8 秒存活，标题 `CS2 Asset Compiler (tier0 KeyValues + resourcecompiler.dll)`；stdout/stderr 均 0 B |
| CompileHost init / compile | exit **0** / exit **9**（stage=compile-path，错误串与 t16 前逐字一致） |
| Tier1 可达性 | Release 与 Debug 输出目录均有 CompileHost.exe（csproj 复制目标 + ResolveHostExe 双保险） |
| `tools\check-naming.ps1` | 614 篇 / 128 不合规 = 既有基线 |


---

## 8. 修订记录（t18）：DLL 调用层抽到独立 SDK 工程 projects\CS2-Tools\SDK

### 8.1 变更

| 项 | 前（t13-t16） | 后（t18） |
|---|---|---|
| 共享 helper | `tools\csharp\NativeStringFunctionInvoker.cs`，被两个工程 Compile Include | **整体迁入** `projects\CS2-Tools\SDK\Native\`（唯一副本，勿留两份） |
| tier0 KV3 加载 | `projects\CS2-Tools\AssetCompiler\Tier0Kv3Service.cs` | `projects\CS2-Tools\SDK\Tier0\Tier0Kv3Service.cs`（+ `Kv3Value.cs`） |
| 编译注册表 | `projects\CS2-Tools\AssetCompiler\RegistryService.cs`（含 P/Invoke） | `projects\CS2-Tools\SDK\ResourceCompiler\RegistryService.cs` |
| 编译调用面 | 无（RVA 常量散落注释） | `projects\CS2-Tools\SDK\ResourceCompiler\CompilerInterop.cs`（RVA 常量 + 委托签名 + 隔离边界说明） |
| CompileHost | `projects\CS2-Tools\AssetCompiler\CompileHost\` | `projects\CS2-Tools\CompileHost\`（独立 Exe，ProjectReference SDK） |
| AssetCompiler 引用 | Compile Include 共享 helper | ProjectReference `..\SDK\SDK.csproj` |
| InvokerSmokeTest | Compile Include 共享 helper | ProjectReference SDK；TFM net8.0 升到 net10.0-windows |

### 8.2 架构

```
AssetCompiler (WinExe, UI)          InvokerSmokeTest (Exe, 回归门禁 T1-T7)
        | ProjectReference                       | ProjectReference
        v                                        v
+-------------------------------------------------------------+
| SDK (net10.0-windows 类库, x64, AllowUnsafeBlocks)       |
|  Native\NativeStringFunctionInvoker.cs                       |
|  Tier0\Tier0Kv3Service.cs · Tier0\Kv3Value.cs                |
|  ResourceCompiler\RegistryService.cs · CompilerInterop.cs    |
+-------------------------------------------------------------+
        ^ ProjectReference
        |
+-------+----------------+
| SDK\CompileHost       |  独立 Exe，隔离子进程（原生 AV 只杀它）
+------------------------+
```

UI 层只面对 SDK 托管 API；`AssetCompiler` 里的 `RegistryService` / `Kv3ConfigService`
退化为薄封装（适配行模型 + 保留友好错误语义），不再出现 P/Invoke。

### 8.3 验证（全部亲自跑）

| 项 | 结果 |
|---|---|
| SDK build Release / Debug | 0 警告 0 错误 |
| AssetCompiler build Release / Debug | 0 警告 0 错误；CompileHost.exe 均 staged=True |
| `--selftest` | ALL PASS exit 0：71 类型 / 3 顶层键 / subassettypes 8 / 43 注册表 / 28+1 解析 / distinct 26-27 |
| GUI 冒烟 | 8 秒存活，标题 `CS2 Asset Compiler (tier0 KeyValues + resourcecompiler.dll)`；stdout/stderr 0 B |
| CompileHost（新位置） | init exit 0；compile exit 9、stage=compile-path、错误串逐字一致 |
| InvokerSmokeTest | T1-T4 全 PASS；T5/T6/T7 因 anchors `build_sha256` 过期而按设计抛 `NativeInvokeException`（**非本次回归**，见下） |
| GameTracking-CS2 KV3 语料 | 9 个文件全部解析成功（最大 `server.kv3` 3.1 MB / 12535 表 / 20478 数组 / 52187 标量） |
| check-naming | 614 篇 / 128 不合规 = 既有基线 |

#### InvokerSmokeTest T5-T7 说明（重要，防误判为回归）

T5/T6/T7 走 `ValidateBuild` → `ReadCompilerRegistry` 路径，会先校验 anchors 的
`build_sha256=d1115f9f…` 与盘上 `204e16e5…` 是否一致。二者因游戏 10-17 更新已不一致，
于是抛 `NativeInvokeException: 构建哈希不匹配` —— **这正是 ValidateBuild 该做的**，
与 t16 之前的行为完全相同，不是 t18 引入的回归。
T1-T4（不依赖 anchors 的纯导出调用 / 对照路径 / 封送 / RVA 解析）全 PASS。

### 8.4 踩坑（t18 新踩，3 条）

1. **net8.0 工程无法 ProjectReference net10.0-windows 库**（NU1201）。
   InvokerSmokeTest 原是 net8.0；随 SDK 一并升到 net10.0-windows。
2. **InvokerSmokeTest 的 anchors 回退路径失效**：原写 `H:\...\CS2-IDA\anchors\...`，
   实际已迁到 `tools\anchors\...`（搬迁遗留）。已修正。
3. **UI 侧 SDK 类型需显式 using**：`RegistrySnapshot` / `RegistryEntryRow` / `Kv3Value`
   现在都在 `SDK.*` 命名空间，`MainWindow.xaml.cs` 与 `App.xaml.cs` 要补 using。



---

## 9. 修订记录（t19）：CKV3Arena 构造 RE —— 原生建链未打通（负面结论如实收口）

### 9.1 结论

**tier0 原生 KV3 建链（`LoadKV3(arena,…) → SaveKV3AsJSON`）在本次未打通。**
`Root()` 与对象头解码已通过，但建树阶段仍 `0xC0000005`；arena 的**最小构造集未能定出**。
按任务书授权「若最终仍差最后一个字段啃不动：如实收口」，保留 t16 的中间态
（tier0 探针做格式/头行校验 + 自包含读取器），三方库未回退。

### 9.2 t19 新增的实证事实（IDA 反编译 + 探针）

| 事实 | 值 / 来源 |
|---|---|
| 文本解析器读的 arena 字段 | `sub_180219220 = *(QWORD*)(arena + 4408)`，被 `sub_1800D3D50` 在两处调用 |
| loader 用的 arena 访问器 | `sub_18008C420(arena) = arena + 8`（被 3 个 KV3 loader 调用） |
| 节点分配机制 | `sub_180222FC0` 走 **`UtlVectorMemory_Alloc`**（全局分配器），**不是** arena bump pointer |
| `sub_18021AAF0` | 校验 `obj` 是否落在 arena 的节点区：节点 64 B stride，从 `v2[5]`（= `arena+40`）起，上限 `2*((DWORD*)(v2+4))`（= `arena+16` 的 DWORD） |
| `CUtlMemoryArena` 符号 | tier0 确有 `??0CUtlMemoryArena@@QEAA@I_N@Z` / `?Alloc@CUtlMemoryArena@@QEAAPEAEI@Z` / `?Free@…` / `MemoryBegin` / `MemoryEnd` / `MaxAvailableAllocSize`，但**未导出**（RTTI 名在 `.rdata`，vtable 未定位） |

### 9.3 探针结果（三个变体全部 AV）

在 t16 基座（arena 零填充 64 MB + `arena[4400]=0x04` + `*(arena+72)=arena`）之上追加：

| 变体 | 追加写入 | 结果 |
|---|---|---|
| `v1` | `arena+40`=bump ptr、`arena+16`=0x10000、`arena+8`=块数组、`arena+4408`=arena 自指针 | **0xC0000005** |
| `v2` | 只给 `arena+40` + `arena+16` | **0xC0000005** |
| `v3` | 只给 `arena+4408` | **0xC0000005** |

⇒ 猜测的偏移不是真正的分配器状态。`sub_180222FC0` 的证据表明节点分配走全局分配器，
那么 AV 更可能来自 **arena 内的字符串表/符号表**（`arena+4408` 指向的子对象）未被正确构造，
或 arena 的 vtable/类型标记缺失。

### 9.4 为什么这次没啃下来（诚实的限制说明）

1. **`CUtlMemoryArena` 的 vtable 未能定位**：`??0CUtlMemoryArena@@` 等 RTTI 名字符串在 `.rdata` 中被引用，
   但我未能从 RTTI COL 反查到 vtable 与构造函数实体（PowerShell 侧 PE 解析多次因节表映射/对齐偏差失败，
   IDA 侧 `xrefs_to` 对这些 RTTI 数据地址返回空）。
2. **arena 头字段语义只能猜**：`arena+4408` / `arena+8` / `arena+40` / `arena+16` 的语义是从 3 个反编译片段
   拼出来的推断，没有构造函数可对照；三个变体全 AV 说明推断不完整。
3. **没有可直接调用的 arena 工厂**：`LoadKV3FromFile(arena 版)` 需要全局文件系统（`qword_1803A9608`），
   裸进程里为 NULL；`CreateDisposableKeyValuesSystem` 体系里未找到 arena 工厂。

### 9.5 给后续的明确线索（按优先级）

1. **反编译 `??0CUtlMemoryArena@@QEAA@I_N@Z` 本体**（先解决 vtable 定位）：
   用 IDA 的 `Names`/`Functions` 窗口直接搜 `CUtlMemoryArena`，或对
   `?Alloc@CUtlMemoryArena@@QEAAPEAEI@Z` 名字符串地址做 `xrefs_to` 找 COL，再 `COL+8` 取 vtable，
   从 vtable 反推 ctor（本任务因工具侧节表映射屡次失败未走通，这条路径本身是有效的）。
2. **从 arena 分配点反推**：`sub_1800D3570`（`sub_1800D4440` 里调它）与 `sub_18021E080`
   是字符串/节点入 arena 的入口，反编译它们看从 arena 头读哪些字段。
3. **换个思路：让引擎自己建 arena**。若能找到 tier0 内部「先建 arena 再调 `LoadKV3(arena 版)`」的调用方
   （`0x180124910` 的 callers 为空，说明只被导出表引用 ⇒ 真实调用方在其它模块），
   可考虑从 `engine2.dll` / `resourcecompiler.dll` 侧找入口。

### 9.6 当前状态（未回退）

* 三方库：**仍未引入**（`PackageReference` = 0，源码无 `ValveKeyValue` 引用）。
* 加载路径：t16 的中间态（`Tier0Kv3Service.ProbeNative` 真实调用 tier0 做格式/头行校验 + 自包含读取器）。
* 对账口径：仍全部通过（71 类型 / 3 顶层键 / subassettypes 8 / asset_warnings 1 / CSV 自校验 71-71 差异 0）。
* build / 自检 / GUI 冒烟 / CompileHost / check-naming：**全部与 t18 一致，无回归**。



---

## 10. 修订记录（t20）：CKV3Arena 构造协议已提取（arena 侧打通，剩块分配器一环）

### 10.1 结论

**arena 构造协议已完整提取并逐条移植成功——`LoadKV3(arena,…)` 不再 AV，解析器真正跑起来了。**
剩余阻塞是一处**与 arena 无关的块分配器**：`CUtlMemoryBlockAllocator` 报
「allocation of 19 too large, max 1」（size-class 表为空）。按任务书授权如实收口。

### 10.2 突破：从真实调用方拿到确定性构造序列

打开 `resourcecompiler.dll`（IDA 会话 `rc-arena`），定位 `LoadAssetTypesCommonTxt`
（VA `0x181F1B3E0`，即 t12 重定位值；由 `assettypes_common.txt` 字符串反查得到）。
它调 `LoadKV3FromFile(root, err, "assettypes_common.txt", "EXECUTABLE_PATH", &fmt, 0)`，
而 root 来自 `sub_181EBA460(ctx) = ctx+120`，ctx 由 `sub_181EAA340(ctx, 0)` 构造。

**构造序列（两级，全部反编译实证）**：

```
sub_181EAA340(ctx, 0)                    // 外层
  └─ sub_181EAA400(ctx, ctx)            // 内层：字段清单 + 建 253 节点空闲链
  ctx[4400] |= 4                         // has root
  node = ctx[80]                         // 取空闲链头
  ctx[92]++ ; ctx[80] = *node ; node[1] = 0
  N = (node - ctx - 120) >> 4
  *node = (N << 18) | 0x2404            // node0 的头：bit2 has root + bit13/14
root = sub_181EBA460(ctx) = ctx + 120
```

### 10.3 CKV3Arena 字段地图（内层 ctor 逐条）

| 偏移 | 类型 | 值 | 说明 |
|---|---|---|---|
| `+0` | QWORD | `ctx` | 自指针 |
| `+8` | CUtlBuffer | ctor(0,0,0) | **LoadKV3 从这里读缓冲区** |
| `+72` | QWORD | `ctx` | 自指针（node0 的 arena 槽） |
| `+80` | QWORD | 空闲链头 | 外层从这里取 node0 |
| `+88` | DWORD | **253** | 节点容量 |
| `+92` | DWORD | 0（外层 ++） | 已用节点数 |
| `+96/+104/+112` | QWORD | 0 | |
| `+4168..4304` | 16×QWORD | 0 | 空闲链数组（16 B stride） |
| `+4312/4328/4336/4360/4368` | QWORD | 0 | |
| `+4320` | DWORD | 32 | |
| `+4324` | BYTE | 0 | |
| `+4352` | — | `sub_1800C84F0(_, 2048, 0x200000, 0)` | CUtlMemoryPoolBase 初始化 |
| `+4381` | BYTE | 0 | |
| `+4384` | DWORD | `0x7FFFFFFE` | |
| `+4388` | BYTE | 1 | |
| `+4392` | DWORD | 0 | |
| `+4400` | BYTE | `|= 4` | flags：bit2 = has root |
| `+4408..4488` | QWORD | 0 | |

空闲链构建：从 `ctx+4168` 起**向下**每次 -16，共 `(2*253)>>1 = 253` 次。

### 10.4 探针结果（arena 侧已通，剩块分配器）

| 阶段 | 结果 |
|---|---|
| t19 及以前（猜偏移） | 全部 `0xC0000005` |
| **t20（逐条移植构造序列）** | **不再 AV**；解析器真正运行 |
| t20 剩余阻塞 | `FATAL ERROR: CUtlMemoryBlockAllocator<char, CMemAllocAllocator>::Alloc: allocation of 19 too large, max 1` |

已尝试但未解决该块分配器：`CMemAllocSystemInitialize`（RVA 0x12E1E0）、
`EarlyInit_Tier0`（RVA 0x84ED0）、手工 size 表（`arena+4168` 指向 16 级块大小表）、
`sub_1800C84F0` pool init（RVA 0xC84F0，调用即 `0xC0000409` fail-fast）。
`g_pMemAlloc`（DATA RVA 0x3AC458）已确认正常初始化（非 NULL）。

### 10.5 为什么还差一步（诚实说明）

`max 1` 说明某个 `CUtlMemoryBlockAllocator` **实例**的 size-class 表为空
（计数读到 1）。它不是 arena 的字段（arena 侧构造已完全正确），
而是 tier0 内部某个字符串/符号表分配器，其初始化入口尚未定位。

### 10.6 给后续的明确线索

1. **定位该 `CUtlMemoryBlockAllocator` 实例**：fatal 串 `"%s: allocation of %u too large, max %u"`
   在 tier0 RVA `0x313480`，唯一 `lea` 引用在 RVA `0x7E1D3` ⇒ 反编译该函数，
   看它从哪个全局/成员读 size 表指针与计数。
2. **找该分配器的初始化**：`CUtlMemoryBlockAllocator` 的 ctor/Init 会写表指针；
   搜导出表里 `CMemAllocSystemInitialize` 的调用方，或 `EarlyInit_Tier0` 内部。
3. **备选：绕过它**。若 19 字节分配来自符号表，可尝试给 arena 的 `CUtlBuffer`
   （`ctx+8`）传一个**已含数据的缓冲**，让解析器走不同路径。

### 10.7 当前状态（未回退）

* 三方库仍未引入（scope 内 `ValveKeyValue` 代码引用 = 0，`PackageReference` = 0）。
* 加载路径：t16 中间态（tier0 探针 + 自包含读取器）。
* 对账口径全部通过：71 类型 / 3 顶层键 / subassettypes 8 / asset_warnings 1 / CSV 自校验 71-71 差异 0。
* build / 自检 / GUI 冒烟 / CompileHost / check-naming：全部与 t19 一致，无回归。



---

## 11. 修订记录（t21）：**tier0 原生 KV3 全链打通**

### 11.1 结论

**tier0 原生 KV3 读取链已完整接通并落入产品代码。**
`LoadKV3(arena,…) → SaveKV3AsJSON → JSON` 全通，WPF 工具的配置加载**默认走原生链**
（自检输出 `KV3 path: tier0 原生链 (原生链 OK, JSON 33324 B)`）。

### 11.2 最后一环：arena+4352 内嵌符号表初始化

t20 的遗留阻塞（`CUtlMemoryBlockAllocator: allocation of 19 too large, max 1`）根因：
**arena 构造序列里 `+4352` 处那次调用被跳过了**。

参考构造 `sub_181EAA400` 明确调用：

```c
sub_1800C84F0(a1 + 4352, 2048, 0x200000, 0);   // a1 = arena
```

该函数初始化 **arena 内嵌符号表的 bucket 数组**。解析器把键名（如
`m_CompilerIdentifier`，19 B）送去 interning 时，这个符号表的块分配器
size-class 表为空 ⇒ 第一个键名就 FATAL。

**关键归因纠正**：`0xC84F0` 是 **resourcecompiler.dll** 的 RVA；
tier0.dll 的同名偏移是**完全不同的函数**（t20 误试 tier0 版导致 `0xC0000409`）。
⇒ 必须加载 resourcecompiler.dll 并调它的 `0xC84F0`。

### 11.3 完整构造协议（t20 + t21 合并，可直接复现）

```c
// 1) arena：4608+ B 零填充缓冲（探针用 64 MB 堆缓冲）
// 2) 内层字段（sub_181EAA400 逐条）：
//    +0/+72 = arena 自指针；+8 = CUtlBuffer(0,0,0)；
//    +4168..4304 = 16xQWORD 0；+4320 = 32；+4324 = 0；
//    +4312/4328/4336/4360/4368 = 0；+4381 = 0；
//    +4384 = 0x7FFFFFFE；+4388 = 1；+4392 = 0；+4408..4488 = 0
// 3) ★关键★ 调 resourcecompiler.dll 的 sub_1800C84F0(arena+4352, 2048, 0x200000, 0)
// 4) +88 = 253（节点容量）；+96/+104/+112 = 0
// 5) 空闲链：从 arena+4168 起向下 -16，共 253 次；arena+80 = 链头；+92 = 0
//    v8 = arena[4168]; if (v8) v8[32] = arena+72; else arena[4176] = arena+72
//    arena[104] = 0; arena[96] = arena[4168]; arena[4168] = arena+72
// 6) 外层：arena[4400] |= 4; node = arena[80]; arena[92]++; arena[80] = *node;
//    node[1] = 0; N = (node - arena - 120) >> 4; *node = (N << 18) | 0x2404
// 7) root = arena + 120
```

### 11.4 KV3ID_t 布局（24 B，从 resourcecompiler 的 off_182742108 提取）

```c
struct KV3ID_t {
    const char* pName;   // +0x00  "generic"
    GUID        format;  // +0x08  7412167c-06e9-4698-aff2-e63eb59037e7
};                        // 总 24 B
```

文件头 `<!-- kv3 encoding:text:version{…} format:generic:version{7412167c-…} -->`
里的 format GUID 就是这个；`pName` 对应 `generic`。

### 11.5 CUtlBuffer 布局（LoadKV3 只读这 4 个字段）

| 偏移 | 值 |
|---|---|
| `+4` | `(len+1) \| 0x80000000`（flags，低 31 位非零才用 +8） |
| `+8` | 文本指针 |
| `+16` | 起始偏移 = 0 |
| `+32` | 结束偏移 = len |

### 11.6 4 跑测试矩阵（结论：必须两者都做）

| +4352 初始化 | fmt | 结果 |
|---|---|---|
| 无 | NULL | FATAL: allocation of 19 too large, max 1 |
| **有** | NULL | Fatal error（0xC0000409，tier0 误归因时）／仍 FATAL |
| 无 | 真实 | FATAL: allocation of 19 too large, max 1 |
| **有** | **真实** | **LOAD-OK，JSON 33324 B** |

⇒ **`+4352` 初始化与真实 `fmt` 两个都必需**，缺一不可。

### 11.7 对账（原生链产出 vs 已知结构）

| 项 | 原生链产出 | 预期 |
|---|---|---|
| 顶层键 | 3：`asset_warnings` / `assettypes` / `subassettypes` | 3 |
| assettypes | **71**（含引号键 `3dsmax`） | 71 |
| subassettypes | **8** | 8 |
| asset_warnings | 1 组，标题 `Legacy Compiled Data` | 1 |
| 带 `m_CompilerIdentifier` | **29** | 29 |
| distinct 标识符 | **27**，与 CSV **集合完全相同** | 27 |
| `_class` 分布 | CResourceAssetTypeInfo 35 · CSimpleAssetTypeInfo 27 · CBitmapAssetTypeInfo 8 · CMapAssetTypeInfo 1 | 同 |
| 名称对账 | CSV 71 名 ↔ JSON 71 名，**双向 0 差异** | 0 |

### 11.8 落地改造

* 新增 `projects\CS2-Tools\SDK\Tier0\Kv3NativeArena.cs`：完整原生链封装
  （`Load(tier0Path, rcPath, kv3Text) -> NativeKv3Result`）。
* 新增 `projects\CS2-Tools\SDK\Tier0\Kv3JsonModel.cs`：JSON → `Kv3Value` 桥接
  （`System.Text.Json`，BCL，不引三方库）。
* `Kv3ConfigService.Load` 改为**默认走原生链**，失败时回退自包含读取器
  （`Tier0Kv3Service.Parse`），并在自检输出里打印用了哪条路径。
* UI / Models 逻辑**一行未改**（消费的仍是 `Kv3Value` 树）。

### 11.9 验证（全部亲自跑）

| 项 | 结果 |
|---|---|
| build SDK / AssetCompiler（Release+Debug） | 均 **0 警告 0 错误**，CompileHost 两种配置均 staged |
| `--selftest` | **ALL PASS exit 0**；`KV3 path: tier0 原生链 (原生链 OK, JSON 33324 B)` |
| 对账 | 71 类型 / 3 顶层键 / 8 subassettypes / 1 warning / 29 带编译器 / distinct 27 与 CSV 集合相同 / CSV↔JSON 名称双向 0 差异 |
| GUI 冒烟 | 8 秒存活，标题 `CS2 Asset Compiler (tier0 KeyValues + resourcecompiler.dll)`；stdout/stderr 0 B |
| CompileHost | init exit 0；compile exit 9（stage=compile-path，错误串不变） |
| InvokerSmokeTest | T1-T4 全 PASS |
| check-naming | **614 篇 / 128 不合规 = 既有基线** |
| 三方库 | scope 内 `ValveKeyValue` 代码引用 = 0；两 csproj `PackageReference` = 0 |

### 11.10 踩坑（t21 新踩 2 条）

1. **同名 RVA 跨模块归因错误**：`0xC84F0` 在 tier0 与 resourcecompiler 是
   完全不同的函数（前者是大函数中部，后者才是符号表初始化）。
   跨模块调函数必须按「函数所在模块」取 RVA，不能拿另一个模块的同名偏移。
2. **`resourcecompiler.dll` 路径推导**：配置在 `<game>\bin\assettypes_common.txt`，
   DLL 在 `<game>\bin\win64\`；相对路径已含 `bin\win64\`，基准目录应是 `<game>`
   （即 `bin` 的上一级），不是 `bin` 本身。



---

## 12. 修订记录（t22）：SDK 调用层按 CS2Fixes 签名范式重构

### 12.1 变更

按用户指示（「SDK 调用方式过于杂乱，可参考 Source2ZE/CS2Fixes 签名调用方式」），
把散落的裸偏移 / RVA 硬编码 / 变形导出名收敛为 CS2Fixes 式三层：

| 文件 | 对应 CS2Fixes | 职责 |
|---|---|---|
| `SDK\Native\Module.cs` | `utils/module.h` (CModule) | Load / Base / Sections / FindInterface(CreateInterface) / FindSignature(逐字节+通配) / FindUniqueSignature(唯一命中) / ResolveCallTargetRva(调用方+rel32) |
| `SDK\Native\Signatures.cs` | `gamedata/cs2fixes.jsonc` | 命名签名表：模块 + windows 字节模式 + **如何重新找到它**注释 |
| `SDK\Native\Functions.cs` | `addresses.h` + `addresses.cpp` | 强类型委托声明 + 单一 `Initialize(gameBinDir)`（集中绑定、fail-fast、逐条记日志） |

`Kv3NativeArena` 同步重写：arena / CUtlBuffer / CUtlString / KV3ID_t 改为
`[StructLayout(LayoutKind.Explicit)]` 结构体，arena 的 20+ 字段偏移收敛为具名常量；
函数调用全部走 `Functions` 的强类型委托。

### 12.2 签名实测唯一性（两个模块都扫过）

| 签名 | 模块 | 命中数 | RVA |
|---|---|---|---|
| `ArenaCtor` | resourcecompiler | **1** | `0x1EAA400` |
| `GenerateResourceFile` | resourcecompiler | **1** | `0x188B90` |
| `AssetConfig_Init` | resourcecompiler | **1** | `0x187EA0` |
| `SymTabPoolInit`（28 B 序言） | resourcecompiler | **10** | `0xC84F0`/`0x1FBC50`/`0x1FBDF0`/`0x1FBF90`/`0xD37DA0`/`0xF38FC0`/`0x13B2100`/`0x190C120`/`0x1FA4DB0`/`0x1FCCDD0` |

**`SymTabPoolInit` 的函数体在 rc 里被多处内联展开 ⇒ 纯模式无法区分**。28 字节序言模式实测
命中 **10 处**（上表），它们只共享 **29 字节**序言，随后在 `E8 rel32`（调用目标）与多处
RIP 相对位移上分叉——hit0(`0xC84F0`) vs hit1(`0x1FBC50`) 在 512 字节内 **111 字节**不同，
是「同一内联代码的不同相对位移副本」，**不是字节完全相同**。

解法是 CS2Fixes 式的
「定位调用方 + 提取 RIP 相对位移」：签名定 `ArenaCtor`（唯一），读其 `+0xF8` 处的
`E8 rel32` 算出被调地址。实测 `0x1EAA400 + 0xF8 + 5 + rel32 = 0xC84F0`，与参考实现一致。
由 `Module.ResolveCallTargetRva` 实现。

> **勘误（t22 复验，队长实测 + B 复核）**：本处原写「4 份字节完全相同的副本」，不准确。
> 实际是 10 处内联展开、仅共享 29 字节序言；「纯模式无法区分 ⇒ 走 ArenaCtor+0xF8 rel32」
> 的结论反而更硬（10 处歧义而非 4 处）。

### 12.3 fail-fast 实测

| 输入 | 行为 |
|---|---|
| 不存在的签名模式 | `SignatureNotFoundException` |
| `SymTabPoolInit` 的函数体模式（4 副本） | `SignatureAmbiguousException`（10 hits，含通配后） |
| 未初始化就调 `Kv3NativeArena.Load` | `FunctionResolveException: 尚未调用 Functions.Initialize` |

### 12.4 行为保持（重构后全部不变）

| 项 | 结果 |
|---|---|
| `dotnet build CS2-Tools.sln -c Release/Debug` | 均 **0 警告 0 错误** |
| `--selftest` | **ALL PASS exit 0**；`KV3 path: tier0 原生链 (原生链 OK, JSON 33324 B)` |
| 解析日志 | 5 条 `Found … at 0x…` 全部正确（含 `SymTabPoolInit via ArenaCtor+F8 → 0xC84F0`） |
| 数字 | 71 类型 / 3 顶层键 / 8 subassettypes / 29 带编译器 28+1 / 43 注册表 / distinct 26-27 |
| CompileHost | Release/Debug 均 init **exit 0**、compile **exit 9**（stage=compile-path，错误串不变） |
| GUI 冒烟 | 8 秒存活，stdout/stderr 0 B |
| check-naming | **614 篇 / 128 不合规 = 既有基线** |
| 三方库 | CS2-Tools 内 `ValveKeyValue` 代码引用 = 0；`PackageReference` = 0 |

### 12.5 踩坑（t22 新踩 3 条）

1. **仓库同期被重构到 `projects\CS2-Tools\`**（`Cs2Sdk` → `SDK`，命名空间 `Cs2Sdk.*` → `SDK.*`，
   `CompileHost` 移出 SDK）。t22 的文件先落到已orphan的 `projects\Cs2Sdk\`，
   发现后迁到 `projects\CS2-Tools\SDK\Native\` 并删掉 orphan 目录。
2. **`Platforms=x64` 会在 `bin` 下多插一段 `x64`**：`CopyCompileHostOutputs` 的 glob
   `bin\$(Configuration)\...` 匹配不到 `bin\x64\$(Configuration)\...`，
   导致 `CompileHost.dll` 没被复制进主程序输出目录（Tier1 不可用）。
   已改为 `bin\**\$(Configuration)\...`；`ResolveHostExe` 也补了 `bin\x64\<cfg>` 候选。
3. **路径常量里的 `\w` / `\t` 会被 JSON 转义搞坏**（`bin\win64\tier0.dll` → `binwin64<TAB>ier0.dll`）。
   代码里的路径常量一律用正斜杠。

### 12.6 未做（如实说明）

* `RegistryService` 的表头 RVA（`0x3078730`）与 `CompilerInterop` 尚未完全迁到签名范式——
  表头是**数据**而非代码，没有可签名的指令序列；当前保留单一 RVA 常量并集中在
  `Functions` / `Signatures` 注明重新找到的方法（`GenerateResourceFile`/`AssetConfig_Init`
  两个代码函数已迁到签名）。
* `NativeStringFunctionInvoker.cs`（448 行大杂烩）未收缩——它仍被 `RegistryService`、
  `CompileHost`、`InvokerSmokeTest` 使用，删除会破坏回归门禁；留待后续专项。



## 13. t24 修订：ValveKeyValues 移除后的 Reader 清理 + 原生链稳定性修复

用户指示：「KeyValues 部分为什么还有 Reader 手动解析部分。完全可依赖 tier0 KeyValues 函数」。

### 13.1 移除手动读取器

- `SDK\Tier0\Tier0Kv3Service.cs` 删除自包含 KV3 文本读取器（Parse + Reader 类，约 150 行）；
- `Kv3ConfigService.Load` 改为**单一原生路径**：Functions.Initialize → Kv3NativeArena.Load（LoadKV3(arena) → SaveKV3AsJSON）→ Kv3JsonModel 桥接，任何一步失败即抛错（fail-fast，不再静默回退）。

### 13.2 修复间歇性堆损坏（队长定位，实测 30/30 通过）

移除 Reader 后密集回归发现**间歇崩溃**（0xC0000374 堆损坏 / 0xC0000005 AV，崩率约 35%）。
二分定位过程：最小控制台复现（63%）→ 仅构造即崩（37%）→ 排除探针/LibraryImport/对齐/VirtualAlloc/节点容量/池初始化 → **锁定 CUtlBuffer 构造函数**：

* **根因**：原生 `CUtlBuffer` 大于 40 字节——其构造函数要写 `+0x28`/`+0x30` 处的函数指针（`CCallStackStatsGatherer_Internal_DumpStatsToFile` / `CUtlBuffer::PutOverflow`），原代码只分配 40 字节 ⇒ 越界写穿 CRT 堆块头 ⇒ 后续堆操作检出损坏（崩点随检测时机漂移，故表现为"间歇"）。
* **修复**：`bufMem`/`buf` 分配 40 → **256 字节**（注释已写明原因）。修复后完整路径 **30/30 零崩溃**。
* **附带修正**：`Functions.Initialize` 补回 `EarlyInit_Tier0`（与 `CMemAllocSystemInitialize` 成对，t21 探针如此调用，t22 重构一度漏掉）。

### 13.3 验证

- `dotnet build CS2-Tools.sln` Release/Debug：0 警告 0 错误；
- `--selftest`：ALL PASS exit 0，`KV3 path: tier0 原生链 (原生链 OK, JSON 33324 B)`，Initialize 六条解析日志（新增 EarlyInit_Tier0 @0x84ED0）；
- 完整路径 30 连跑：**0 崩溃**（修复前约 35%）；GUI 冒烟 8 秒存活。

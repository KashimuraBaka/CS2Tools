# `projects\CS2-Tools\AssetCompiler` — CS2 资产编译工具（tier0 KeyValues + resourcecompiler.dll）
> 用 **tier0.dll 原生 KV3 API**（不引任何三方 KV 库）加载 `assettypes_common.txt`（KV3 文本），
> 并对 vmdl/vdata/vnmgraph 等资产调用 `resourcecompiler.dll` 编译、展示结果。
>
> ⚠ **守卫句：请勿重新引入三方 KV 库（ValveKeyValue / ValveKeyValues）**。t16 已按用户指示移除；
> KV3 **默认走 tier0 原生链**（`SDK.Tier0.Kv3NativeArena`：`LoadKV3(arena,…) → SaveKV3AsJSON → JSON`，t21 打通），
> 并用 `Tier0Kv3Service.ProbeNative`（`?LoadKV3Text_NoHeader`）做头行校验与引擎错误串回显。
> 自包含读取器保留为**回退保底**（t21 队长裁定），原生链异常时自动回退、路径在自检/日志明示。
> 关联分析：`docs\dlls\ResourceCompiler.dll\assettypes_common_compileridentifier_report.md`（t24 编译注册表链路）、
> `docs\dlls\ResourceCompiler.dll\assettypes_common_config_report.md`（A 的 t10 配置盘点）、
> `docs\dlls\ResourceCompiler.dll\kv3_parse_api_report.md`（t11 KV3 解析 API 定案）、
> `docs\dlls\ResourceCompiler.dll\asset_compiler_e2e_report.md`（A 的 t14 端到端终验）、
> `docs\dlls\ResourceCompiler.dll\asset_compiler_wpf_report.md`（t13 交付 + t15/t16/t18/t19/t20/t21 修订记录）。

## 1. 工程与运行

```
projects\CS2-Tools\AssetCompiler\        ← 根目录仅入口与工程文件
├── AssetCompiler.csproj      net10.0-windows + UseWPF + x64 + AllowUnsafeBlocks
├── App.xaml / App.xaml.cs       应用入口（合并 Styles\Styles.xaml）+ --selftest 无头自检
├── MainWindow.xaml / .cs        主窗口入口（五个面板）
├── README.md                    本文件
├── Models\Models.cs             UI 行模型（AssetTypeRow / RegistryEntryRow / ResolveResult…）
├── Services\                    Kv3ConfigService（KV3 加载：游戏目录 → assettypes_common.txt → tier0 解析）
│                                RegistryService（SDK 薄封装）
│                                CompileService（编译执行，进程内 + EngineServiceGuard 前置守卫）
└── Styles\Styles.xaml           t17 Fluent 主题补齐（DataGrid 一族 + 画刷/尺寸令牌）
```

```powershell
dotnet build projects\CS2-Tools\AssetCompiler\AssetCompiler.csproj -c Release      # 0 warning 0 error
dotnet run   --project projects\CS2-Tools\AssetCompiler\AssetCompiler.csproj -c Release
# 无头自检（CI / 复核用，无需点 GUI）
projects\CS2-Tools\AssetCompiler\bin\Release\net10.0-windows\AssetCompiler.exe --selftest
# GUI 启动冒烟（t15 教训：自检 ALL PASS ≠ GUI 能起来；交付 WPF 必须额外做这个）
$p = Start-Process projects\CS2-Tools\AssetCompiler\bin\Release\net10.0-windows\AssetCompiler.exe -PassThru
Start-Sleep -Seconds 8; if ($p.HasExited) { throw "GUI 启动即退出 exit=$($p.ExitCode)" } else { Stop-Process -Id $p.Id }
```

`NativeStringFunctionInvoker.cs` 以 `<Compile Include="..\..\tools\csharp\NativeStringFunctionInvoker.cs">`

## 2. 五个面板

| 面板 | 说明 |
|---|---|
| ① 配置加载 | **唯一输入是游戏目录**（默认预填 + 目录选择）；`bin\assettypes_common.txt` 相对于它派生；错误双通道（MessageBox + 日志） |
| ② 资产类型表 | DataGrid；「仅有编译器」过滤 + 名称/友好名/扩展名/编译器搜索；数据全部来自加载的配置本身 |
| ②' subassettypes | **独立标签页**（8 条，字段只有 m_bFollowReferences / m_bAllowDefinitions 开关，无 `_class`/编译器）——不并入主表、不隐藏 |
| ③ 编译器解析 | 选中类型 → `m_CompilerIdentifier` → 注册表解析 → **条目序号 / 工厂 RVA / 单例 RVA**；未命中时优雅降级显示「Resource type compiler identifier not found」语义，**不崩溃、不显示空指针** |
| ④ 编译面板 | 多选源文件 → **单一进程内编译**（签名解析；可指定输出目录）→ 逐文件：成功/失败、输出路径+大小+mtime、耗时 |
| ⑤ 日志面板 | 模块基址 / RVA / 委托 / 返回值 / 错误串 |

## 3. 关键设计决策

### 3.1 编译执行：进程内 + 前置守卫（t28c，取代 t12 的子进程隔离）

**历史**：t12 实证 `AssetConfig_Init` / `GenerateResourceFile` 在裸进程必崩
`0xC0000005`——编译路径依赖引擎服务图，而 .NET 对 AccessViolationException
fail-fast 不可 catch，故一度用 CompileHost 子进程隔离（崩了只是退出码）。

**现状（用户明确禁止额外进程方式）**：编译改为**全程进程内**（t28q 起无 Tier 之分），靠
`SDK.Native.EngineServiceGuard` 前置守卫避免进入崩代码：

* IDA 反编译 `AssetConfig_Init`(sub_180187EA0) 显示它是**虚表方法**（a1 为引擎创建的
  asset-config 对象），且经一批间接槽访问全局服务对象
  （`qword_183B60158` / `qword_183B60060` / `qword_183B60140` / `off_182FEAE88` /
  `g_pThreadPool`）——这些由**引擎主程序**在服务注册阶段写入；
* 我们的进程只 `LoadLibrary` 了 tier0/resourcecompiler（其静态依赖仅
  tier0/FBX/embree/v8/steam，**不含 engine2**），服务槽全 NULL ⇒ 调用即 AV；
* VEH（向量化异常处理）进程内恢复路线已实测不可行：.NET 运行时对原生 AV 走
  fail-fast，上下文重写后重执行仍崩（进程 exit 0xC0000005）；
* **守卫做法**：调用前先读这些全局量（带映像边界检查），任缺即**不调用**，
  把「引擎服务图未注册」作为可预期错误上报——应用不崩、原因明确。

**KG3 类资源（.vnmgraph/.vdata）已端到端跑通**（t28i→t28m）：tier0 原生序列化链路进程内可达，
编译产物落 `.vXXX_c`（如 viewmodel.vnmgraph → 113178 B，源 1066582 B，9.4x）。
**其余资源的编译执行仍缺引擎服务注册**（= 引擎引导：engine2 + filesystem + mesh/material
初始化序列）——属重大工程，未做；这批资源走 SDK 门面会得到明确失败（不调用、不崩）。

### 3.2 单一编译路径（t28q：只有一级，无 DLL/exe 选择）
* **唯一路径 = 进程内签名编译**：`签名解析 → 注册表只读验证 → KV3 类直接编译`。
  无 DLL/exe 按钮、无 mod dir 字段、不起任何子进程——用户口径「默认就是通过签名进行编译，
  无需选择 dll 和 exe」。
* Tier2（官方 `resourcecompiler.exe`，`-i <file> -game <moddir> -nop4 -v`）与
  CompileHost 子工程均已删除；编译执行体是 `SDK.ResourceCompiler.ResourceCompilerService`，
  UI 侧 `CompileService.CompileSource` 只是薄适配。
* 失败本身是有价值的实证结论，UI 不隐藏（如实上报 Stage/Detail）。
* **输出目录（t28s）**：用户指定了就必须可写——编译前先实测（建目录 → 写探针 → 删），
  不可写就提示并**不开始编译**。指定目录留空时按候选顺序落地：源文件同目录 →
  `%TEMP%\CS2-Tools-Compiled`；全部不可用则明确报错，**绝不**悄悄写进程序自身的
  bin 目录（那会让产物看起来像构建垃圾，并掩盖真实原因）。`UnauthorizedAccessException`
  多半是被沙盒/只读/安全策略拦截，与磁盘权限是两回事。

### 3.2.1 线程口径：WPF 控件只能由 UI 线程访问（t28r 修复，防回退）
* **症状**：点「编译」抛 `InvalidOperationException: 调用线程无法访问此对象，因为另一个线程拥有该对象`，
  栈顶落在 `private string GameDir => TxtGameDir.Text.Trim();`。
* **根因**：`GameDir` 读的是 WPF 控件 `TxtGameDir.Text`，却被写进 `await Task.Run(())` 的 lambda 里——
  编译在**线程池线程**上跑，该线程访问 UI 控件即抛。`Task.Run` 的 lambda 体运行在另一个线程，
  不是 UI 线程。
* **规则**：**凡读 `TxtXxx` / 绑定属性，必须在第一个 `await` 之前（此时仍在 UI 线程）取成局部变量**，
  lambda 里只能引用这些局部变量。
* **回归探针**：`temp\t49-probes\UiCompileProbe`（STA 线程建真实 `MainWindow`，反射调用真实
  `OnCompile`；PASS 判据 = 结果行 1 条且 Status=成功、产物路径非 `-`、日志无 `ERROR:`）。
  已验证：修复版 exit=0 PASS，把 buggy 行还原后 exit=2 FAIL——探针能真正抓住该回归。
* `async void` 里未处理异常会直接崩掉 GUI：`OnCompile` 已整体包 try/catch，兜住并写日志 + 弹窗，
  不再静默闪退。

### 3.3 注册表：禁用锚点库旧 RVA，工厂指针实时读
* 锚点库 `tools\anchors\compiler-identifier-anchors.json` 的 `build_sha256=d1115f9f…` 已**不等于**
  盘上的 `204E16E5…`（游戏 10-17 更新）。helper 的 `ValidateBuild` 会抛异常——这正是它该做的，
  故**不**用 `ReadCompilerRegistry` / `ResolveCompilerFactoryByString`。
* 注册表**表头 RVA 经实测未变**（`0x3078730`，43 条标识符与 t24 逐位相同），但
  **工厂指针非均匀平移**（实测 +0x1C0 / +0x90 / +0x110 / 0 混存）⇒ **绝不可硬编码工厂 RVA**，
  必须从表里实时读。表头 RVA 仍做**运行时自校验**（index 0 必须读到 `CompileWorldNode`），
  不符则明确报错，不静默用错数据。
* 进程内**不做** RTTI 全链读取：t12 是在隔离探针里验证的，COL/TD 偏移口径一旦随构建漂移，
  错误偏移会读坏内存 ⇒ AV ⇒ fail-fast 打死 GUI。

### 3.4 UI 计数口径（防 off-by-one）
* 带编译器条目 = **29** = 可解析 **28** + 未命中 **1**（`resource_remap_table` → `CompileResourceRemapTable`，注册表无此条）。
* 28 个可解析条目跨 **26 distinct**（`CompilePanorama` 3 条 → 1 distinct）。
* **UI 用条目级（28/29）**；distinct 级（26/27）另列，两口径不混用。
* `_class` 分布：`CResourceAssetTypeInfo` 35（28 带编译器 + 7 不带）· `CSimpleAssetTypeInfo` 27 · `CBitmapAssetTypeInfo` 8 · `CMapAssetTypeInfo` 1（带编译器）。

### 3.5 InvariantGlobalization 必须保持 false（t15 修复，防回退）
`<InvariantGlobalization>true</InvariantGlobalization>` 与 **WPF 字体/文本排版不兼容**：
WPF 文本链（`TextFormatterImp → FontFaceLayoutInfo → MS.Internal.FontCache.MajorLanguages`）要构造真实
`CultureInfo("en")`，invariant 模式下抛 `CultureNotFoundException` ⇒ 布局测量期间**每个文本元素抛一次**
⇒ GUI 启动即以 `0xE0434352` 未处理异常退出（t15 实测：exit `-532462766`）。
它们不走 WPF 渲染路径，且 invariant 是对共享 helper 的更严格文化无关性回归保护）。
⇒ 本工程（UseWPF）**必须保持 `InvariantGlobalization=false`**；改它之前先读 `asset_compiler_wpf_report.md` §6。

## 4. 落位纪律（t49 规则）
* 本工程位于 `projects\CS2-Tools\AssetCompiler\`——**任何项目（工程）一律写 `projects\`**（t49 队长裁定，规则见 `docs\meta\structure_guide.md` §0）；`tools\` 只放通用检查/分析工具与共享库（如 `tools\csharp\NativeStringFunctionInvoker.cs`）。
* 本目录 = **工程本体**（不硬编码任何索引副本——游戏自带的 assettypes_common.txt 是唯一正本）；分析类 md 不写入 `projects\`。
* 测试/探针/测试源/试跑日志 → `temp\t49-probes\`（可清空、须可复现）；唯一证据（自检输出、host 输出、GUI 冒烟日志）已引用进 `docs\dlls\ResourceCompiler.dll\` 下的报告。

## 5. 已知限制（真实结论，非失败）
**裸进程无法驱动编译**：`resourcecompiler.dll` 的编译路径需要引擎宿主提供服务图
（文件系统 / KV3 解析 / 资产配置）。`CreateInterface("ResourceCompilerSystem001")` 可拿到
系统对象，但 `AssetConfig_Init` 在其配置加载器内经间接槽调用引擎服务时 AV。
要与真实资产打交道，必须用官方 `resourcecompiler.exe` 配完整 mod 上下文，或由引擎宿主加载本 DLL。
这与 t24/t25 是同级的真实逆向结论。

## 6. tier0.dll 原生 KV3 加载链（t16 起步，**t21 全线打通**；勿引三方 KV 库）

```
assettypes_common.txt (KV3 text)
   |
   +== 默认路径（t21 打通）===========================================
   |   SDK.Tier0.Kv3NativeArena.Load(tier0.dll, resourcecompiler.dll, text)
   |     1) arena：64 MB 零填充 + 内层 20+ 字段逐条写入
   |     2) ★调 resourcecompiler.dll 的 sub_1800C84F0(arena+4352, 2048, 0x200000, 0)
   |        —— arena 内嵌符号表 bucket 数组初始化（缺此项 ⇒ 第一个键名 interning 即 FATAL）
   |     3) 建 253 节点空闲链 + 取 node0 + 写头 (N<<18)|0x2404
   |     4) LoadKV3(arena, &err, &buf, &fmt(KV3ID_t), "EXECUTABLE_PATH", 0) -> true
   |     5) root = arena+120 -> SaveKV3AsJSON(root, &out, &err) -> JSON 文本
   |
   +-- Tier0Kv3Service.ProbeNative()  ->  tier0.dll
   |      LoadLibraryW + GetProcAddress(?LoadKV3Text_NoHeader...)
   |      bit0=1 对象 + 全文  =>  返回 False + 引擎错误串
   |      Line 1: Invalid value <!--      （只读探针，做头行/格式校验与错误串回显）
   |
   +-- 回退路径（原生链异常时自动切入）
   |      Tier0Kv3Service.Parse()  ->  自包含 KV3 读取器
   |      key = value / { } / [ ] / flag:value / 三引号多行串
   |
   +-- Dictionary<string, Kv3Value> -> Models -> UI 行模型
```

关键事实（IDA 反编译 + 探针实证，详见 `docs\dlls\ResourceCompiler.dll\kv3_parse_api_report.md` §11）：

| 事实 | 值 |
|---|---|
| `Root()` | `arena+120`；要求 `arena[4400] & 4` |
| 对象头（首 8 字节） | `bit0=1` 无 arena；否则 `arena = *(obj - (2*N+6)*8)`，`N=(uint16)(hdr>>18)` |
| `CUtlBuffer` | `+4` flags（低 31 位非零才用 `+8`；引擎自建 `(len+1)` 并置 `0x80000000`）、`+8` pBase、`+16` start、`+32` end |
| `CUtlString` | 首 qword = `char*` |
| `LoadKV3(arena,…)` | = `Root(arena)` + `LoadKV3(kv,…)` |
| **arena 内嵌符号表初始化** | 必须调 **resourcecompiler.dll** 的 `sub_1800C84F0(arena+4352, 2048, 0x200000, 0)` |
| **`KV3ID_t` 布局** | 24 B：`{ const char* pName("generic"); GUID format(7412167c-06e9-4698-aff2-e63eb59037e7) }` |

**跨模块 RVA 陷阱（勿删）**：`0xC84F0` 是 **resourcecompiler.dll** 的 RVA；**tier0.dll 的同名偏移是
完全不同的函数**（前者是符号表初始化，后者是大函数中部）。跨模块调函数必须按「函数所在模块」取 RVA，
不能拿另一个模块的同名偏移。

**测试矩阵（两者缺一不可）**：`+4352` 初始化 + 真实 `fmt` ⇒ LOAD-OK（JSON 33324 B）；
缺任一 ⇒ `FATAL: CUtlMemoryBlockAllocator allocation of 19 too large, max 1`。

原生链 `LoadKV3(arena,…) → SaveKV3AsJSON` **已全线打通（t21）并作为默认路径**；自包含读取器**保留为回退保底**（t21 队长裁定：原生链依赖两个游戏 DLL 与 arena 构造协议，游戏更新导致协议漂移时回退保证 UI 始终可用；所用路径在自检/日志中明示，不静默降级）。



修法（两道，互为兜底）：

2. **运行时**：`CompileService.ResolveHostExe()` 按候选路径再探一次——

两种配置（Debug/Release）下均已验证进程内编译可达（自检 + GUI 冒烟 + 端到端产物校验）。

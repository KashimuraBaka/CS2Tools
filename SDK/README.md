# projects\CS2-Tools\SDK — CS2 SDK DLL 调用层

> 把散落各处的 DLL import 代码集中到**一个工程**，对外只暴露托管 API。
> 上游调用方（`projects\CS2-Tools\AssetCompiler\`、`tools\csharp\InvokerSmokeTest\`）不再直接出现 P/Invoke；
> 原生细节全部收敛在本工程内。

## 1. 架构（三层）

```
   调用方 ──▶  SDK.ResourceCompiler
  (UI / CLI)     RegistryInterop   CreateInterface + 43 条表 + 标识符解析
                 CompilerInterop   GenerateResourceFile 调用面（仅隔离子进程内可用）
              SDK.Tier0
                 Tier0Kv3Service   LoadKV3Text_NoHeader 原生探针
                 Kv3Value          自包含 KV3 文本读取器
              SDK.Native
                 NativeStringFunctionInvoker
                   LoadWithDllImport / GetExport / GetRva /
                   ResolveDelegateAtRva / ResolveDelegateByName /
                   ValidateBuild / ReadCompilerRegistry
                        │  P/Invoke + Marshal
                        ▼
                 tier0.dll  ·  resourcecompiler.dll
```

```
projects\CS2-Tools\SDK\
├── SDK.csproj                          net10.0-windows 类库 · x64 · AllowUnsafeBlocks
├── Native\NativeStringFunctionInvoker.cs  共享 helper（自 tools\csharp\ 整体迁入，唯一副本）
├── Tier0\Kv3Value.cs                     KV3 值模型（Kind/Table/Items/Flag）
├── Tier0\Tier0Kv3Service.cs              tier0 原生探针 + 自包含 KV3 读取器
├── ResourceCompiler\RegistryService.cs   注册表只读解析 + 标识符解析
├── ResourceCompiler\CompilerInterop.cs   编译调用面（RVA 常量 + 委托签名 + 隔离边界说明）
└── README.md
```

## 2. 公开 API 分层

| 层 | 入口 | 说明 |
|---|---|---|
| 配置加载 | `SDK.Tier0.Tier0Kv3Service.ProbeNative(tier0Path, text)` / `.Parse(text)` | KV3 → `Dictionary<string, Kv3Value>`；ProbeNative 真实调用 tier0.dll 并把引擎错误串回显 |
| 编译器解析 | `SDK.ResourceCompiler.RegistryService.Read(dllPath)` / `.Resolve(snap, id)` | 标识符 → 条目 / 工厂 RVA / 单例 RVA；表头 RVA 运行时自校验 |
| 编译调用 | `SDK.ResourceCompiler.CompilerInterop` | **进程内执行，但必须先过 `EngineServiceGuard` 预检**（见 §4） |

UI 层只面对上述托管 API；`AssetCompiler` 里的 `RegistryService` / `Kv3ConfigService`
已退化为**薄封装**（适配行模型 + 保留友好错误语义）。

## 3. 关键 ABI 事实清单

### 3.1 tier0 KV3

| 事实 | 值 | 来源 |
|---|---|---|
| `Root()` | `arena+120`；要求 `arena[4400] & 4`，否则 `Plat_FatalError("pool arena no root")` | IDA 0x180220300 |
| 对象头（首 8 字节） | `bit0=1` ⇒ 无 arena；否则 `arena = *(obj - (2*N+6)*8)`，`N=(uint16)(hdr>>18)` | IDA 0x180219B90 |
| `CUtlBuffer` | `+4` flags（低 31 位非零才用 `+8`；引擎自建值 = `(len+1)` 并置高位 `0x80000000`）、`+8` pBase、`+16` start、`+32` end | IDA 0x180124A20 / 0x1800D3D50 |
| `CUtlString` | 首 qword = `char*` | 探针实证 |
| `LoadKV3(arena,…)` | = `Root(arena)` + `LoadKV3(kv,…)` | IDA 0x180124910 |
| 入口（mangled） | `?LoadKV3Text_NoHeader@@…` / `?LoadKV3@@YA_NPEAVCKV3Arena@@…` / `?SaveKV3AsJSON@@…` | 导出表 |

### 3.2 arena 最小构造集（t19：**未定出**，负面结论如实记录）

`Root()` 与对象头解码已通（`arena+120`、`arena[4400] & 4`、`*(arena+72)=arena`），
但 `LoadKV3` 在建树阶段仍 `0xC0000005`。t19 三个探针变体（bump ptr / 容量 / 块数组 /
`arena+4408` 自指针的各种组合）全部 AV ⇒ 猜测偏移不是真正的分配器状态。

已知线索：

| 线索 | 值 / 来源 |
|---|---|
| 文本解析器唯一直接读的 arena 字段 | `sub_180219220 = *(QWORD*)(arena+4408)` |
| 3 个 KV3 loader 共用的 arena 访问器 | `sub_18008C420 = arena+8` |
| 节点分配机制 | `sub_180222FC0` 走 `UtlVectorMemory_Alloc`（**全局分配器**，不是 arena bump pointer） |
| 节点区校验 | `sub_18021AAF0`：64 B stride、从 `arena+40` 起、上限 `2*(arena+16 的 DWORD)` |
| `CUtlMemoryArena` RTTI 名 | tier0 有 ctor/Alloc/Free/MemoryBegin/MemoryEnd，但**未导出、vtable 未定位** |

⇒ 下一步优先反编译 `??0CUtlMemoryArena@@QEAA@I_N@Z` 本体（先解决 vtable 定位），
或从 arena 分配点 `sub_1800D3570` / `sub_18021E080` 反推字段语义。
详见 `docs\dlls\ResourceCompiler.dll\asset_compiler_wpf_report.md` §9。

### 3.3 resourcecompiler 编译注册表

| 事实 | 值 |
|---|---|
| 表头 RVA | `0x3078730`（t24 与当前构建**均未变**；仍做 index0 自校验） |
| 条目数 / 条目大小 | 43 / `0x28` |
| index0 标识符 | `CompileWorldNode`（自校验基准） |
| 工厂指针 | **非均匀平移**（实测 +0x1C0 / +0x90 / +0x110 / 0 混存）⇒ 禁硬编码，必须实时读 |
| `m_CompilerIdentifier` 偏移 | assettype 对象 `+0x108` |
| 关键 RVA | `GenerateResourceFile 0x188B90` · `AssetConfig_Init 0x187EA0` · `Resolver 0x1884C0` · `ConfigLoader 0x1F1B3E0` |

### 3.4 锚点库状态

`tools\anchors\compiler-identifier-anchors.json` 的 `build_sha256=d1115f9f…` **已不等于**盘上的
`204e16e5…`（游戏 10-17 更新）。`NativeStringFunctionInvoker.ValidateBuild` 会抛异常——
**这正是它该做的**，因此本工程**不**使用 `ReadCompilerRegistry` / `ResolveCompilerFactoryByString`，
而是直接按表头 RVA 实时读表。

## 4. 进程安全边界（重要）

* **进程内（GUI 安全区）**：`CreateInterface("ResourceCompilerSystem001")` + 注册表**只读**解析。
  t12 已实证在裸进程安全。
* **必须进程外**：`AssetConfig_Init` / `GenerateResourceFile`。t12 实证二者在裸进程 `0xC0000005`
  （编译器缺引擎服务图），而 .NET 对 `AccessViolationException` 是 fail-fast（不可 catch）
  ⇒ 在 UI 进程内直调会连 GUI 一起崩。⇒ 一律先过 `SDK.Native.EngineServiceGuard` 预检，缺服务槽则不调用（t28c；原 CompileHost 子进程方案已废弃，t28d 移除）。
* **进程内不做** RTTI 全链读取：t13 首版按 COL/TD 偏移直读导致 AV 打穿 GUI，已移除。

## 5. 结构定义参考来源

KeyValues3 / CUtl* 的结构布局与使用范式参考 **`G:\项目参考\GameTracking-CS2`**
（Source 2 schema 头文件转储）：

* `DumpSource2\schemas\<域>\*.h` 中 21 处 `KeyValues3 m_XXX;` 嵌入用法
  （compositematerialslib / animationsystem / worldrenderer / particleslib 等）；
* `DumpSource2\module_metadata\*.kv3` 共 31 个真实 KV3 文件 —— 已直接用作本 SDK 的测试语料。

> 该目录**只读**，未把其任何文件复制进本仓库；仅作结构参考并在本 README 与代码注释注明来源。

KV3 读取器实测覆盖：`server.kv3`(3.1 MB) / `client.kv3`(1.0 MB) / `pulse_system.kv3` /
`resourcecompiler.kv3` / `tools_hammer.kv3` / `assetpreview.kv3` 等 9 个文件全部解析成功
（最大者 12535 张表 / 20478 个数组 / 52187 个标量），与 `assettypes_common.txt` 并列验证。

## 6. tier0 原生 KV3 读取链（t21：**已全线打通**）

### 6.1 完整构造协议（t20 + t21，可直接复现）

```c
// 1) arena：4608+ B 零填充缓冲（SDK 用 64 MB 堆缓冲）
// 2) 内层字段（resourcecompiler 的 sub_181EAA400 逐条）：
//    +0/+72 = arena 自指针；+8 = CUtlBuffer(0,0,0)；
//    +4168..4304 = 16xQWORD 0；+4320 = 32；+4324 = 0；
//    +4312/4328/4336/4360/4368 = 0；+4381 = 0；
//    +4384 = 0x7FFFFFFE；+4388 = 1；+4392 = 0；+4408..4488 = 0
// 3) ★关键★ 调 resourcecompiler.dll 的 sub_1800C84F0(arena+4352, 2048, 0x200000, 0)
//    —— 初始化 arena 内嵌符号表的 bucket 数组（t20 跳过此项 ⇒ 第一个键名 interning 即 FATAL）
// 4) +88 = 253（节点容量）；+96/+104/+112 = 0
// 5) 空闲链：从 arena+4168 起向下 -16，共 253 次；arena+80 = 链头；+92 = 0
//    v8 = arena[4168]; if (v8) v8[32] = arena+72; else arena[4176] = arena+72
//    arena[104] = 0; arena[96] = arena[4168]; arena[4168] = arena+72
// 6) 外层：arena[4400] |= 4; node = arena[80]; arena[92]++; arena[80] = *node;
//    node[1] = 0; N = (node - arena - 120) >> 4; *node = (N << 18) | 0x2404
// 7) root = arena + 120
```

### 6.2 关键归因（跨模块 RVA 陷阱）

`0xC84F0` 是 **resourcecompiler.dll** 的 RVA；**tier0.dll 的同名偏移是完全不同的函数**
（前者是符号表初始化，后者是大函数中部）。跨模块调用必须按「函数所在模块」取 RVA。

### 6.3 KV3ID_t 布局（24 B）

```c
struct KV3ID_t {
    const char* pName;   // +0x00  "generic"
    GUID        format;  // +0x08  7412167c-06e9-4698-aff2-e63eb59037e7
};                        // 总 24 B
```

`CUtlBuffer`：`+4` = `(len+1)|0x80000000`；`+8` = 文本指针；`+16` = 0；`+32` = len。

### 6.4 测试矩阵（两者都必需）

| +4352 初始化 | fmt | 结果 |
|---|---|---|
| 无 | NULL | FATAL: allocation of 19 too large, max 1 |
| **有** | NULL | 仍 FATAL |
| 无 | 真实 | FATAL: allocation of 19 too large, max 1 |
| **有** | **真实** | **LOAD-OK，JSON 33324 B** |

### 6.5 对账（原生产出 vs 已知结构）

3 顶层键 / **71** assettypes（含引号键 `3dsmax`）/ **8** subassettypes /
1 asset_warnings（标题 `Legacy Compiled Data`）/ **29** 带 `m_CompilerIdentifier` /
**27** distinct（配置自身口径；t22 终态：71 行索引数据已按用户指示全部移除，`SDK\Data\AssetTypesIndex.cs` 只保留四个分类枚举作类型定义） /
`_class`：CResourceAssetTypeInfo 35 · CSimpleAssetTypeInfo 27 · CBitmapAssetTypeInfo 8 · CMapAssetTypeInfo 1 /
CSV↔JSON 名称双向 **0 差异**。

### 6.6 产品代码

* `Tier0\Kv3NativeArena.cs` —— `Load(tier0Path, rcPath, kv3Text) -> NativeKv3Result`，完整原生链。
* `Tier0\Kv3JsonModel.cs` —— JSON → `Kv3Value` 桥接（`System.Text.Json`，BCL）。
* `Kv3ConfigService.Load` 默认走原生链，失败回退自包含读取器。

## 7. 复现

```powershell
dotnet build projects\CS2-Tools\SDK\SDK.csproj -c Release                       # 0 警告 0 错误
dotnet build projects\CS2-Tools\AssetCompiler\AssetCompiler.csproj -c Release
tools\csharp\InvokerSmokeTest\bin\Release\net10.0-windows\InvokerSmokeTest.exe 1 2 3 4   # T1-T4
```


> sub_180222FC0 走 UtlVectorMemory_Alloc（全局分配器，非 arena bump）、
> CUtlMemoryArena RTTI 名存在但未导出且 vtable 未定位。
## 8. 调用层（t22：CS2Fixes 签名范式重构）

参考 [Source2ZE/CS2Fixes](https://github.com/Source2ZE/CS2Fixes) 的签名调用方式，
把原先散落的裸偏移/RVA 硬编码收敛为三层：

```
SDK\Native\
  Module.cs        CModule 等价物：Load / Base / Size / Sections /
                     FindInterface(CreateInterface) / FindSignature(逐字节+通配) /
                     FindUniqueSignature(唯一命中，否则抛) /
                     ResolveCallTargetRva(定位调用方 + 提取 E8 rel32)
  signatures.json  gamedata 等价物：**命名签名表（JSON 数据维护）**——模块 + windows 字节模式 + 如何重新找到它
  Signatures.cs    上表的加载器（System.Text.Json 解析；API 不变）
  Functions.cs     addresses.h/cpp 等价物：强类型委托声明 + 单一 Initialize(gameBinDir)
```

### 8.1 设计要点

* **强类型委托**：每个函数一条 delegate，参数用 `nint` / `int` / `uint` / `bool` 等真实类型，
  不再用 `new Action(nint,int,int,int)` 式泛化冒充。
* **单一 Initialize()**：一处集中绑定全部函数，未找到即 fail-fast，
  逐条记录 `Found <name> (<module>) at 0x<rva>` 到 `Functions.ResolveLog` 供复核。
* **签名是 JSON 数据不是代码**（t24 按用户指示）：签名表在 `SDK\signatures.json`，
  改签名只编辑 JSON、不必动代码。加载顺序：**① 程序输出目录的 `signatures.json`**
  （随构建复制过去，改它重启即生效、免重编译）→ **② SDK.dll 内嵌资源**（保底，文件缺失仍可用）。
  维护流程：编辑 `SDK\signatures.json` → 重新构建（或手工复制到程序输出目录）→ 重启；
  自检的 `Found … at 0x…` 五行即解析结果，`KV3 path:` 行区分原生链/回退读取器。
* **签名而非 RVA**：内部函数按代码形状定位（`?` 通配），游戏更新平移后仍能命中；
  导出函数仍走 `GetProcAddress`，接口走 `CreateInterface`。
* **每条签名带「如何重新找到它」注释**：依据反编译结论 / 字符串引用 / 调用方，
  与 CS2Fixes 的 gamedata 注释风格一致。

### 8.2 签名表（已实测唯一性）

| 签名 | 模块 | 命中 | 重新找到它的依据 |
|---|---|---|---|
| `ArenaCtor` | resourcecompiler | 1 处 `0x1EAA400` | CKV3Arena 内层构造器；内串 `mov [rsi+0x10xx],rdi` 零写 arena+4168..4312 |
| `GenerateResourceFile` | resourcecompiler | 1 处 `0x188B90` | 起点是四个参数落栈序列；失效时走系统对象 vtable |
| `AssetConfig_Init` | resourcecompiler | 1 处 `0x187EA0` | `push rbx; sub rsp,50h; cmp [rcx+38],0`（幂等检查） |
| `LoadAssetTypesCommonTxt` | resourcecompiler | 参考实现 | 搜字符串 `assettypes_common.txt` 的 xref |

**多内联副本函数的处理**：`SymTabPoolInit` 的函数体在 resourcecompiler 里被**多处内联
展开**——28 字节序言模式实测命中 **10 处**（`0xC84F0`/`0x1FBC50`/`0x1FBDF0`/`0x1FBF90`/
`0xD37DA0`/`0xF38FC0`/`0x13B2100`/`0x190C120`/`0x1FA4DB0`/`0x1FCCDD0`）。它们只共享
**29 字节**序言，随后在 `E8 rel32`（调用目标）与多处 RIP 相对位移上分叉
（hit0 vs hit1 在 512 字节内 **111 字节**不同）——是「同一内联代码的不同相对位移副本」，
**不是字节完全相同**。纯模式无法区分 ⇒ 改用
「定位 `ArenaCtor`（唯一）+ 提取 `+0xF8` 处 `E8 rel32`」的 CS2Fixes 式做法，
由 `Module.ResolveCallTargetRva` 完成。实测解析结果 `0xC84F0`，与参考实现一致。

### 8.3 fail-fast 语义

| 情况 | 行为 |
|---|---|
| 签名零命中 | 抛 `SignatureNotFoundException`（含签名名、模块、模式） |
| 签名多命中 | 抛 `SignatureAmbiguousException`（含全部命中 RVA） |
| 导出缺失 | 抛 `FunctionResolveException` |
| 未初始化就调用 | `Functions.EnsureInitialized()` 抛错 |

宁可明确失败，也不静默用错地址——这是本仓库从「游戏一更新、anchors 全失效」事故里
学到的教训。

### 8.4 裸偏移的收敛

`Kv3NativeArena` 里的 arena / CUtlBuffer / CUtlString / KV3ID_t 已改为
`[StructLayout(LayoutKind.Explicit)]` 结构体（显式 `FieldOffset`），用
`Marshal.PtrToStructure` / `StructureToPtr` 访问；arena 的 20+ 个字段偏移收敛为
一组具名常量（`OFF_SELF` / `OFF_FREELIST` / `OFF_NODECAP` / `OFF_FLAGS` …），
可读、可核对、可单点修改。

### 8.5 复现

```powershell
dotnet build projectsCS2-ToolsCS2-Tools.sln -c Release
# 自检会打印 Functions.Initialize 的解析日志：
#   Found CMemAllocSystemInitialize (tier0) at 0x12E1E0
#   Found ArenaCtor (resourcecompiler) at 0x1EAA400
#   Found GenerateResourceFile (resourcecompiler) at 0x188B90
#   Found AssetConfig_Init (resourcecompiler) at 0x187EA0
#   Found SymTabPoolInit(via ArenaCtor+F8) (resourcecompiler) at 0xC84F0
```



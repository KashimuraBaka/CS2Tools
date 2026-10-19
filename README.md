# CS2-Tools

Counter-Strike 2 资源管线工具集（.NET 10 / WPF）。所有 KeyValues（KV1/KV3）解析与写出
**一律使用游戏自带的 tier0 原生函数**（经本仓 SDK 封装），不依赖任何三方 KV 库。

## 组成

| 工程 | 说明 |
| --- | --- |
| [SDK](SDK/) | tier0 / resourcecompiler 原生互操作层：签名扫描（CS2Fixes 范式）、KV3 原生链、KV1 读写、编译注册表 |
| [VmatLib](VmatLib/) | .vmat 材质生成核心库：贴图语义槽位、后缀规则、着色器模板（11 个出厂模板） |
| [VmatGenerator](VmatGenerator/) | .vmat 生成器 GUI（三步向导 + 拖拽导入） |
| [AssetCompilerWpf](AssetCompilerWpf/) | 资产编译 GUI：读 assettypes_common.txt（tier0 原生链）、编译 vmdl/vdata/vnmgraph |
| [CompileHost](CompileHost/) | 编译宿主进程（与 GUI 隔离，防引擎崩溃波及 UI） |

## 构建

```powershell
dotnet build CS2-Tools.sln -c Release
```

要求：Windows x64 + .NET 10 SDK / Desktop Runtime。游戏目录（含 `bin/win64/tier0.dll` 等）
只读加载，不修改。

## 设计要点

- **签名驱动调用**：内部函数按字节签名定位（`SDK/Native/signatures.json`，CS2Fixes gamedata 范式），
  导出函数走 GetProcAddress，全部集中在 `Functions.Initialize` 单一入口 fail-fast。
- **KV 全原生**：读 `LoadKV3FromKV3OrKV1` / 写 `SaveKV3AsKV1Text`（tier0 导出），
  中间经引擎自带 JSON 序列化桥接（`SaveKV3AsJSON` + System.Text.Json）。
- **进程隔离**：编译在独立宿主进程中进行，宿主崩溃不影响 GUI。

## 致谢

VmatGenerator 由 [KashimuraBaka/CS2Tools](https://github.com/KashimuraBaka/CS2Tools) 的
GUI/Lib 工程整合而来（t24）：KV 层从三方库 ValveKeyValue 改为 tier0 原生函数，
UI 层（CommunityToolkit.Mvvm）保持原样。

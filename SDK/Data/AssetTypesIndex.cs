// ============================================================================
// SDK.Data.AssetTypesIndex —— assettypes_common.txt 资产类型域的分类枚举
//
// 来源：A 的 t10 产物（71 行 × 8 列）分析出的**取值域**。t22 按用户逐条指示的终态：
//   ① CSV 沉入 SDK 代码 → ②「写成枚举 + Attribute」→ ③「AssetType 枚举类型多余」
//   → ④「本文件仅保留枚举部分，枚举禁止写在一行」⇒ **本文件只保留四个分类枚举**。
//   71 行索引数据（CSV 字符串 / 枚举+特性 / 强类型记录，三种形态）与以其为基准的自校验
//   已全部移除——游戏自带的 assettypes_common.txt 是唯一正本，工具加载后即得全部数据。
//   这四个枚举是**类型定义**（取值域），不是数据副本；UI 模型 / 编译链路要强类型化时直接用。
// 注：m_BlockIDs / m_Encodings 在源配置里是分号列表且**含重复 token**（如 DATA;DATA）；
//     Flags 枚举按**集合语义**归并（重复不计数）。
// ============================================================================
namespace SDK.Data;

/// <summary>资产类型条目的 _class（4 种）。</summary>
public enum AssetTypeClass
{
    CBitmapAssetTypeInfo,
    CMapAssetTypeInfo,
    CResourceAssetTypeInfo,
    CSimpleAssetTypeInfo,
}

/// <summary>m_CompilerIdentifier（27 种；None = 该类型不带编译器）。</summary>
public enum ResourceCompilerId
{
    None = 0,
    CompileAnimgraph,
    CompileCS2EconItem,
    CompileEntityLump,
    CompileJavaScript,
    CompileMap,
    CompileMapVisibility,
    CompileMaterial,
    CompileModel,
    CompileNmClip,
    CompileNmGraph,
    CompileNmSkeleton,
    CompilePanorama,
    CompileParticle,
    CompilePostProcessing,
    CompileResourceManifest,
    CompileResourceRemapTable,
    CompileSnap,
    CompileSound,
    CompileSoundEventScript,
    CompileSoundStackScript,
    CompileSurfaceProperties,
    CompileTexture,
    CompileVCompMat,
    CompileVData,
    CompileVectorGraphic,
    CompileWorld,
    CompileWorldNode,
}

/// <summary>m_BlockIDs 的 token 集合（Flags；源配置分号列表的重复 token 归并为集合语义）。</summary>
[Flags]
public enum BlockId
{
    None = 0,
    AGRP = 1,
    ANIM = 2,
    ASEQ = 4,
    CTRL = 8,
    DATA = 16,
    DNAR = 32,
    DSTF = 64,
    Data = 128,
    INSG = 256,
    LaCo = 512,
    MADJ = 1024,
    MBUF = 2048,
    MDAT = 4096,
    MIDX = 8192,
    MRPH = 16384,
    MSLT = 32768,
    MVTX = 65536,
    OLDKV = 131072,
    PHYS = 262144,
    SNAP = 524288,
    SrMa = 1048576,
    StCo = 2097152,
    TBUF = 4194304,
    VXVS = 8388608,
}

/// <summary>m_Encodings 的 token 集合（Flags；同上）。</summary>
[Flags]
public enum ResourceEncoding
{
    None = 0,
    RESOURCE_ENCODING_COMPILEIMAGEUTILS_TEXT = 1,
    RESOURCE_ENCODING_INTROSPECTED = 2,
    RESOURCE_ENCODING_KV3 = 4,
    RESOURCE_ENCODING_LEGACY_VSND = 8,
    RESOURCE_ENCODING_MBUF = 16,
    RESOURCE_ENCODING_MIDX = 32,
    RESOURCE_ENCODING_MSLT = 64,
    RESOURCE_ENCODING_MVTX = 128,
    RESOURCE_ENCODING_RAW_BYTES = 256,
    RESOURCE_ENCODING_TEXT = 512,
    RESOURCE_ENCODING_VRMAN = 1024,
    RESOURCE_ENCODING_VSNAP = 2048,
    RESOURCE_ENCODING_VTEX = 4096,
}

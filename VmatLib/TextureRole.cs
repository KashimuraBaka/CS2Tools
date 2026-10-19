namespace VmatLib;

/// <summary>
/// 与具体着色器无关的贴图语义槽位（详见 <c>docs/feature-dragdrop-suffix-spec.md</c> §5.2）。
///
/// 槽位是「后缀规则」与「着色器参数键」之间的中间层：后缀规则把文件名后缀映射到本枚举，
/// <see cref="TextureRoleResolver"/> 再把本枚举映射到某个具体 <see cref="ShaderTemplate"/>
/// 上真实存在的参数键。这样规则表才能与着色器解耦——同一条 <c>_normal → Normal</c>
/// 规则在 <c>csgo_environment</c> 上落到 <c>TextureNormal1</c>，在
/// <c>csgo_lightmappedgeneric</c> 上落到 <c>TextureLayer1Normal</c>。
///
/// <para>枚举值顺序即种子规则表与 GUI 下拉框的默认展示顺序，仅<b>追加</b>成员，
/// 不重排既有值（0 必须是 <see cref="Unknown"/>，以便 <c>default(TextureRole)</c> 落在安全值上）。
/// 配置文件按<b>枚举名</b>序列化，因此追加成员不会影响既有 <c>settings.json</c>。</para>
/// </summary>
public enum TextureRole
{
    /// <summary>未识别：后缀未命中任何规则，或用户显式把某条规则指向本值。</summary>
    Unknown = 0,

    /// <summary>颜色 / 反照率（主 Token <c>Color</c>）。</summary>
    Color,

    /// <summary>法线（主 Token <c>Normal</c>）。</summary>
    Normal,

    /// <summary>粗糙度（主 Token <c>Roughness</c>）。</summary>
    Roughness,

    /// <summary>金属度（主 Token <c>Metalness</c>）。</summary>
    Metalness,

    /// <summary>环境光遮蔽（主 Token <c>AmbientOcclusion</c>）。</summary>
    AmbientOcclusion,

    /// <summary>高度 / 位移（主 Token <c>Height</c>）。</summary>
    Height,

    /// <summary>半透明度（主 Token <c>Translucency</c>）。</summary>
    Translucency,

    /// <summary>细节层漫反射（主 Token <c>Detail</c>）。</summary>
    Detail,

    /// <summary>通用遮罩（主 Token <c>Mask</c>）。</summary>
    Mask,

    /// <summary>自发光（主 Token <c>Emissive</c>）。</summary>
    Emissive,

    /// <summary>Cube 贴图 / 天空盒（主 Token <c>CubeMap</c>）。</summary>
    CubeMap,

    /// <summary>烘焙光照贴图（主 Token <c>Lightmap</c>）。</summary>
    Lightmap,

    /// <summary>泡沫遮罩（主 Token <c>Foam</c>），对应 <c>csgo_water_fancy</c> 的 <c>TextureFoam</c>。</summary>
    FoamMask,

    /// <summary>泡沫法线（主 Token <c>FoamNormal</c>），对应 <c>TextureFoamNormal</c>。</summary>
    FoamNormal,

    /// <summary>波浪遮罩（主 Token <c>Waves</c>）。</summary>
    WavesMask,

    /// <summary>波浪法线（主 Token <c>WavesNormal</c>），对应 <c>TextureWavesNormal</c>。</summary>
    WavesNormal,

    /// <summary>波浪高度（主 Token <c>WavesHeight</c>），对应 <c>TextureWavesHeight</c>。</summary>
    WavesHeight,

    /// <summary>漂浮物颜色（主 Token <c>Debris</c>），对应 <c>TextureDebris</c>。</summary>
    DebrisColor,

    /// <summary>漂浮物法线（主 Token <c>DebrisNormal</c>），对应 <c>TextureDebrisNormal</c>。</summary>
    DebrisNormal,

    /// <summary>漂浮物高度（主 Token <c>DebrisHeight</c>），对应 <c>TextureDebrisHeight</c>。</summary>
    DebrisHeight,

    /// <summary>染色遮罩（主 Token <c>TintMask</c>），对应 <c>TextureTintMask1</c>。</summary>
    TintMask,

    /// <summary>
    /// 细节遮罩（主 Token <c>DetailMask</c>），对应 <c>TextureDetailMask</c>。
    ///
    /// <para>主 Token 取完整复合名而非 <c>Detail</c>：与 <see cref="SelfIllumMask"/> 同理，
    /// 取通用短名会让 channel 精确相等与结尾判定双双落空，重新掉回多候选歧义。</para>
    /// </summary>
    DetailMask,

    /// <summary>
    /// 自发光遮罩（主 Token <c>SelfIllumMask</c>），对应 <c>TextureSelfIllumMask</c>。
    ///
    /// <para><b>主 Token 必须是 <c>SelfIllumMask</c> 而不是 <c>SelfIllum</c></b>：
    /// 本槽位要靠 <b>P4（派生通道名精确相等）</b> 命中 <c>TextureSelfIllumMask</c>。
    /// 若把主 Token 写成 <c>SelfIllum</c>，该键只会落进 P5 并与 <c>TextureDetailMask</c>
    /// 并列成多候选，于是「用户想给自发光遮罩改名」这条出路又被堵死，问题原地复发。</para>
    /// </summary>
    SelfIllumMask,

    /// <summary>边缘光遮罩（主 Token <c>RimMask</c>），对应 <c>csgo_character</c> 的 <c>TextureRimMask</c>。</summary>
    RimMask,

    /// <summary>
    /// 低端 Cube Map（主 Token <c>LowEndCubeMap</c>），对应
    /// <c>csgo_water_fancy</c> 的 <c>TextureLowEndCubeMap</c>。
    /// </summary>
    LowEndCubeMap,

    /// <summary>毛发遮罩（主 Token <c>HairMask</c>），对应 <c>csgo_character</c> 的 <c>TextureHairMask</c>。</summary>
    HairMask,

    /// <summary>次表面散射遮罩（主 Token <c>SssMask</c>），对应 <c>csgo_character</c> 的 <c>TextureSssMask</c>。</summary>
    SssMask,

    /// <summary>逆反射遮罩（主 Token <c>RetroReflectiveMask</c>），对应 <c>csgo_character</c> 的 <c>TextureRetroReflectiveMask</c>。</summary>
    RetroReflectiveMask,

    /// <summary>细节法线（主 Token <c>NormalDetail</c>），对应 <c>csgo_environment</c> 的 <c>TextureNormalDetail1</c>。</summary>
    NormalDetail,

    /// <summary>贴花半透明（主 Token <c>DecalTranslucency</c>），对应 <c>TextureDecalTranslucency</c>。</summary>
    DecalTranslucency,
}
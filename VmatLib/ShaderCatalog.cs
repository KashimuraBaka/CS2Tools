namespace VmatLib;

/// <summary>
/// 已知着色器的目录。数据驱动：<see cref="All"/> 由 <c>VmatLib/Templates/*.vmat</c>
/// 内嵌模板在运行时<b>解析构建</b>，模板文件是唯一的真值来源——参数、默认值、
/// feature flag（含出厂值）、Compiled Textures 与 Attributes 全部来自模板本身，
/// 编辑模板即等于更新目录，无需改代码。
///
/// <para>本类只保留模板文本<b>表达不了</b>的展示与语义元数据（中文显示名/说明/参数标签、
/// 参数分组门控 <see cref="ShaderParamTemplate.RequiredFeatureFlag"/>、描述性分类标签
/// <c>AttributeFlags</c>），作为<em>标注叠加层</em>按模板基名合并进解析结果。
/// 标注只允许引用模板中真实存在的键与 flag：<see cref="ValidateTemplate"/> 会拒绝
/// 引用悬空的标注，杜绝旧版「目录与模板各说各话」（过期 flag、占位默认值）的问题复发。</para>
///
/// <para><see cref="Find"/> 同时接受 <c>csgo_environment.vfx</c>（写出文件里
/// <c>shader</c> 指令的值）与 <c>csgo_environment</c>（模板资源基名）。</para>
/// </summary>
public static class ShaderCatalog
{
    private const string Extension = ".vfx";

    /// <summary>标注叠加层：键为模板基名（不带 <c>.vmat</c>）。</summary>
    private sealed record Overlay(
        string DisplayName,
        string Description,
        IReadOnlyDictionary<string, string> ParamLabels,
        IReadOnlyDictionary<string, string> ParamFlagGates,
        IReadOnlyList<string> AttributeFlags);

    /// <summary>
    /// 全部着色器模板，按模板基名排序，构建保证不会抛异常。
    /// 声明为 <c>Lazy</c>：求值必然发生在 <see cref="Overlays"/> 等静态字段
    /// 初始化之后，避免静态构造顺序引发整表构建失败。
    /// </summary>
    private static readonly Lazy<IReadOnlyList<ShaderTemplate>> LazyAll = new(Build);

    /// <summary>全部着色器模板，按模板基名排序，构建保证不会抛异常。</summary>
    public static IReadOnlyList<ShaderTemplate> All => LazyAll.Value;

    /// <summary>按 <c>shader</c> 指令值（带或不带 <c>.vfx</c>）查找模板；找不到返回 <c>null</c>。</summary>
    public static ShaderTemplate? Find(string shaderName)
    {
        if (string.IsNullOrWhiteSpace(shaderName)) return null;
        foreach (var s in All)
        {
            if (string.Equals(s.ShaderName, shaderName, StringComparison.OrdinalIgnoreCase)) return s;
            if (string.Equals(s.TemplateResourceName, shaderName, StringComparison.OrdinalIgnoreCase)) return s;
        }
        return null;
    }

    /// <summary>加载某个模板的内嵌原文（带基名解析）；不存在返回 <c>null</c>。</summary>
    public static string? LoadTemplateText(string shaderName)
    {
        var shader = Find(shaderName);
        return shader is null ? null : ShaderTemplateEmitter.LoadEmbedded(shader.TemplateResourceName);
    }

    // ---- 构建：解析 → 标注叠加 → 模板完整性校验 ----

    private static IReadOnlyList<ShaderTemplate> Build()
    {
        var list = new List<ShaderTemplate>();
        foreach (var (resourceName, text) in ShaderTemplateEmitter.EnumerateEmbedded())
        {
            try
            {
                var shader = BuildOne(resourceName, text);
                ValidateTemplate(resourceName, shader, text);
                list.Add(shader);
            }
            catch
            {
                // 目录构建与静态字段初始化绝不抛异常：坏模板 / 坏标注一律整条跳过，
                // GUI 顶多少一个条目，而不是炸在类型初始化上。具体原因由自检用例负责
                // 暴露（见 TextureAssignmentSelfTest 的模板一致性用例）。
            }
        }

        // 兜底：内嵌资源全部缺失时至少按目录名给出空壳条目，Find / Fallback 契约不崩。
        if (list.Count == 0)
        {
            foreach (var name in FallbackBasenames)
            {
                list.Add(new ShaderTemplate(name + Extension, name, name, [], []));
            }
        }

        list.Sort((a, b) => string.CompareOrdinal(a.TemplateResourceName, b.TemplateResourceName));
        return list;
    }

    private static ShaderTemplate BuildOne(string resourceName, string text)
    {
        var parsed = TemplateParser.Parse(resourceName, text);
        var overlay = Overlays.TryGetValue(resourceName, out var o) ? o : null;

        // 只保留解析出的 key 上存在的门控引用；悬空引用由校验器直接拒绝整条模板。
        var flagGates = new Dictionary<string, string>(StringComparer.Ordinal);
        if (overlay is not null)
        {
            foreach (var p in parsed.Parameters)
            {
                if (overlay.ParamFlagGates.TryGetValue(p.Key, out var gate))
                    flagGates[p.Key] = gate;
            }
        }

        var parameters = new List<ShaderParamTemplate>(parsed.Parameters.Count);
        foreach (var p in parsed.Parameters)
        {
            var label = overlay?.ParamLabels.GetValueOrDefault(p.Key) ?? p.Key;
            parameters.Add(new ShaderParamTemplate(p.Key, label, p.Kind, p.DefaultValue)
            {
                Shape = p.Shape,
                RequiredFeatureFlag = flagGates.GetValueOrDefault(p.Key),
            });
        }

        return new ShaderTemplate(
            parsed.ShaderValue,
            overlay?.DisplayName ?? resourceName,
            overlay?.Description ?? string.Empty,
            parameters,
            parsed.FeatureFlags)
        {
            TemplateResourceName = resourceName,
            FeatureFlagDefaults = parsed.FeatureFlagDefaults,
            TemplateAttributes = parsed.TemplateAttributes,
            AttributeFlags = overlay?.AttributeFlags ?? [],
            SystemAttributeDefaults = parsed.SystemAttributeDefaults,
            CompiledTextureKeys = parsed.CompiledTextureKeys,
        };
    }

    /// <summary>
    /// 模板完整性校验：任何一条不满足都抛 <see cref="InvalidOperationException"/>，
    /// 由 <see cref="Build"/> 吞掉并跳过该条目。
    /// </summary>
    private static void ValidateTemplate(string resourceName, ShaderTemplate shader, string text)
    {
        var layer = TemplateParser.FindLayer(VmatFormat.Parse(text))
                    ?? throw new InvalidOperationException($"{resourceName}: 缺少 Layer0。");

        // 1) shader 指令存在，并与资源基名一致（允许带 .vfx 后缀）。
        var shaderValue = layer.FindChild("shader")?.Value;
        if (string.IsNullOrWhiteSpace(shaderValue))
            throw new InvalidOperationException($"{resourceName}: 缺少 shader 指令。");
        var expectedValue = shaderValue.EndsWith(Extension, StringComparison.Ordinal) ? shaderValue : shaderValue + Extension;
        if (!string.Equals(resourceName + Extension, expectedValue, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{resourceName}: shader 指令为 {shaderValue}，与资源基名不符。");

        // 2) flag 出厂值齐全且只能是 0/1——「合并后的设置保持默认值」的硬约束。
        foreach (var flag in shader.FeatureFlags)
        {
            var raw = layer.FindChild(flag)?.Value
                      ?? throw new InvalidOperationException($"{resourceName}: flag {flag} 无法解析出厂值。");
            if (raw is not "0" and not "1")
                throw new InvalidOperationException($"{resourceName}: flag {flag} 出厂值应为 0 或 1，实际为 {raw}。");
        }

        // 3) 参数键不得重复（TextureMask1/2/3 这类编号变体是合法的同通道多候选，不算重复）。
        var keys = shader.Parameters.Select(p => p.Key).ToList();
        if (keys.Count != keys.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidOperationException($"{resourceName}: 参数键重复。");

        // 4) 标注叠加层的门控引用的每个键 / flag 都必须真实存在——模板改动后忘改标注会立刻暴露。
        //    标签（ParamLabels）是纯展示元数据、且跨模板共用一张公共表，缺失的键在
        //    BuildOne 里天然不生效，因此不设存在性约束；门控才需要，因为它决定 UI 分组。
        if (Overlays.TryGetValue(resourceName, out var overlay))
        {
            var flagSet = shader.FeatureFlags.ToHashSet(StringComparer.Ordinal);
            foreach (var (key, gate) in overlay.ParamFlagGates)
            {
                if (layer.FindChild(key) is null)
                    throw new InvalidOperationException($"{resourceName}: 门控标注引用了模板不存在的参数 {key}。");
                if (!flagSet.Contains(gate))
                    throw new InvalidOperationException($"{resourceName}: 门控标注 {key} -> {gate}，但模板没有 flag {gate}。");
            }
        }
    }

    // ---- 标注字典构造辅助 ----

    private static Dictionary<string, string> Map(params (string Key, string Value)[] items) => items.ToDictionary(i => i.Key, i => i.Value, StringComparer.Ordinal);

    /// <summary>展示性中文标签的公共词条（跨模板复用；各模板再叠加专属词条）。</summary>
    private static readonly (string Key, string Value)[] SharedLabels =
    [
        ("g_bFogEnabled", "启用雾效"),
        ("g_flModelTintAmount", "模型染色强度"),
        ("g_flTexCoordRotation", "纹理坐标旋转"),
        ("g_flAlphaTestReference", "Alpha 测试阈值"),
        ("g_flAntiAliasedEdgeStrength", "抗锯齿边缘强度"),
        ("g_nScaleTexCoordUByModelScaleAxis", "U 坐标按模型缩放"),
        ("g_nScaleTexCoordVByModelScaleAxis", "V 坐标按模型缩放"),
        ("g_nTextureAddressModeU", "U 寻址模式"),
        ("g_nTextureAddressModeV", "V 寻址模式"),
        ("g_vColorTint", "颜色染色"),
        ("g_vTexCoordCenter", "纹理坐标中心"),
        ("g_vTexCoordOffset", "纹理坐标偏移"),
        ("g_vTexCoordScale", "纹理坐标缩放"),
        ("g_vTexCoordScrollSpeed", "纹理坐标滚动速度"),
        ("g_bUseSecondaryUvForTintMask", "染色遮罩使用第二 UV"),
        ("g_bUseSecondaryUvForDecal", "贴花使用第二 UV"),
        ("g_bUseSecondaryUvForSelfIllum", "自发光使用第二 UV"),
        ("g_bUseSecondaryUvForAmbientOcclusion", "AO 使用第二 UV"),
        ("g_flSelfIllumAlbedoFactor", "自发光反照率系数"),
        ("g_flSelfIllumBrightness", "自发光亮度"),
        ("g_flSelfIllumScale", "自发光缩放"),
        ("g_vSelfIllumScrollSpeed", "自发光滚动速度"),
        ("g_vSelfIllumTint", "自发光染色"),
        ("g_flAnimationFrame", "动画帧"),
        ("g_flAnimationTimeOffset", "动画时间偏移"),
        ("g_flAnimationTimePerFrame", "每帧动画时长"),
        ("g_nNumAnimationCells", "动画格数"),
        ("g_vAnimationGrid", "动画网格"),
        ("TextureColor", "颜色 / 反照率"),
        ("TextureNormal", "法线贴图"),
        ("TextureRoughness", "粗糙度"),
        ("TextureMetalness", "金属度"),
        ("TextureAmbientOcclusion", "环境光遮蔽"),
        ("TextureTintMask", "染色遮罩"),
        ("TextureSelfIllumMask", "自发光遮罩"),
        ("TextureTranslucency", "半透明度"),
        ("TextureDecal", "贴花"),
        ("TextureDecalTranslucency", "贴花半透明"),
    ];

    /// <summary>公共词条 + 本模板专属词条（专属词条覆盖同名公共词条）。</summary>
    private static Dictionary<string, string> Labels(params (string Key, string Value)[] extra)
    {
        var dict = Map(SharedLabels);
        foreach (var (key, value) in extra)
            dict[key] = value;
        return dict;
    }

    // ---- 标注叠加层（只有展示元数据；数据本体一律来自模板） ----

    private static readonly Dictionary<string, Overlay> Overlays = new(StringComparer.Ordinal)
    {
        ["csgo_character"] = new Overlay(
            "CS:GO 角色",
            "玩家 / NPC 模型的皮肤着色器。在标准 PBR 基础上增加边缘光、布料、毛发、逆反射、次表面散射、贴花与贴片（Patch）控制。",
            Labels(
                ("TextureRimMask", "边缘光遮罩"),
                ("TextureCloth", "布料遮罩"),
                ("TextureHairMask", "毛发遮罩"),
                ("TextureRetroReflectiveMask", "逆反射遮罩"),
                ("TextureSssMask", "次表面散射遮罩"),
                ("TextureDiffuseFalloff", "漫反射衰减"),
                ("TextureColorPatch0", "贴片 0 颜色"),
                ("TextureColorPatch1", "贴片 1 颜色"),
                ("TextureColorPatch2", "贴片 2 颜色"),
                ("TexturePatch0Backing", "贴片 0 衬底"),
                ("TexturePatch1Backing", "贴片 1 衬底"),
                ("TexturePatch2Backing", "贴片 2 衬底"),
                ("g_flAmbientOcclusionMasking", "AO 遮蔽"),
                ("g_flHairGlossRoughnessScale", "毛发光泽粗糙度缩放"),
                ("g_flHairGlossShift", "毛发光泽偏移"),
                ("g_flHairTransmission", "毛发透射"),
                ("g_flSheenScale", "织物光泽强度"),
                ("g_flSheenTintColor", "织物光泽染色"),
                ("g_flCurvatureScale", "曲率缩放"),
                ("g_vNormalSoftness", "法线柔和度"),
                ("g_fDistanceContrastExposure", "距离对比度曝光"),
                ("g_vSphericalAnisotropyAngle", "球面各向异性角"),
                ("g_vSphericalAnisotropyPole", "球面各向异性极"),
                ("g_bEnablePatch0", "启用贴片 0"),
                ("g_bEnablePatch1", "启用贴片 1"),
                ("g_bEnablePatch2", "启用贴片 2"),
                ("g_flPatch0Scale", "贴片 0 缩放"),
                ("g_flPatch1Scale", "贴片 1 缩放"),
                ("g_flPatch2Scale", "贴片 2 缩放"),
                ("g_flPatch0Rotation", "贴片 0 旋转"),
                ("g_flPatch1Rotation", "贴片 1 旋转"),
                ("g_flPatch2Rotation", "贴片 2 旋转"),
                ("g_flPatch0Squash", "贴片 0 压扁"),
                ("g_flPatch1Squash", "贴片 1 压扁"),
                ("g_flPatch2Squash", "贴片 2 压扁"),
                ("g_flPatch0BackingScale", "贴片 0 衬底缩放"),
                ("g_flPatch1BackingScale", "贴片 1 衬底缩放"),
                ("g_flPatch2BackingScale", "贴片 2 衬底缩放"),
                ("g_vPatch0Offset", "贴片 0 偏移"),
                ("g_vPatch1Offset", "贴片 1 偏移"),
                ("g_vPatch2Offset", "贴片 2 偏移")),
            Map(
                ("TextureTintMask", "F_TINT_MASK"),
                ("g_bUseSecondaryUvForTintMask", "F_TINT_MASK"),
                ("TextureDecal", "F_DECAL_TEXTURE"),
                ("TextureDecalTranslucency", "F_DECAL_TEXTURE"),
                ("g_bUseSecondaryUvForDecal", "F_DECAL_TEXTURE"),
                ("TextureCloth", "F_CLOTH_SHADING"),
                ("g_flSheenScale", "F_CLOTH_SHADING"),
                ("g_flSheenTintColor", "F_CLOTH_SHADING"),
                ("TextureHairMask", "F_ANISOTROPIC_HAIR"),
                ("g_flHairGlossRoughnessScale", "F_ANISOTROPIC_HAIR"),
                ("g_flHairGlossShift", "F_ANISOTROPIC_HAIR"),
                ("g_flHairTransmission", "F_ANISOTROPIC_HAIR"),
                ("TextureRetroReflectiveMask", "F_RETRO_REFLECTIVE"),
                ("TextureSssMask", "F_SUBSURFACE_SCATTERING"),
                ("g_flCurvatureScale", "F_SUBSURFACE_SCATTERING"),
                ("TextureDiffuseFalloff", "F_SUBSURFACE_SCATTERING"),
                ("g_vSphericalAnisotropyAngle", "F_SPHERICAL_PROJECTED_ANISOTROPIC_TANGENTS"),
                ("g_vSphericalAnisotropyPole", "F_SPHERICAL_PROJECTED_ANISOTROPIC_TANGENTS"),
                ("g_fDistanceContrastExposure", "F_DISTANCE_CONTRAST_ADJUSTMENT")),
            ["mapbuilder.character"]),

        ["csgo_complex"] = new Overlay(
            "CS:GO 复合材质",
            "用于半透明 / 自发光道具（灯具、黏液、防化服）的 PBR 材质。在环境材质基础上增加金属度、半透明、自发光与贴图动画控制。",
            Labels(
                ("g_flMetalness", "金属度常量"),
                ("g_flOcclusionCullingBoundsScale", "遮挡剔除包围盒缩放"),
                ("g_bUseSecondaryUvForAmbientOcclusion", "AO 使用第二 UV")),
            Map(
                ("TextureTintMask", "F_TINT_MASK"),
                ("g_bUseSecondaryUvForTintMask", "F_TINT_MASK"),
                ("TextureSelfIllumMask", "F_SELF_ILLUM"),
                ("g_bUseSecondaryUvForSelfIllum", "F_SELF_ILLUM"),
                ("g_flSelfIllumAlbedoFactor", "F_SELF_ILLUM"),
                ("g_flSelfIllumBrightness", "F_SELF_ILLUM"),
                ("g_flSelfIllumScale", "F_SELF_ILLUM"),
                ("g_vSelfIllumScrollSpeed", "F_SELF_ILLUM"),
                ("g_vSelfIllumTint", "F_SELF_ILLUM"),
                ("TextureMetalness", "F_METALNESS_TEXTURE"),
                ("g_flAnimationFrame", "F_TEXTURE_ANIMATION"),
                ("g_flAnimationTimeOffset", "F_TEXTURE_ANIMATION"),
                ("g_flAnimationTimePerFrame", "F_TEXTURE_ANIMATION"),
                ("g_nNumAnimationCells", "F_TEXTURE_ANIMATION"),
                ("g_vAnimationGrid", "F_TEXTURE_ANIMATION"),
                ("g_flOcclusionCullingBoundsScale", "F_OCCLUSION_CULLING_BOUNDS_SCALE")),
            []),

        ["csgo_effects"] = new Overlay(
            "CS:GO 特效",
            "面向粒子 / 发光的着色器，含颜色染色、三组独立遮罩缩放、深度羽化与菲涅尔衰减。用于加法发光覆盖层。",
            Labels(
                ("g_flColorBoost", "颜色增强"),
                ("g_flFeatherDistance", "羽化距离"),
                ("g_flFeatherFalloff", "羽化衰减"),
                ("g_flFadeDistance", "渐隐距离"),
                ("g_flFadeFalloff", "渐隐衰减"),
                ("g_flFadeMax", "渐隐最大"),
                ("g_flFadeMin", "渐隐最小"),
                ("g_flOpacityScale", "不透明度缩放"),
                ("g_flFresnelExponent", "菲涅尔指数"),
                ("g_flFresnelFalloff", "菲涅尔衰减"),
                ("g_flFresnelMax", "菲涅尔最大"),
                ("g_flFresnelMin", "菲涅尔最小"),
                ("g_vMask1PanSpeed", "遮罩 1 平移速度"),
                ("g_vMask1Scale", "遮罩 1 缩放"),
                ("g_vMask2PanSpeed", "遮罩 2 平移速度"),
                ("g_vMask2Scale", "遮罩 2 缩放"),
                ("g_vMask3PanSpeed", "遮罩 3 平移速度"),
                ("g_vMask3Scale", "遮罩 3 缩放"),
                ("TextureMask1", "遮罩 1"),
                ("TextureMask2", "遮罩 2"),
                ("TextureMask3", "遮罩 3")),
            Map(
                ("TextureTintMask", "F_TINT_MASK"),
                ("g_flFeatherDistance", "F_DEPTH_FEATHER"),
                ("g_flFeatherFalloff", "F_DEPTH_FEATHER")),
            ["mapbuilder.effect"]),

        ["csgo_environment"] = new Overlay(
            "CS:GO 环境材质",
            "用于接受图像光照（IBL）的实体几何 PBR 材质。本资源中最常用的着色器，支持漫反射 / 法线 / 粗糙度 / 金属度 / AO / 高度 / 染色遮罩与湿润（Puddle）层。",
            Labels(
                ("g_bSnowLayer1", "第一层积雪"),
                ("g_flWetnessDarkeningStrength1", "湿润变暗强度 1"),
                ("g_flDetailTexCoordRotation1", "细节纹理坐标旋转 1"),
                ("g_fDetailTextureNormalContrast1", "细节法线对比度 1"),
                ("g_flTexCoordRotation1", "纹理坐标旋转 1"),
                ("g_nDetailUVSet1", "细节 UV 集 1"),
                ("g_nUVSet1", "UV 集 1"),
                ("g_vDetailTexCoordCenter1", "细节纹理坐标中心 1"),
                ("g_vDetailTexCoordOffset1", "细节纹理坐标偏移 1"),
                ("g_vDetailTexCoordScale1", "细节纹理坐标缩放 1"),
                ("g_vTexCoordCenter1", "纹理坐标中心 1"),
                ("g_vTexCoordOffset1", "纹理坐标偏移 1"),
                ("g_vTexCoordScale1", "纹理坐标缩放 1"),
                ("g_flOcclusionCullingBoundsScale", "遮挡剔除包围盒缩放"),
                ("TextureColor1", "颜色 / 反照率"),
                ("TextureNormal1", "法线贴图"),
                ("TextureNormalDetail1", "细节法线"),
                ("TextureRoughness1", "粗糙度"),
                ("TextureMetalness1", "金属度"),
                ("TextureAmbientOcclusion1", "环境光遮蔽"),
                ("TextureHeight1", "高度贴图"),
                ("TextureTintMask1", "染色遮罩"),
                ("TextureTranslucency1", "半透明度"),
                ("g_bPuddlesOnVerticalSurfaces", "垂直面水洼"),
                ("g_bWetnessUseHeightmapAdjustments", "湿润使用高度图调整"),
                ("g_fPuddleBlendSoftness", "水洼混合柔和度"),
                ("g_fPuddleRoughness", "水洼粗糙度"),
                ("g_fPuddleSedimentHeight", "水洼沉积高度"),
                ("g_fPuddleSedimentOpacity", "水洼沉积不透明度"),
                ("g_fPuddleStrength", "水洼强度"),
                ("g_fRainStrength", "雨强度"),
                ("g_fRippleStrength", "涟漪强度"),
                ("g_fWetEdgeSpread", "湿润边缘扩散"),
                ("g_fWetEdgeStrength", "湿润边缘强度"),
                ("g_fWetnessStrength", "湿润强度"),
                ("g_vPuddleSedimentColor", "水洼沉积颜色")),
            Map(
                ("TextureNormalDetail1", "F_DETAIL_NORMAL"),
                ("g_fDetailTextureNormalContrast1", "F_DETAIL_NORMAL"),
                ("g_flDetailTexCoordRotation1", "F_DETAIL_NORMAL"),
                ("g_nDetailUVSet1", "F_DETAIL_NORMAL"),
                ("g_vDetailTexCoordCenter1", "F_DETAIL_NORMAL"),
                ("g_vDetailTexCoordOffset1", "F_DETAIL_NORMAL"),
                ("g_vDetailTexCoordScale1", "F_DETAIL_NORMAL"),
                ("g_flWetnessDarkeningStrength1", "F_WETNESS"),
                ("g_bPuddlesOnVerticalSurfaces", "F_WETNESS"),
                ("g_bWetnessUseHeightmapAdjustments", "F_WETNESS"),
                ("g_fPuddleBlendSoftness", "F_WETNESS"),
                ("g_fPuddleRoughness", "F_WETNESS"),
                ("g_fPuddleSedimentHeight", "F_WETNESS"),
                ("g_fPuddleSedimentOpacity", "F_WETNESS"),
                ("g_fPuddleStrength", "F_WETNESS"),
                ("g_fRainStrength", "F_WETNESS"),
                ("g_fRippleStrength", "F_WETNESS"),
                ("g_fWetEdgeSpread", "F_WETNESS"),
                ("g_fWetEdgeStrength", "F_WETNESS"),
                ("g_fWetnessStrength", "F_WETNESS"),
                ("g_vPuddleSedimentColor", "F_WETNESS"),
                ("g_flOcclusionCullingBoundsScale", "F_OCCLUSION_CULLING_BOUNDS_SCALE")),
            ["mapbuilder.bakedlighting"]),

        ["csgo_lightmappedgeneric"] = new Overlay(
            "CS:GO 光照贴图通用",
            "仅限笔刷的 PBR 材质，使用烘焙光照贴图。常用于玻璃、泳池砖等地图几何。第一层（Layer1）承载全部贴图通道。",
            Labels(
                ("g_flVertexColorOpacityScale", "顶点色不透明度缩放"),
                ("g_flMetalness", "金属度常量"),
                ("g_vLayer1Tint", "第一层染色"),
                ("TextureLayer1Color", "第一层颜色"),
                ("TextureLayer1Normal", "第一层法线"),
                ("TextureLayer1Roughness", "第一层粗糙度"),
                ("TextureLayer1Metalness", "第一层金属度"),
                ("TextureLayer1AmbientOcclusion", "第一层 AO"),
                ("TextureLayer1Translucency", "第一层半透明度")),
            Map(
                ("g_flMetalness", "F_METALNESS_TEXTURE")),
            ["mapbuilder.lightmap"]),

        ["csgo_moondome"] = new Overlay(
            "CS:GO 模拟天穹",
            "将颜色染色与天空 Cube Map 以及可选视差偏移进行混合的天穹着色器，用于伪造 / 室内天空。",
            Labels(
                ("TextureColor", "颜色染色常量"),
                ("TextureCubeMap", "Cube 贴图"),
                ("g_flCubeParallax", "Cube 视差")),
            Map(),
            ["mapbuilder.sky"]),

        ["csgo_static_overlay"] = new Overlay(
            "CS:GO 静态覆盖",
            "用于贴花的混合材质，支持喷溅、模板等需要非标准混合模式（加法、半透明等）的覆盖层。",
            Labels(
                ("g_fTextureColorBrightness", "颜色亮度"),
                ("g_fTextureColorContrast", "颜色对比度"),
                ("g_fTextureColorSaturation", "颜色饱和度"),
                ("g_fTextureRoughnessBrightness", "粗糙度亮度"),
                ("g_fTextureRoughnessContrast", "粗糙度对比度"),
                ("g_fTextureNormalContrast", "法线对比度"),
                ("g_vTextureColorCorrectionTint", "颜色校正染色"),
                ("g_bMaskRoughnessAdjustmentsByTintMask", "粗糙度调整受染色遮罩裁剪")),
            Map(
                ("g_fTextureColorBrightness", "F_LIT"),
                ("g_fTextureColorContrast", "F_LIT"),
                ("g_fTextureColorSaturation", "F_LIT"),
                ("g_vTextureColorCorrectionTint", "F_LIT"),
                ("TextureTintMask", "F_TINT_MASK"),
                ("g_flAnimationFrame", "F_TEXTURE_ANIMATION"),
                ("g_flAnimationTimeOffset", "F_TEXTURE_ANIMATION"),
                ("g_flAnimationTimePerFrame", "F_TEXTURE_ANIMATION"),
                ("g_nNumAnimationCells", "F_TEXTURE_ANIMATION"),
                ("g_vAnimationGrid", "F_TEXTURE_ANIMATION")),
            ["mapbuilder.decal"]),

        ["csgo_vertexlitgeneric"] = new Overlay(
            "CS:GO 顶点光照通用",
            "廉价顶点光照材质，用于自发光贴图类（照片、标识、UI 贴花）。支持漫反射 / 法线 / 粗糙度 / 金属度 / AO / 自发光 / 贴花。",
            Labels(
                ("g_flAmbientOcclusionScale", "AO 缩放")),
            Map(
                ("TextureTintMask", "F_TINT_MASK"),
                ("g_bUseSecondaryUvForTintMask", "F_TINT_MASK"),
                ("TextureDecal", "F_DECAL_TEXTURE"),
                ("TextureDecalTranslucency", "F_DECAL_TEXTURE"),
                ("g_bUseSecondaryUvForDecal", "F_DECAL_TEXTURE"),
                ("TextureSelfIllumMask", "F_SELF_ILLUM"),
                ("g_bUseSecondaryUvForSelfIllum", "F_SELF_ILLUM"),
                ("g_flSelfIllumAlbedoFactor", "F_SELF_ILLUM"),
                ("g_flSelfIllumBrightness", "F_SELF_ILLUM"),
                ("g_flSelfIllumScale", "F_SELF_ILLUM"),
                ("g_vSelfIllumScrollSpeed", "F_SELF_ILLUM"),
                ("g_vSelfIllumTint", "F_SELF_ILLUM")),
            []),

        ["csgo_water_fancy"] = new Overlay(
            "CS:GO 高级水面",
            "带泡沫、漂浮物与双层涟漪法线贴图的专用折射水面着色器。所有已观测到的控制项均暴露为标量浮点。",
            Labels(
                ("g_bUseTriplanarCaustics", "三平面焦散"),
                ("g_flCausticDepthFallOffDistance", "焦散深度衰减距离"),
                ("g_flCausticDistortion", "焦散扭曲"),
                ("g_flCausticShadowCutOff", "焦散阴影截止"),
                ("g_flCausticSharpness", "焦散锐度"),
                ("g_flCausticsStrength", "焦散强度"),
                ("g_flCausticUVScaleMultiple", "焦散 UV 缩放倍数"),
                ("g_vCausticsTint", "焦散染色"),
                ("g_flDebrisEdgeSharpness", "漂浮物边缘锐度"),
                ("g_flDebrisNormalStrength", "漂浮物法线强度"),
                ("g_flDebrisOilyness", "漂浮物油感"),
                ("g_flDebrisReflectance", "漂浮物反射率"),
                ("g_flDebrisScale", "漂浮物缩放"),
                ("g_flDebrisWobble", "漂浮物摆动"),
                ("g_vDebrisTint", "漂浮物染色"),
                ("TextureDebris", "漂浮物颜色"),
                ("TextureDebrisHeight", "漂浮物高度"),
                ("TextureDebrisNormal", "漂浮物法线"),
                ("g_flRainStrength", "雨强度"),
                ("g_flFoamScale", "泡沫缩放"),
                ("g_flFoamWobble", "泡沫摆动"),
                ("g_vFoamColor", "泡沫颜色"),
                ("TextureFoam", "泡沫遮罩"),
                ("TextureFoamNormal", "泡沫法线"),
                ("g_flWaterEffectCausticStrength", "水效焦散强度"),
                ("g_flWaterEffectDisturbanceStrength", "水效扰动强度"),
                ("g_flWaterEffectFoamStrength", "水效泡沫强度"),
                ("g_flWaterEffectSiltStrength", "水效泥沙强度"),
                ("g_flWaterEffectsRippleStrength", "水效涟漪强度"),
                ("g_flSkyBoxFadeRange", "天空盒渐隐范围"),
                ("g_flSkyBoxScale", "天空盒缩放"),
                ("g_vMapUVMax", "地图 UV 上限"),
                ("g_vMapUVMin", "地图 UV 下限"),
                ("g_vSimpleSkyReflectionColor", "简单天空反射颜色"),
                ("g_flReflectance", "反射率"),
                ("g_flReflectionDistanceEffect", "反射距离效果"),
                ("g_flRefractChromaticSeparation", "折射色散分离"),
                ("g_flRefractionLimit", "折射上限"),
                ("g_flRefractSampleOffset", "折射采样偏移"),
                ("g_flUnderwaterDarkening", "水下变暗"),
                ("g_flWaterDecayStrength", "水面衰减强度"),
                ("g_flWaterFogShadowStrength", "水雾阴影强度"),
                ("g_flWaterFogStrength", "水雾强度"),
                ("g_flWaterMaxDepth", "水面最大深度"),
                ("g_vWaterDecayColor", "水面衰减颜色"),
                ("g_vWaterFogColor", "水雾颜色"),
                ("g_flSpecularBloomBoostStrength", "高光泛光增强强度"),
                ("g_flSpecularBloomBoostThreshold", "高光泛光增强阈值"),
                ("g_flSpecularNormalMultiple", "高光法线倍数"),
                ("g_flSpecularPower", "高光强度"),
                ("g_flDebrisMax", "漂浮物最大"),
                ("g_flDebrisMin", "漂浮物最小"),
                ("g_flFoamMax", "泡沫最大"),
                ("g_flFoamMin", "泡沫最小"),
                ("g_flWaterRoughnessMax", "水面粗糙度最大"),
                ("g_flWaterRoughnessMin", "水面粗糙度最小"),
                ("g_flEdgeHardness", "边缘硬度"),
                ("g_flEdgeShapeEffect", "边缘形状效果"),
                ("g_flFresnelExponent", "菲涅尔指数"),
                ("g_flHighFreqWeight", "高频权重"),
                ("g_flLowFreqWeight", "低频权重"),
                ("g_flMedFreqWeight", "中频权重"),
                ("g_flWaterInitialDirection", "水面初始方向"),
                ("g_flWaterPlaneOffset", "水面平面偏移"),
                ("g_flWavesHeightOffset", "波浪高度偏移"),
                ("g_flWavesNormalJitter", "波浪法线抖动"),
                ("g_flWavesNormalStrength", "波浪法线强度"),
                ("g_flWavesPhaseOffset", "波浪相位偏移"),
                ("g_flWavesSharpness", "波浪锐度"),
                ("g_flWavesSpeed", "波浪速度"),
                ("g_nWaveIterations", "波浪迭代次数"),
                ("g_vWaveScale", "波浪缩放"),
                ("TextureWavesHeight", "波浪高度"),
                ("TextureWavesNormal", "波浪法线")),
            Map(
                ("g_bUseTriplanarCaustics", "F_CAUSTICS"),
                ("g_flCausticDepthFallOffDistance", "F_CAUSTICS"),
                ("g_flCausticDistortion", "F_CAUSTICS"),
                ("g_flCausticShadowCutOff", "F_CAUSTICS"),
                ("g_flCausticSharpness", "F_CAUSTICS"),
                ("g_flCausticsStrength", "F_CAUSTICS"),
                ("g_flCausticUVScaleMultiple", "F_CAUSTICS"),
                ("g_vCausticsTint", "F_CAUSTICS"),
                ("g_flWaterEffectCausticStrength", "F_INTERACTION_EFFECTS"),
                ("g_flWaterEffectDisturbanceStrength", "F_INTERACTION_EFFECTS"),
                ("g_flWaterEffectFoamStrength", "F_INTERACTION_EFFECTS"),
                ("g_flWaterEffectSiltStrength", "F_INTERACTION_EFFECTS"),
                ("g_flWaterEffectsRippleStrength", "F_INTERACTION_EFFECTS"),
                ("g_flReflectionDistanceEffect", "F_REFRACTION"),
                ("g_flRefractionLimit", "F_REFRACTION"),
                ("g_flRefractChromaticSeparation", "F_REFRACTION"),
                ("g_flRefractSampleOffset", "F_REFRACTION")),
            ["mapbuilder.water"]),

        ["generic"] = new Overlay(
            "通用",
            "回退无光照着色器。多用于工具材质（天空盒遮挡体、裁切笔刷、不绘制表面）与编辑器占位。",
            Labels(
                ("g_vGlossinessRange", "光泽度范围"),
                ("g_vMetalnessRange", "金属度范围"),
                ("g_vReflectanceRange", "反射率范围"),
                ("TextureGlossiness", "光泽度"),
                ("TextureReflectance", "反射率"),
                ("g_flBumpStrength", "凹凸强度")),
            Map(
                ("TextureTintMask", "F_TINT_MASK"),
                ("TextureSelfIllumMask", "F_SELF_ILLUM"),
                ("g_flSelfIllumScale", "F_SELF_ILLUM"),
                ("g_vSelfIllumTint", "F_SELF_ILLUM")),
            ["mapbuilder.nodraw", "mapbuilder.playerclip", "mapbuilder.sky", "mapbuilder.visblocker", "tools.toolsmaterial"]),

        ["sky"] = new Overlay(
            "天空盒",
            "轻量 HDR 天空盒着色器。仅需一张天空贴图外加曝光偏置 / 旋转 / 地平线偏移。",
            Labels(
                ("SkyTexture", "天空贴图"),
                ("g_flBrightnessExposureBias", "亮度曝光偏置"),
                ("g_flHorizonOffset", "地平线偏移"),
                ("g_flRenderOnlyExposureBias", "仅渲染曝光偏置"),
                ("g_flRotation", "旋转")),
            Map(),
            ["mapbuilder.sky"]),
    };

    /// <summary>内嵌资源全部缺失时的兜底基名（与模板目录一致）。</summary>
    private static readonly string[] FallbackBasenames =
    [
        "csgo_character", "csgo_complex", "csgo_effects", "csgo_environment",
        "csgo_lightmappedgeneric", "csgo_moondome", "csgo_static_overlay",
        "csgo_vertexlitgeneric", "csgo_water_fancy", "generic", "sky",
    ];
}

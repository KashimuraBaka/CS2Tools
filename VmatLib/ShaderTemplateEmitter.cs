namespace VmatLib;

/// <summary>
/// Emits "factory default" VMAT text for every shader in <see cref="ShaderCatalog"/>,
/// both to disk (under <c>Templates/</c>) and as embedded resources in the assembly.
/// The embedded copies are used by the library to bootstrap an editor session even when
/// the developer hasn't run the build-time emitter script.
/// </summary>
public static class ShaderTemplateEmitter
{
    /// <summary>
    /// Render a single shader to VMAT text using the template's parameter defaults.
    /// The result is a complete Source 2 VMAT — <c>Layer0</c> with every feature flag
    /// (at its template factory default unless listed in <paramref name="enabledFeatureFlags"/>),
    /// every parameter, the template's <c>Attributes</c> block, and any
    /// <c>SystemAttributes</c> defaults.
    /// </summary>
    public static string EmitDefault(ShaderTemplate shader, IEnumerable<string>? enabledFeatureFlags = null)
    {
        var values = shader.BuildDefaultValueMap();
        // 「合并后的设置保持默认值」：默认不强制启用任何 flag，未列出的 flag 按模板出厂值写出。
        var enabledFlags = enabledFeatureFlags?.ToList() ?? [];
        return VmatGenerator.Render(
            shader,
            values,
            enabledFeatureFlags: enabledFlags,
            systemAttributeOverrides: shader.SystemAttributeDefaults.ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    /// <summary>
    /// Write every shader's factory default to a file under <paramref name="directory"/>.
    /// Returns the list of file paths that were written.
    /// </summary>
    public static IReadOnlyList<string> EmitAllToDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        var paths = new List<string>(ShaderCatalog.All.Count);
        foreach (var shader in ShaderCatalog.All)
        {
            // 文件名跟模板基名走（csgo_character.vmat），与内嵌资源一一对应。
            var name = string.IsNullOrEmpty(shader.TemplateResourceName) ? shader.ShaderName : shader.TemplateResourceName;
            var path = Path.Combine(directory, name + ".vmat");
            File.WriteAllText(path, EmitDefault(shader));
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>
    /// Load a specific template that was embedded into the assembly at build time.
    /// Returns <c>null</c> if no template was embedded for that shader.
    /// </summary>
    public static string? LoadEmbedded(string shaderName)
    {
        var assembly = typeof(ShaderTemplateEmitter).Assembly;
        var resourceName = assembly.GetName().Name + ".Templates." + shaderName + ".vmat";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null) return null;
        using var sr = new StreamReader(stream);
        return sr.ReadToEnd();
    }

    /// <summary>
    /// Enumerate every embedded template resource. Used by the round-trip test to make
    /// sure tier0 原生 KV 函数（经 KvAdapter） can reparse what we shipped.
    /// </summary>
    public static IEnumerable<(string ShaderName, string Text)> EnumerateEmbedded()
    {
        var assembly = typeof(ShaderTemplateEmitter).Assembly;
        var prefix = assembly.GetName().Name + ".Templates.";
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (!name.EndsWith(".vmat", StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var sr = new StreamReader(stream);
            yield return (name[prefix.Length..^".vmat".Length], sr.ReadToEnd());
        }
    }
}
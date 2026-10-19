using System.Text;
using VmatGenerator.Diagnostics;
using VmatLib;

namespace VmatGenerator.ViewModels;

/// <summary>
/// <see cref="MainViewModel"/> 的右列「KeyValues 模板预览」实现。
///
/// <para>单独成文件而非并入 <see cref="MainViewModel"/> 主体，是为了把这个
/// 「渲染 + 文本标注」的关注点与规则表的增删改逻辑分开；类本身是
/// <c>partial</c>，成员与主体完全同级。</para>
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>贴图参数行加用的中文前缀。</summary>
    private const string TexturePathPrefix = "路径：";

    /// <summary>
    /// 生成右列的 KeyValues 模板预览：当前着色器的出厂基线文档，
    /// 贴图参数行加 <see cref="TexturePathPrefix"/> 前缀。
    /// </summary>
    /// <remarks>
    /// 走 <see cref="ShaderTemplateEmitter.EmitDefault"/>——与应用实际写出<b>同一条</b>路径，
    /// 因此值本身与保存到磁盘的内容一致。异常按项目约定交给
    /// <see cref="ControlErrorRecorder"/> 记录并回落到占位文案，
    /// 避免单个着色器渲染失败打断整个对话框。
    /// </remarks>
    private string BuildTemplatePreview() => ControlErrorRecorder.Guard("预览着色器模板文档", this, () =>
                                                  {
                                                      if (SelectedShader is not { } shader)
                                                          return "（未选择生成着色器类型）";

                                                      var text = ShaderTemplateEmitter.EmitDefault(shader);
                                                      return string.IsNullOrWhiteSpace(text)
                                                          ? $"（{shader.ShaderName} 未生成任何内容）"
                                                          : AnnotateTexturePaths(shader, text);
                                                  }, "（生成模板文档失败，详见错误日志）");

    /// <summary>
    /// 给贴图参数所在行加 <see cref="TexturePathPrefix"/> 前缀，其余行原样保留。
    /// </summary>
    /// <remarks>
    /// <para><b>贴图键的判定取自模板数据，而不是对键名或值做字符串猜测：</b>
    /// 收集 <see cref="ShaderParamKind.Texture"/> 或
    /// <see cref="ShaderValueShape.Texture"/> / <see cref="ShaderValueShape.TextureOrVector"/>
    /// 的参数键，外加 <see cref="ShaderTemplate.CompiledTextureKeys"/>
    /// （它们在 Compiled Textures 块里同样是路径）。</para>
    /// <para>逐行只认行首形如 <c>"Key"</c> 的键名，因此嵌套块（如 Layer0 内）的参数
    /// 同样能命中；而值里恰好含 <c>/</c> 的非贴图参数不会被误标。</para>
    /// </remarks>
    private static string AnnotateTexturePaths(ShaderTemplate shader, string kvText)
    {
        var textureKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in shader.Parameters)
        {
            if (p.Kind == ShaderParamKind.Texture
                || p.Shape == ShaderValueShape.Texture
                || p.Shape == ShaderValueShape.TextureOrVector)
            {
                textureKeys.Add(p.Key);
            }
        }
        foreach (var key in shader.CompiledTextureKeys) textureKeys.Add(key);
        if (textureKeys.Count == 0) return kvText;

        var sb = new StringBuilder(kvText.Length + 64);
        foreach (var raw in kvText.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var t = line.TrimStart();
            if (t.Length > 2 && t[0] == '"')
            {
                var end = t.IndexOf('"');
                if (end > 1 && textureKeys.Contains(t[1..end]))
                    sb.Append(TexturePathPrefix);
            }
            sb.Append(line).Append('\n');
        }
        return sb.ToString();
    }
}
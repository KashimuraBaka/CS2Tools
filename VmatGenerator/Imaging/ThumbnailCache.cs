using System.IO;
using System.Windows.Media.Imaging;

namespace VmatGenerator.Imaging;

/// <summary>
/// 扫描列表缩略图的<b>有界</b>缓存：按字节预算的 LRU，解码后的缩略图尺寸固定。
///
/// <para><b>为什么必须缩到 64px 再缓存。</b>一张 4096×4096 的贴图按全尺寸解码是
/// 4096 × 4096 × 4B = <b>67 MB</b>；一个稍具规模的资产目录动辄上千张，全尺寸缓存必然 OOM。
/// <see cref="BitmapImage.DecodePixelWidth"/> 让解码器在<b>解码过程中</b>就降采样，
/// 64px 的成品只有 16 KB——相差 4096 倍，而且省掉的不只是缓存，
/// 连解码过程中的临时缓冲也一并按缩放后的尺寸分配。</para>
///
/// <para><b>为什么还要预算上限。</b>16 KB × 几千行仍是几十 MB，而列表行数不受用户控制。
/// 因此这里不按张数计费，而是按<b>实际解码后像素数 × 4</b>计费，超预算就从最久未用的开始淘汰。
/// 按张数计费会低估长条形贴图（1024×64 同样是 4 万像素），按字节计费才不会。</para>
///
/// <para><b>负缓存。</b>解不出来的文件（TGA、DDS 等 WPF 不支持的格式，或损坏文件）
/// 记一条 <c>Source == null</c> 的条目。它占 0 字节所以永不淘汰，但能避免每次滚动
/// 都对同一个坏文件重跑一遍解码——那才是真正会卡住界面的地方。</para>
///
/// <para><b>并发。</b>解码跑在线程池上，信号量把并发压在 2：解码瞬时缓冲虽已被缩样控制，
/// 但多个大文件同时解码仍会顶高峰值。</para>
///
/// <para>线程安全：所有状态都在同一把锁内修改，返回的 <see cref="BitmapSource"/>
/// 已 <c>Freeze</c>，跨线程共享不会触发复制。</para>
/// </summary>
public sealed class ThumbnailCache : IDisposable
{
    /// <summary>进程内共享实例；缩略图只服务于扫描列表，没必要每窗口一份。</summary>
    public static ThumbnailCache Shared { get; } = new();

    /// <summary>缩略图边长（像素）。</summary>
    public const int Edge = 64;

    private const long DefaultBudgetBytes = 32L * 1024 * 1024;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _recency = new();
    private readonly SemaphoreSlim _decodeGate = new(2, 2);

    private long _budgetBytes = DefaultBudgetBytes;
    private long _usedBytes;
    private int _decodeFailures;

    /// <summary>一条缓存记录。</summary>
    private sealed class Entry
    {
        /// <summary>解码结果；<c>null</c> 表示这个文件解不出来（负缓存）。</summary>
        public BitmapSource? Source { get; init; }

        /// <summary>占用的字节数，按解码后像素计；负缓存为 0。</summary>
        public long Bytes { get; init; }

        /// <summary>LRU 链表节点；负缓存也要挂表，否则每次滚动都会重新解码。</summary>
        public LinkedListNode<string> Node { get; set; } = new("!");
    }

    /// <summary>当前已缓存的字节数。</summary>
    public long UsedBytes { get { lock (_gate) return _usedBytes; } }

    /// <summary>缓存预算上限。</summary>
    public long BudgetBytes
    {
        get { lock (_gate) return _budgetBytes; }
        set
        {
            lock (_gate)
            {
                _budgetBytes = Math.Max(1L * 1024 * 1024, value);
                Evict();
            }
        }
    }

    /// <summary>已缓存的条目数（含负缓存）。</summary>
    public int Count { get { lock (_gate) return _entries.Count; } }

    /// <summary>累计解码失败的文件数；排查资产库问题时有用。</summary>
    public int DecodeFailures { get { lock (_gate) return _decodeFailures; } }

    /// <summary>取缩略图：命中缓存直接返回，否则在后台解码。</summary>
    /// <param name="path">图像文件完整路径。</param>
    /// <returns>缩略图；解码失败时返回 <c>null</c>（调用方显示占位符）。</returns>
    public async Task<BitmapSource?> GetAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        lock (_gate)
        {
            if (_entries.TryGetValue(path, out var cached))
            {
                Touch(cached);
                return cached.Source;
            }
        }

        await _decodeGate.WaitAsync().ConfigureAwait(false);
        BitmapSource? decoded;
        try
        {
            decoded = await Task.Run(() => Decode(path)).ConfigureAwait(false);
        }
        finally
        {
            _decodeGate.Release();
        }

        lock (_gate)
        {
            // 滚动时同一行可能被并发请求多次；后来者直接丢弃，保留先到的那份。
            if (_entries.TryGetValue(path, out var existing))
            {
                Touch(existing);
                return existing.Source;
            }

            if (decoded is null) _decodeFailures++;

            var entry = new Entry
            {
                Source = decoded,
                Bytes = decoded is null ? 0 : (long)decoded.PixelWidth * decoded.PixelHeight * 4,
                Node = _recency.AddFirst(path),
            };
            _entries[path] = entry;
            _usedBytes += entry.Bytes;
            Evict();
            return decoded;
        }
    }

    /// <summary>清空缓存。切换资产目录或改递归选项后必须调用，否则旧条目会一直占着预算。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _recency.Clear();
            _usedBytes = 0;
            _decodeFailures = 0;
        }
    }

    /// <summary>缓存占用的人读摘要，例如 <c>12.3 / 32 MB（384 张）</c> 或 <c>640 / 1024 KB（120 张）</c>。</summary>
    /// <remarks>
    /// 单位按预算大小自适应：预算有 1 MB 的下限，若一律按 MB 显示，
    /// 小预算会被 <c>:0</c> 四舍五入成 "0 MB"，看不出任何区别。
    /// </remarks>
    public string DescribeUsage()
    {
        lock (_gate)
        {
            var usedKb = _usedBytes / 1024d;
            var budgetKb = _budgetBytes / 1024d;

            return budgetKb < 1024
                ? $"{usedKb:0} / {budgetKb:0} KB（{_entries.Count} 张）"
                : $"{usedKb / 1024:0.#} / {budgetKb / 1024:0.#} MB（{_entries.Count} 张）";
        }
    }

    /// <summary>把条目移到 LRU 队首，并原地更新它的链表节点。</summary>
    private void Touch(Entry entry)
    {
        _recency.Remove(entry.Node);
        entry.Node = _recency.AddFirst(entry.Node.Value);
    }

    /// <summary>超预算时从队尾（最久未用）淘汰，直到落回预算内。</summary>
    private void Evict()
    {
        while (_usedBytes > _budgetBytes && _recency.Last is { } oldest)
        {
            _recency.RemoveLast();
            if (_entries.Remove(oldest.Value, out var entry)) _usedBytes -= entry.Bytes;
        }
    }

    /// <summary>
    /// 解码一张缩略图。<b>必须在后台线程调用</b>。
    /// </summary>
    /// <remarks>
    /// <see cref="BitmapCacheOption.OnLoad"/> 让解码器读完就关句柄：不锁住源文件，
    /// 也不用把文件留在 WPF 的全局位图缓存里。共享模式放宽到 <c>ReadWrite</c>，
    /// 因为资产常被其他工具（VTF 编辑器、Explorer 预览）打开着。
    /// </remarks>
    private static BitmapSource? Decode(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bitmap.DecodePixelWidth = Edge;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }

            // 冻结后才能安全跨线程使用，也避免 UI 线程再复制一份。
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            // 损坏文件、DDS/TGA 等 WPF 不支持的格式、权限不足……一律记为负缓存。
            return null;
        }
    }

    /// <summary>
    /// 释放解码闸门的内核等待句柄。进程内共享实例（<see cref="Shared"/>）
    /// 一般活到进程结束，无需显式调用；重复调用安全。
    /// </summary>
    public void Dispose() => _decodeGate.Dispose();
}
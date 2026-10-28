using System.Diagnostics;
using System.Text;
using ANalyze.Domain;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SkiaSharp;

namespace ANalyze.Preview;

public sealed record TextChunk(string Text, string Encoding, long Offset, long NextOffset, long Size, bool Truncated);

public sealed record HexChunk(long Offset, long Size, string Base64);

public sealed record ArchiveListing(IReadOnlyList<ArchiveItem> Items, int Total, bool Truncated, string? Error);

public sealed record ArchiveItem(string Path, long Size, long CompressedSize, bool IsDir, DateTime? Modified);

public sealed record MetadataGroup(string Name, IReadOnlyList<KeyValuePair<string, string>> Tags);

/// <summary>Thumbnails, paged text/hex reads, archive listings and embedded metadata.</summary>
public sealed class PreviewService(string thumbsDir, ILogger<PreviewService> log)
{
    public const int MaxTextChunk = 256 * 1024;
    public const int MaxHexChunk = 64 * 1024;
    private static readonly SemaphoreSlim ThumbGate = new(Math.Max(2, Environment.ProcessorCount / 2));

    public static readonly string? FfmpegPath = FindTool("ffmpeg");
    public static readonly string? FfprobePath = FindTool("ffprobe");

    /// <summary>Returns a cached WebP thumbnail, generating it if needed; null when the file cannot be thumbnailed.</summary>
    public async Task<string?> ThumbnailAsync(long entryId, long mtime, string path, FileKind kind, int size, CancellationToken ct)
    {
        size = Math.Clamp(size, 64, 1024);
        Directory.CreateDirectory(thumbsDir);
        var cache = Path.Combine(thumbsDir, $"{entryId % 256:x2}", $"{entryId}-{mtime}-{size}.webp");
        var failMarker = cache + ".fail";
        if (File.Exists(cache))
        {
            return cache;
        }

        if (File.Exists(failMarker))
        {
            return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        await ThumbGate.WaitAsync(ct);
        try
        {
            if (File.Exists(cache))
            {
                return cache;
            }

            byte[]? webp = null;
            if (kind == FileKind.Image)
            {
                webp = SkiaThumb(path, size) ?? await FfmpegThumbAsync(path, size, seek: false, ct);
            }
            else if (kind == FileKind.Video)
            {
                webp = await FfmpegThumbAsync(path, size, seek: true, ct);
            }
            else if (kind == FileKind.Audio)
            {
                webp = EmbeddedCover(path, size);
            }

            if (webp is null)
            {
                await File.WriteAllBytesAsync(failMarker, [], ct);
                return null;
            }

            var tmp = cache + ".tmp";
            await File.WriteAllBytesAsync(tmp, webp, ct);
            File.Move(tmp, cache, overwrite: true);
            return cache;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log.LogDebug(e, "Thumbnail failed for {Path}", path);
            return null;
        }
        finally
        {
            ThumbGate.Release();
        }
    }

    private static byte[]? SkiaThumb(string path, int size)
    {
        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
        {
            return null;
        }

        var info = codec.Info;
        var scale = Math.Min(1f, size / (float)Math.Max(info.Width, info.Height));
        var dims = codec.GetScaledDimensions(scale);
        using var decoded = SKBitmap.Decode(codec, new SKImageInfo(dims.Width, dims.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (decoded is null)
        {
            return null;
        }

        var w = Math.Max(1, (int)(info.Width * scale));
        var h = Math.Max(1, (int)(info.Height * scale));
        using var resized = decoded.Width == w && decoded.Height == h ? null : decoded.Resize(new SKImageInfo(w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var oriented = Orient(resized ?? decoded, codec.EncodedOrigin);
        using var img = SKImage.FromBitmap(oriented ?? resized ?? decoded);
        using var data = img.Encode(SKEncodedImageFormat.Webp, 80);
        return data?.ToArray();
    }

    /// <summary>Applies the EXIF orientation; returns null when none is needed.</summary>
    private static SKBitmap? Orient(SKBitmap src, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return null;
        }

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var dst = new SKBitmap(swap ? src.Height : src.Width, swap ? src.Width : src.Height);
        using var canvas = new SKCanvas(dst);
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Scale(-1, 1, src.Width / 2f, 0);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.RotateDegrees(180, src.Width / 2f, src.Height / 2f);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Scale(1, -1, 0, src.Height / 2f);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.Translate(dst.Width, 0);
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1, 0, src.Height / 2f);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(dst.Width, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(0, dst.Height);
                canvas.RotateDegrees(-90);
                canvas.Scale(1, -1, 0, src.Height / 2f);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, dst.Height);
                canvas.RotateDegrees(-90);
                break;
        }

        using var srcImage = SKImage.FromBitmap(src);
        canvas.DrawImage(srcImage, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        return dst;
    }

    private static byte[]? EmbeddedCover(string path, int size)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var pic = file.Tag.Pictures.FirstOrDefault();
            if (pic is null)
            {
                return null;
            }

            using var bmp = SKBitmap.Decode(pic.Data.Data);
            if (bmp is null)
            {
                return null;
            }

            var scale = Math.Min(1f, size / (float)Math.Max(bmp.Width, bmp.Height));
            using var resized = bmp.Resize(new SKImageInfo(Math.Max(1, (int)(bmp.Width * scale)), Math.Max(1, (int)(bmp.Height * scale))), new SKSamplingOptions(SKCubicResampler.Mitchell));
            using var img = SKImage.FromBitmap(resized ?? bmp);
            return img.Encode(SKEncodedImageFormat.Webp, 80)?.ToArray();
        }
        catch (Exception e) when (e is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or IOException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> FfmpegThumbAsync(string path, int size, bool seek, CancellationToken ct)
    {
        if (FfmpegPath is null)
        {
            return null;
        }

        var psi = new ProcessStartInfo(FfmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error" })
        {
            psi.ArgumentList.Add(a);
        }

        if (seek)
        {
            // Skip intros/black frames; ffmpeg clamps to the last frame for short clips via thumbnail filter.
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add("10");
        }

        foreach (var a in new[] { "-i", path, "-frames:v", "1", "-vf", $"thumbnail,scale='min({size},iw)':-2", "-f", "image2pipe", "-c:v", "libwebp", "-quality", "80", "pipe:1" })
        {
            psi.ArgumentList.Add(a);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var p = Process.Start(psi);
        if (p is null)
        {
            return null;
        }

        try
        {
            using var ms = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(ms, timeout.Token);
            var err = p.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(copy, err);
            await p.WaitForExitAsync(timeout.Token);
            if (ms.Length > 0)
            {
                return ms.ToArray();
            }

            // Clip shorter than the seek point: retry from the start.
            return seek ? await FfmpegThumbAsync(path, size, seek: false, ct) : null;
        }
        catch (OperationCanceledException)
        {
            try
            {
                p.Kill(true);
            }
            catch (InvalidOperationException)
            {
            }

            return null;
        }
    }

    public static async Task<TextChunk> ReadTextAsync(string path, long offset, int length, CancellationToken ct)
    {
        length = Math.Clamp(length, 1, MaxTextChunk);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var size = fs.Length;
        offset = Math.Clamp(offset, 0, size);
        var bom = new byte[4];
        fs.Position = 0;
        var bomLen = await fs.ReadAsync(bom, ct);
        var (encoding, preamble) = DetectEncoding(bom.AsSpan(0, bomLen));
        if (offset < preamble)
        {
            offset = preamble;
        }

        fs.Position = offset;
        var buf = new byte[length];
        var n = await fs.ReadAtLeastAsync(buf, length, throwOnEndOfStream: false, ct);

        // Do not split a UTF-8 sequence at the end of the chunk.
        if (encoding is UTF8Encoding && offset + n < size)
        {
            var cut = n;
            while (cut > 0 && cut > n - 4 && (buf[cut - 1] & 0xC0) == 0x80)
            {
                cut--;
            }

            if (cut > 0 && buf[cut - 1] >= 0xC0)
            {
                cut--;
            }

            n = cut == 0 ? n : cut;
        }

        string text;
        var name = encoding.WebName;
        if (encoding is UTF8Encoding && !IsValidUtf8(buf.AsSpan(0, n)))
        {
            // Not UTF-8: show it as Latin-1 so every byte stays visible.
            text = Encoding.Latin1.GetString(buf, 0, n);
            name = "iso-8859-1";
        }
        else
        {
            text = encoding.GetString(buf, 0, n);
        }

        return new TextChunk(text, name, offset, offset + n, size, offset + n < size);
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> bytes) =>
        System.Text.Unicode.Utf8.IsValid(bytes);

    private static (Encoding Encoding, int Preamble) DetectEncoding(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
        {
            return (new UTF8Encoding(false), 3);
        }

        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
        {
            return (Encoding.Unicode, 2);
        }

        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
        {
            return (Encoding.BigEndianUnicode, 2);
        }

        return (new UTF8Encoding(false), 0);
    }

    public static async Task<HexChunk> ReadBytesAsync(string path, long offset, int length, CancellationToken ct)
    {
        length = Math.Clamp(length, 1, MaxHexChunk);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var size = fs.Length;
        offset = Math.Clamp(offset, 0, size);
        fs.Position = offset;
        var buf = new byte[length];
        var n = await fs.ReadAtLeastAsync(buf, length, throwOnEndOfStream: false, ct);
        return new HexChunk(offset, size, Convert.ToBase64String(buf, 0, n));
    }

    public static ArchiveListing ListArchive(string path, int max = 5000)
    {
        try
        {
            using var archive = ArchiveFactory.OpenArchive(path);
            var items = new List<ArchiveItem>();
            var total = 0;
            foreach (var e in archive.Entries)
            {
                total++;
                if (items.Count < max)
                {
                    items.Add(new ArchiveItem(e.Key ?? "", e.Size, e.CompressedSize, e.IsDirectory, e.LastModifiedTime));
                }
            }

            return new ArchiveListing(items, total, total > items.Count, null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new ArchiveListing([], 0, false, e.Message);
        }
    }

    public static IReadOnlyList<MetadataGroup> ReadMetadata(string path, FileKind kind)
    {
        var groups = new List<MetadataGroup>();
        if (kind is FileKind.Audio or FileKind.Video)
        {
            try
            {
                using var f = TagLib.File.Create(path);
                var tags = new List<KeyValuePair<string, string>>();
                void Add(string k, object? v)
                {
                    var s = v switch
                    {
                        null => null,
                        string str => str,
                        string[] arr => string.Join(", ", arr),
                        uint u when u == 0 => null,
                        TimeSpan ts => ts.ToString(@"hh\:mm\:ss"),
                        _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
                    };
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        tags.Add(new(k, s));
                    }
                }

                Add("Title", f.Tag.Title);
                Add("Artist", f.Tag.Performers);
                Add("Album", f.Tag.Album);
                Add("Album artist", f.Tag.AlbumArtists);
                Add("Year", f.Tag.Year);
                Add("Track", f.Tag.Track);
                Add("Genre", f.Tag.Genres);
                Add("Comment", f.Tag.Comment);
                Add("Duration", f.Properties.Duration);
                Add("Bitrate", f.Properties.AudioBitrate > 0 ? $"{f.Properties.AudioBitrate} kbps" : null);
                Add("Sample rate", f.Properties.AudioSampleRate > 0 ? $"{f.Properties.AudioSampleRate} Hz" : null);
                Add("Channels", f.Properties.AudioChannels > 0 ? f.Properties.AudioChannels : null);
                Add("Resolution", f.Properties.VideoWidth > 0 ? $"{f.Properties.VideoWidth}×{f.Properties.VideoHeight}" : null);
                Add("Codecs", string.Join(", ", f.Properties.Codecs.Where(c => c is not null).Select(c => c.Description)));
                if (tags.Count > 0)
                {
                    groups.Add(new MetadataGroup("Media", tags));
                }
            }
            catch (Exception e) when (e is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or IOException or NotImplementedException)
            {
                // fall through to MetadataExtractor
            }
        }

        if (kind is FileKind.Image or FileKind.Video or FileKind.Audio || groups.Count == 0)
        {
            try
            {
                foreach (var dir in MetadataExtractor.ImageMetadataReader.ReadMetadata(path))
                {
                    if (dir.Name is "File" or "File Type")
                    {
                        continue;
                    }

                    var tags = dir.Tags
                        .Where(t => !string.IsNullOrWhiteSpace(t.Description) && t.Description!.Length < 400)
                        .Select(t => new KeyValuePair<string, string>(t.Name, t.Description!))
                        .ToList();
                    if (tags.Count > 0)
                    {
                        groups.Add(new MetadataGroup(dir.Name, tags));
                    }
                }
            }
            catch (Exception e) when (e is MetadataExtractor.ImageProcessingException or IOException)
            {
                // not a supported container
            }
        }

        return groups;
    }

    private static string? FindTool(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var file = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, file);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

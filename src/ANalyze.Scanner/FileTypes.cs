using ANalyze.Domain;

namespace ANalyze.Scanner;

/// <summary>Maps extensions and magic bytes to a <see cref="FileKind"/> and MIME type.</summary>
public static class FileTypes
{
    public readonly record struct TypeInfo(FileKind Kind, string Mime);

    private static readonly Dictionary<string, TypeInfo> ByExt = Build();

    private static Dictionary<string, TypeInfo> Build()
    {
        var d = new Dictionary<string, TypeInfo>(StringComparer.OrdinalIgnoreCase);

        void Add(FileKind kind, string mime, params string[] exts)
        {
            foreach (var e in exts)
            {
                d[e] = new TypeInfo(kind, mime);
            }
        }

        // Images
        Add(FileKind.Image, "image/jpeg", "jpg", "jpeg", "jpe", "jfif");
        Add(FileKind.Image, "image/png", "png");
        Add(FileKind.Image, "image/gif", "gif");
        Add(FileKind.Image, "image/webp", "webp");
        Add(FileKind.Image, "image/bmp", "bmp", "dib");
        Add(FileKind.Image, "image/tiff", "tif", "tiff");
        Add(FileKind.Image, "image/heic", "heic", "heif");
        Add(FileKind.Image, "image/avif", "avif");
        Add(FileKind.Image, "image/svg+xml", "svg", "svgz");
        Add(FileKind.Image, "image/x-icon", "ico", "cur");
        Add(FileKind.Image, "image/jxl", "jxl");
        Add(FileKind.Image, "image/vnd.adobe.photoshop", "psd", "psb");
        Add(FileKind.Image, "image/x-xcf", "xcf");
        Add(FileKind.Image, "image/x-tga", "tga");
        Add(FileKind.Image, "image/x-exr", "exr", "hdr");
        Add(FileKind.Image, "image/x-dcraw", "cr2", "cr3", "crw", "nef", "nrw", "arw", "srf", "sr2", "dng", "orf", "rw2", "raf", "srw", "pef", "x3f", "erf", "3fr", "iiq", "mos", "mrw", "rwl");

        // Video
        Add(FileKind.Video, "video/mp4", "mp4", "m4v", "mp4v");
        Add(FileKind.Video, "video/x-matroska", "mkv", "mk3d");
        Add(FileKind.Video, "video/webm", "webm");
        Add(FileKind.Video, "video/x-msvideo", "avi", "divx");
        Add(FileKind.Video, "video/quicktime", "mov", "qt");
        Add(FileKind.Video, "video/x-ms-wmv", "wmv", "asf");
        Add(FileKind.Video, "video/x-flv", "flv", "f4v");
        Add(FileKind.Video, "video/mpeg", "mpg", "mpeg", "mpe", "m1v", "m2v", "vob");
        Add(FileKind.Video, "video/mp2t", "ts", "m2ts", "mts", "tp", "trp");
        Add(FileKind.Video, "video/3gpp", "3gp", "3g2");
        Add(FileKind.Video, "video/ogg", "ogv");
        Add(FileKind.Video, "video/x-ms-dvr", "dvr-ms", "wtv");
        Add(FileKind.Video, "video/x-rmvb", "rm", "rmvb");

        // Audio
        Add(FileKind.Audio, "audio/mpeg", "mp3", "mp2", "mpga");
        Add(FileKind.Audio, "audio/flac", "flac");
        Add(FileKind.Audio, "audio/wav", "wav", "wave");
        Add(FileKind.Audio, "audio/aac", "aac");
        Add(FileKind.Audio, "audio/mp4", "m4a", "m4b", "m4p", "alac");
        Add(FileKind.Audio, "audio/ogg", "ogg", "oga", "opus", "spx");
        Add(FileKind.Audio, "audio/x-ms-wma", "wma");
        Add(FileKind.Audio, "audio/aiff", "aif", "aiff", "aifc");
        Add(FileKind.Audio, "audio/x-ape", "ape");
        Add(FileKind.Audio, "audio/x-wavpack", "wv");
        Add(FileKind.Audio, "audio/x-dsf", "dsf", "dff");
        Add(FileKind.Audio, "audio/midi", "mid", "midi");
        Add(FileKind.Audio, "audio/amr", "amr");
        Add(FileKind.Audio, "audio/x-mod", "mod", "xm", "it", "s3m");

        // Documents
        Add(FileKind.Pdf, "application/pdf", "pdf");
        Add(FileKind.Document, "application/msword", "doc", "dot");
        Add(FileKind.Document, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "docx", "docm", "dotx");
        Add(FileKind.Document, "application/vnd.ms-excel", "xls", "xlt");
        Add(FileKind.Document, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx", "xlsm", "xltx", "xlsb");
        Add(FileKind.Document, "application/vnd.ms-powerpoint", "ppt", "pps", "pot");
        Add(FileKind.Document, "application/vnd.openxmlformats-officedocument.presentationml.presentation", "pptx", "ppsx", "pptm");
        Add(FileKind.Document, "application/vnd.oasis.opendocument.text", "odt", "ott");
        Add(FileKind.Document, "application/vnd.oasis.opendocument.spreadsheet", "ods", "ots");
        Add(FileKind.Document, "application/vnd.oasis.opendocument.presentation", "odp", "otp");
        Add(FileKind.Document, "application/rtf", "rtf");
        Add(FileKind.Document, "application/vnd.apple.pages", "pages", "numbers", "key");
        Add(FileKind.Document, "application/vnd.ms-xpsdocument", "xps", "oxps");
        Add(FileKind.Document, "image/vnd.djvu", "djvu", "djv");
        Add(FileKind.Document, "application/vnd.ms-outlook", "msg", "pst", "ost");
        Add(FileKind.Document, "message/rfc822", "eml", "mbox");
        Add(FileKind.Document, "application/x-vnd.visio", "vsd", "vsdx");
        Add(FileKind.Document, "application/x-onenote", "one");

        // Ebooks & comics
        Add(FileKind.Ebook, "application/epub+zip", "epub");
        Add(FileKind.Ebook, "application/x-fictionbook+xml", "fb2", "fbz");
        Add(FileKind.Ebook, "application/x-mobipocket-ebook", "mobi", "prc", "azw", "azw3", "kfx");
        Add(FileKind.Ebook, "application/vnd.comicbook+zip", "cbz", "cbr", "cb7", "cbt");
        Add(FileKind.Ebook, "application/x-ibooks+zip", "ibooks");

        // Text
        Add(FileKind.Text, "text/plain", "txt", "text", "nfo", "diz", "me", "readme", "1st", "srt", "ass", "ssa", "vtt", "sub", "lrc", "cue", "m3u", "m3u8", "pls", "log");
        Add(FileKind.Text, "text/markdown", "md", "markdown", "mdx", "rst", "adoc", "org", "tex", "bib");
        Add(FileKind.Text, "text/csv", "csv", "tsv");

        // Code & config
        Add(FileKind.Code, "text/plain", "c", "h", "cc", "cpp", "cxx", "hpp", "hh", "hxx", "cs", "csx", "fs", "fsx", "vb", "java", "kt", "kts", "scala", "groovy",
            "go", "rs", "swift", "m", "mm", "dart", "py", "pyw", "pyi", "rb", "php", "pl", "pm", "lua", "r", "jl", "ex", "exs", "erl", "hs", "clj", "elm",
            "zig", "nim", "v", "d", "asm", "s", "sql", "sh", "bash", "zsh", "fish", "ps1", "psm1", "bat", "cmd", "awk", "sed", "vim", "el", "lisp", "scm",
            "tf", "hcl", "nix", "gradle", "cmake", "mk", "makefile", "dockerfile", "proto", "graphql", "gql", "sol", "vue", "svelte", "astro",
            "csproj", "fsproj", "vbproj", "sln", "slnx", "props", "targets", "editorconfig", "gitignore", "gitattributes", "lock", "patch", "diff");
        Add(FileKind.Code, "text/javascript", "js", "mjs", "cjs", "jsx");
        Add(FileKind.Code, "text/x-typescript", "ts", "tsx", "mts", "cts");
        Add(FileKind.Code, "text/html", "html", "htm", "xhtml", "cshtml", "razor");
        Add(FileKind.Code, "text/css", "css", "scss", "sass", "less", "styl");
        Add(FileKind.Code, "application/json", "json", "jsonc", "json5", "geojson", "ipynb", "har", "webmanifest");
        Add(FileKind.Code, "application/xml", "xml", "xsd", "xsl", "xslt", "plist", "resx", "config", "nuspec", "opf", "nzb", "kml", "gpx");
        Add(FileKind.Code, "application/yaml", "yaml", "yml");
        Add(FileKind.Code, "application/toml", "toml", "ini", "cfg", "conf", "env", "properties");

        // Archives
        Add(FileKind.Archive, "application/zip", "zip", "zipx", "jar", "war", "ear", "apkg", "xpi", "crx", "nupkg", "whl", "vsix");
        Add(FileKind.Archive, "application/vnd.rar", "rar", "r00", "r01");
        Add(FileKind.Archive, "application/x-7z-compressed", "7z");
        Add(FileKind.Archive, "application/x-tar", "tar");
        Add(FileKind.Archive, "application/gzip", "gz", "tgz");
        Add(FileKind.Archive, "application/x-bzip2", "bz2", "tbz", "tbz2");
        Add(FileKind.Archive, "application/x-xz", "xz", "txz");
        Add(FileKind.Archive, "application/zstd", "zst", "tzst");
        Add(FileKind.Archive, "application/x-lzma", "lzma", "lz", "lz4", "lzo");
        Add(FileKind.Archive, "application/x-compress", "z", "cab", "arj", "lzh", "lha", "ace", "cpio", "sit", "sitx");

        // Disk images
        Add(FileKind.DiskImage, "application/x-iso9660-image", "iso", "img", "bin", "cue", "nrg", "mdf", "mds", "cdi", "ccd");
        Add(FileKind.DiskImage, "application/x-virtualbox-vdi", "vdi", "vmdk", "vhd", "vhdx", "qcow", "qcow2", "hdd", "ova", "ovf", "raw", "dmg", "sparseimage", "sparsebundle", "wim", "esd", "swm");

        // Executables & packages
        Add(FileKind.Executable, "application/x-msdownload", "exe", "msi", "msix", "msixbundle", "appx", "appxbundle", "dll", "sys", "com", "scr", "msp");
        Add(FileKind.Executable, "application/vnd.debian.binary-package", "deb", "rpm", "apk", "xapk", "ipa", "appimage", "flatpak", "snap", "pkg", "mpkg", "run", "so", "dylib", "elf", "pyz");

        // Fonts
        Add(FileKind.Font, "font/ttf", "ttf", "otf", "woff", "woff2", "eot", "ttc", "fon", "fnt", "pfb", "pfm");

        // Databases
        Add(FileKind.Database, "application/vnd.sqlite3", "db", "sqlite", "sqlite3", "db3", "mdb", "accdb", "frm", "ibd", "myd", "myi", "dbf", "kdbx", "realm", "ldb", "nsf", "fdb", "gdb", "mdf", "ndf", "bak-db");

        // 3D & CAD
        Add(FileKind.Model3D, "model/stl", "stl", "obj", "fbx", "3ds", "max", "blend", "dae", "gltf", "glb", "3mf", "step", "stp", "iges", "igs", "dwg", "dxf", "skp", "ply", "usd", "usdz", "c4d", "ma", "mb", "f3d", "ipt", "sldprt", "gcode");

        // Fix ambiguous ones: these are more commonly text than images or disks.
        d["ts"] = new TypeInfo(FileKind.Video, "video/mp2t");
        d["bin"] = new TypeInfo(FileKind.Other, "application/octet-stream");
        d["cue"] = new TypeInfo(FileKind.Text, "application/x-cue");
        d["mdf"] = new TypeInfo(FileKind.DiskImage, "application/x-mdf");
        d["log"] = new TypeInfo(FileKind.Text, "text/plain");
        return d;
    }

    public static string? ExtensionOf(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
        {
            // Dotfiles like ".bashrc" and names ending with '.' have no extension.
            return name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ? "dockerfile"
                : name.Equals("Makefile", StringComparison.OrdinalIgnoreCase) ? "makefile"
                : null;
        }

        var ext = name[(dot + 1)..].ToLowerInvariant();
        return ext.Length > 16 ? null : ext;
    }

    public static TypeInfo? FromExtension(string? ext) =>
        ext is not null && ByExt.TryGetValue(ext, out var t) ? t : null;

    /// <summary>Detects common formats from the first bytes of a file.</summary>
    public static TypeInfo? Sniff(ReadOnlySpan<byte> h)
    {
        static bool At(ReadOnlySpan<byte> s, int offset, ReadOnlySpan<byte> sig) =>
            s.Length >= offset + sig.Length && s.Slice(offset, sig.Length).SequenceEqual(sig);

        if (At(h, 0, [0xFF, 0xD8, 0xFF])) return new(FileKind.Image, "image/jpeg");
        if (At(h, 0, [0x89, (byte)'P', (byte)'N', (byte)'G'])) return new(FileKind.Image, "image/png");
        if (At(h, 0, "GIF8"u8)) return new(FileKind.Image, "image/gif");
        if (At(h, 0, "RIFF"u8) && At(h, 8, "WEBP"u8)) return new(FileKind.Image, "image/webp");
        if (At(h, 0, "RIFF"u8) && At(h, 8, "AVI "u8)) return new(FileKind.Video, "video/x-msvideo");
        if (At(h, 0, "RIFF"u8) && At(h, 8, "WAVE"u8)) return new(FileKind.Audio, "audio/wav");
        if (At(h, 0, "BM"u8) && h.Length > 14) return new(FileKind.Image, "image/bmp");
        if (At(h, 0, "II*\0"u8) || At(h, 0, "MM\0*"u8)) return new(FileKind.Image, "image/tiff");
        if (At(h, 4, "ftyp"u8))
        {
            var brand = h.Length >= 12 ? System.Text.Encoding.ASCII.GetString(h.Slice(8, 4)) : "";
            return brand switch
            {
                "heic" or "heix" or "mif1" or "msf1" or "heim" or "heis" => new(FileKind.Image, "image/heic"),
                "avif" or "avis" => new(FileKind.Image, "image/avif"),
                "M4A " or "M4B " => new(FileKind.Audio, "audio/mp4"),
                "qt  " => new(FileKind.Video, "video/quicktime"),
                "crx " => new(FileKind.Image, "image/x-canon-cr3"),
                _ => new(FileKind.Video, "video/mp4"),
            };
        }

        if (At(h, 0, [0x1A, 0x45, 0xDF, 0xA3])) return new(FileKind.Video, "video/x-matroska");
        if (At(h, 0, "FLV"u8)) return new(FileKind.Video, "video/x-flv");
        if (At(h, 0, [0x00, 0x00, 0x01, 0xBA])) return new(FileKind.Video, "video/mpeg");
        if (h.Length > 188 && h[0] == 0x47 && h[188] == 0x47) return new(FileKind.Video, "video/mp2t");
        if (At(h, 0, "ID3"u8) || (h.Length > 1 && h[0] == 0xFF && (h[1] & 0xE0) == 0xE0)) return new(FileKind.Audio, "audio/mpeg");
        if (At(h, 0, "fLaC"u8)) return new(FileKind.Audio, "audio/flac");
        if (At(h, 0, "OggS"u8)) return new(FileKind.Audio, "audio/ogg");
        if (At(h, 0, "%PDF"u8)) return new(FileKind.Pdf, "application/pdf");
        if (At(h, 0, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1])) return new(FileKind.Document, "application/x-ole-storage");
        if (At(h, 0, "PK\x03\x04"u8))
        {
            if (At(h, 30, "mimetypeapplication/epub+zip"u8)) return new(FileKind.Ebook, "application/epub+zip");
            return new(FileKind.Archive, "application/zip");
        }

        if (At(h, 0, "Rar!"u8)) return new(FileKind.Archive, "application/vnd.rar");
        if (At(h, 0, [(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C])) return new(FileKind.Archive, "application/x-7z-compressed");
        if (At(h, 0, [0x1F, 0x8B])) return new(FileKind.Archive, "application/gzip");
        if (At(h, 0, "BZh"u8)) return new(FileKind.Archive, "application/x-bzip2");
        if (At(h, 0, [0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0x00])) return new(FileKind.Archive, "application/x-xz");
        if (At(h, 0, [0x28, 0xB5, 0x2F, 0xFD])) return new(FileKind.Archive, "application/zstd");
        if (At(h, 257, "ustar"u8)) return new(FileKind.Archive, "application/x-tar");
        if (At(h, 0x8001, "CD001"u8)) return new(FileKind.DiskImage, "application/x-iso9660-image");
        if (At(h, 0, "KDMV"u8)) return new(FileKind.DiskImage, "application/x-vmdk");
        if (At(h, 0, [(byte)'Q', (byte)'F', (byte)'I', 0xFB])) return new(FileKind.DiskImage, "application/x-qemu-disk");
        if (At(h, 0, "conectix"u8) || At(h, 0, "vhdxfile"u8)) return new(FileKind.DiskImage, "application/x-vhd");
        if (At(h, 0, [0x7F, (byte)'E', (byte)'L', (byte)'F'])) return new(FileKind.Executable, "application/x-elf");
        if (At(h, 0, "MZ"u8)) return new(FileKind.Executable, "application/x-msdownload");
        if (At(h, 0, [0xCF, 0xFA, 0xED, 0xFE]) || At(h, 0, [0xCA, 0xFE, 0xBA, 0xBE])) return new(FileKind.Executable, "application/x-mach-binary");
        if (At(h, 0, "!<arch>\ndebian"u8)) return new(FileKind.Executable, "application/vnd.debian.binary-package");
        if (At(h, 0, [0xED, 0xAB, 0xEE, 0xDB])) return new(FileKind.Executable, "application/x-rpm");
        if (At(h, 0, "SQLite format 3\0"u8)) return new(FileKind.Database, "application/vnd.sqlite3");
        if (At(h, 0, "wOFF"u8) || At(h, 0, "wOF2"u8) || At(h, 0, "OTTO"u8) || At(h, 0, [0x00, 0x01, 0x00, 0x00, 0x00])) return new(FileKind.Font, "font/ttf");
        if (At(h, 0, "8BPS"u8)) return new(FileKind.Image, "image/vnd.adobe.photoshop");
        if (At(h, 0, "AT&TFORM"u8)) return new(FileKind.Document, "image/vnd.djvu");
        if (At(h, 0, "BLENDER"u8)) return new(FileKind.Model3D, "application/x-blender");
        if (At(h, 0, "solid "u8)) return new(FileKind.Model3D, "model/stl");
        if (At(h, 0, "d8:announce"u8)) return new(FileKind.Other, "application/x-bittorrent");

        return LooksLikeText(h) ? new(FileKind.Text, "text/plain") : null;
    }

    /// <summary>No NUL bytes, few control characters, valid UTF-8 (a truncated trailing sequence is allowed).</summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> h)
    {
        if (h.IsEmpty)
        {
            return false;
        }

        if (At3(h, [0xEF, 0xBB, 0xBF]) || At3(h, [0xFF, 0xFE]) || At3(h, [0xFE, 0xFF]))
        {
            return true;
        }

        var control = 0;
        foreach (var b in h)
        {
            if (b == 0)
            {
                return false;
            }

            if (b < 0x09 || (b > 0x0D && b < 0x20 && b != 0x1B))
            {
                control++;
            }
        }

        if (control > h.Length / 50)
        {
            return false;
        }

        var status = System.Text.Unicode.Utf8.ToUtf16(h, new char[h.Length], out _, out _, replaceInvalidSequences: false, isFinalBlock: false);
        return status is System.Buffers.OperationStatus.Done or System.Buffers.OperationStatus.NeedMoreData;

        static bool At3(ReadOnlySpan<byte> s, ReadOnlySpan<byte> sig) => s.Length >= sig.Length && s[..sig.Length].SequenceEqual(sig);
    }
}

using ANalyze.Actions;
using ANalyze.Domain;
using ANalyze.Infrastructure.Roots;
using ANalyze.Integrations;
using ANalyze.Integrations.Plex;
using ANalyze.Scanner;

namespace ANalyze.Tests;

public class RuleEngineTests
{
    private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static EntryFacts File(string relPath, long size = 100, FileKind kind = FileKind.Unknown, long ageDays = 0)
    {
        var name = relPath[(relPath.LastIndexOf('/') + 1)..];
        return new EntryFacts(name, relPath, FileTypes.ExtensionOf(name), null, kind, size, Now - (ageDays * 86400), Now, relPath.Count(c => c == '/') + 1);
    }

    private static RuleClause C(RuleField f, RuleOp op, string v, bool negate = false) => new() { Field = f, Op = op, Value = v, Negate = negate };

    private static Rule R(int id, string name, int? cat, params RuleClause[] clauses) =>
        new() { Id = id, Name = name, Order = id, CategoryId = cat, Condition = new RuleCondition { Clauses = [.. clauses] } };

    [Fact]
    public void First_category_wins_and_tags_accumulate()
    {
        var tag = R(1, "huge", null, C(RuleField.Size, RuleOp.GreaterThan, "1KB"));
        tag.Tags = ["huge"];
        tag.StopProcessing = false;
        var engine = new RuleEngine([tag, R(2, "video", 10, C(RuleField.Extension, RuleOp.In, "mkv,mp4")), R(3, "any", 20, C(RuleField.Size, RuleOp.GreaterThan, "0"))]);
        var res = engine.Evaluate(File("Movies/a.mkv", 5000), Now);
        Assert.Equal(10, res.CategoryId);
        Assert.Equal(["huge"], res.Tags);
    }

    [Theory]
    [InlineData("proj/node_modules/lodash/index.js", true)]
    [InlineData("node_modules/x.js", true)]
    [InlineData("proj/src/node_modules.txt", false)]
    [InlineData("node_modules", false)]
    public void PathSegment_matches_parent_folders_only(string path, bool expected)
    {
        var engine = new RuleEngine([R(1, "deps", 1, C(RuleField.PathSegment, RuleOp.In, "node_modules,.venv"))]);
        Assert.Equal(expected, engine.Evaluate(File(path), Now).CategoryId == 1);
    }

    [Fact]
    public void Glob_regex_negate_and_any()
    {
        var engine = new RuleEngine([
            new Rule
            {
                Id = 1, Name = "x", CategoryId = 1,
                Condition = new RuleCondition
                {
                    Match = MatchMode.Any,
                    Clauses = [C(RuleField.Path, RuleOp.Glob, "Photos/**/*.jpg"), C(RuleField.Name, RuleOp.Regex, @"^IMG_\d+\.png$")],
                },
            },
            new Rule { Id = 2, Name = "not-old", CategoryId = 2, Order = 2, Condition = new RuleCondition { Clauses = [C(RuleField.ModifiedDays, RuleOp.GreaterThan, "365", negate: true)] } },
        ]);
        Assert.Equal(1, engine.Evaluate(File("Photos/2021/x/a.jpg"), Now).CategoryId);
        Assert.Equal(1, engine.Evaluate(File("Other/IMG_0001.png"), Now).CategoryId);
        Assert.Equal(2, engine.Evaluate(File("Other/a.txt", ageDays: 10), Now).CategoryId);
        Assert.Null(engine.Evaluate(File("Other/a.txt", ageDays: 1000), Now).CategoryId);
    }

    [Theory]
    [InlineData("1500", 1500L)]
    [InlineData("10KB", 10240L)]
    [InlineData("1.5 GB", 1610612736L)]
    [InlineData("2GiB", 2147483648L)]
    public void ParseSize(string input, long expected) => Assert.Equal(expected, RuleEngine.ParseSize(input));

    [Fact]
    public void Invalid_regex_never_matches_instead_of_throwing()
    {
        var engine = new RuleEngine([R(1, "bad", 1, C(RuleField.Name, RuleOp.Regex, "(unclosed"))]);
        Assert.Null(engine.Evaluate(File("a.txt"), Now).CategoryId);
    }

    [Fact]
    public void Builtin_rules_categorize_common_files()
    {
        var cats = Builtins.Categories.Select((c, i) => (c.Name, Id: i + 1)).ToDictionary(x => x.Name, x => x.Id);
        var rules = Builtins.Rules.Select((r, i) => new Rule
        {
            Id = i + 1, Name = r.Name, Order = i, Condition = r.Condition, CategoryId = r.Category is null ? null : cats[r.Category], Tags = [.. r.Tags], StopProcessing = r.Stop,
        });
        var engine = new RuleEngine(rules);
        int? Cat(string path, FileKind kind = FileKind.Unknown) => engine.Evaluate(File(path, kind: kind), Now).CategoryId;
        Assert.Equal(cats["Dev junk"], Cat("app/node_modules/react/index.js", FileKind.Code));
        Assert.Equal(cats["System files"], Cat("Photos/Thumbs.db"));
        Assert.Equal(cats["System files"], Cat("Photos/@eaDir/a.jpg/SYNOFILE_THUMB_M.jpg", FileKind.Image));
        Assert.Equal(cats["Incomplete downloads"], Cat("Downloads/ubuntu.iso.part"));
        Assert.Equal(cats["Video"], Cat("Movies/x.mkv", FileKind.Video));
        Assert.Equal(cats["Video"], Cat("Movies/x.en.srt", FileKind.Text));
        Assert.Equal(cats["RAW photos"], Cat("DCIM/IMG_1.CR2", FileKind.Image));
        Assert.Equal(cats["Images"], Cat("Pictures/Screenshot 2024-01-01.png", FileKind.Image));
        Assert.Contains("screenshot", engine.Evaluate(File("Pictures/Screenshot 2024-01-01.png", kind: FileKind.Image), Now).Tags);
        Assert.Contains("copy-name", engine.Evaluate(File("Docs/report (1).docx", kind: FileKind.Document), Now).Tags);
        Assert.Equal(cats["Logs"], Cat("var/app.log.3.gz", FileKind.Archive));
    }
}

public class PathTemplateTests
{
    private static readonly TemplateFacts Facts = new("IMG 1.jpg", "Camera/2021/IMG 1.jpg", "jpg", "Images", "Image", 1625097600, 5_000_000);

    [Fact]
    public void Expands_tokens()
    {
        Assert.Equal("Sorted/Images/2021/IMG 1.jpg", PathTemplate.Expand("Sorted/{category}/{mtime:yyyy}/{name}", Facts));
        Assert.Equal("By type/jpg/IMG 1", PathTemplate.Expand("By type/{ext}/{stem}", Facts));
        Assert.Equal("Archive/Camera/2021/IMG 1.jpg", PathTemplate.Expand("Archive/{relDir}/{name}", Facts));
        Assert.Equal("1-100 MB/2021/IMG 1.jpg", PathTemplate.Expand("{size}/{parent}/{name}", Facts));
    }

    [Fact]
    public void Token_values_cannot_escape()
    {
        var evil = Facts with { Name = "../../etc/passwd", Category = ".." };
        var path = PathTemplate.Expand("{category}/{name}", evil);
        Assert.DoesNotContain("..", path.Split('/'));
        Assert.Throws<ArgumentException>(() => PathTemplate.Expand("../{name}", Facts));
    }

    [Fact]
    public void Suffix_before_extension() => Assert.Equal("a/b (2).txt", PathTemplate.WithSuffix("a/b.txt", 2));
}

public class DedupeSelectorTests
{
    private static readonly DupMember[] Members =
    [
        new(1, 1, "Backup/2021/Photos/a.jpg", 300, "1"),
        new(2, 1, "Photos/a.jpg", 200, "2"),
        new(3, 1, "Downloads/a (1).jpg", 100, "3"),
    ];

    [Fact]
    public void Strategies()
    {
        Assert.Equal(3, DedupeSelector.ChooseKeep(Members, DedupeKeep.Oldest, []).EntryId);
        Assert.Equal(1, DedupeSelector.ChooseKeep(Members, DedupeKeep.Newest, []).EntryId);
        Assert.Equal(2, DedupeSelector.ChooseKeep(Members, DedupeKeep.ShortestPath, []).EntryId);
        Assert.Equal(1, DedupeSelector.ChooseKeep(Members, DedupeKeep.LongestPath, []).EntryId);
    }

    [Fact]
    public void Preferred_folder_in_list_order_with_fallback()
    {
        Assert.Equal(2, DedupeSelector.ChooseKeep(Members, DedupeKeep.PreferredFolder, ["Music", "Photos", "Backup"]).EntryId);
        Assert.Equal(3, DedupeSelector.ChooseKeep(Members, DedupeKeep.PreferredFolder, ["Nowhere"]).EntryId);
        // "Photo" must not match "Photos".
        Assert.Equal(3, DedupeSelector.ChooseKeep(Members, DedupeKeep.PreferredFolder, ["Photo"]).EntryId);
    }
}

public class FileTypesTests
{
    [Fact]
    public void Sniffs_common_signatures()
    {
        Assert.Equal(FileKind.Image, FileTypes.Sniff([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A])?.Kind);
        Assert.Equal(FileKind.Pdf, FileTypes.Sniff("%PDF-1.7"u8)?.Kind);
        Assert.Equal(FileKind.Archive, FileTypes.Sniff("PK\x03\x04rest"u8)?.Kind);
        Assert.Equal(FileKind.Database, FileTypes.Sniff("SQLite format 3\0"u8)?.Kind);
        Assert.Equal(FileKind.Text, FileTypes.Sniff("hello world\nsecond line"u8)?.Kind);
        Assert.Null(FileTypes.Sniff([0x00, 0x01, 0x02, 0x03, 0xFF, 0x00]));
    }

    [Theory]
    [InlineData("movie.MKV", "mkv")]
    [InlineData(".bashrc", null)]
    [InlineData("archive.tar.gz", "gz")]
    [InlineData("Dockerfile", "dockerfile")]
    public void Extension(string name, string? ext) => Assert.Equal(ext, FileTypes.ExtensionOf(name));
}

public class PathSafetyTests
{
    [Fact]
    public void Combine_refuses_escapes()
    {
        Assert.Equal("/library/a/b", RootRegistry.Combine("/library", "a/b"));
        Assert.Throws<UnauthorizedAccessException>(() => RootRegistry.Combine("/library", "../etc/passwd"));
        Assert.Throws<UnauthorizedAccessException>(() => RootRegistry.Combine("/library", "a/../../x"));
        Assert.False(RootRegistry.IsUnder("/library", "/library2/x"));
    }

    [Fact]
    public void Ignore_matcher()
    {
        var m = new IgnoreMatcher([".snapshot", "Media/Cache/**", "# comment", ""]);
        Assert.True(m.IsIgnored("a/.snapshot", ".snapshot", 2));
        Assert.True(m.IsIgnored("Media/Cache/x/y", "y", 4));
        Assert.False(m.IsIgnored("Media/Other", "Other", 2));
        Assert.True(m.IsIgnored(".analyze-trash", ".analyze-trash", 1));
        Assert.False(m.IsIgnored("x/.analyze-trash", ".analyze-trash", 2));
    }
}

public class IntegrationPathTests
{
    [Fact]
    public void Map_uses_longest_prefix()
    {
        PathMapping[] maps = [new("/data", "/library"), new("/data/movies", "/media/Movies")];
        Assert.Equal("/media/Movies/A/a.mkv", IntegrationSettings.MapPath("/data/movies/A/a.mkv", maps));
        Assert.Equal("/library/tv/x.mkv", IntegrationSettings.MapPath("/data/tv/x.mkv", maps));
        Assert.Equal("/other/x.mkv", IntegrationSettings.MapPath("/other/x.mkv", maps));
        Assert.Equal("/library/x", IntegrationSettings.MapPath("/datax/x".Replace("/datax", "/data"), maps));
    }

    [Fact]
    public void Common_suffix_split_finds_prefixes() =>
        Assert.Equal(("/data/movies", "/library/Media/Movies"), PathMatcher.CommonSuffixSplit("/data/movies/Heat (1995)/Heat.mkv", "/library/Media/Movies/Heat (1995)/Heat.mkv"));
}

public class ReleaseNotesTests
{
    [Fact]
    public void Parses_versions_dates_kinds_and_continuations()
    {
        var notes = ANalyze.Web.Infrastructure.ReleaseNotes.Parse("""
            # Release Notes

            ## v0.2.0 — 2026-11-01

            - feat: release notes page
              with a second line
            - fix: something
            - plain bullet

            ## v0.1.0
            - refactor: old
            """);
        Assert.Equal(2, notes.Count);
        Assert.Equal("0.2.0", notes[0].Version);
        Assert.Equal("2026-11-01", notes[0].Date);
        Assert.Equal(["feat", "fix", "other"], notes[0].Items.Select(i => i.Kind));
        Assert.Equal("release notes page with a second line", notes[0].Items[0].Text);
        Assert.Null(notes[1].Date);
        Assert.Equal("old", notes[1].Items[0].Text);
    }

    [Fact]
    public void Embedded_file_is_present() => Assert.NotEmpty(ANalyze.Web.Infrastructure.ReleaseNotes.All);
}

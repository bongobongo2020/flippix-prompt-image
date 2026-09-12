using System.IO;
using System.Text.Json;
using FlipPix.UI.Services;

namespace FlipPix.Tests;

/// <summary>
/// <see cref="CastSheetLibrary.FindInOwnClothesAsync"/> — ⚡ H3 Express with "their own clothes" reuses a sheet
/// of the same photo showing the clothes in it. The outfit is read by a vision model that never words one photo
/// the same way twice, so an exact-text match alone would rebuild every sheet; and a sheet of a stranger must
/// never come back on a file name alone.
/// </summary>
public sealed class CastSheetLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flippix-sheetlib-" + Guid.NewGuid().ToString("N"));
    private readonly string _sheets;
    private readonly string _photo;
    private readonly string _stranger;

    public CastSheetLibraryTests()
    {
        _sheets = Path.Combine(_root, "cast", "sheets");
        Directory.CreateDirectory(Path.Combine(_root, "out"));
        _photo = WriteFile(Path.Combine(_root, "photos", "me.png"), "me");
        _stranger = WriteFile(Path.Combine(_root, "elsewhere", "me.png"), "somebody else");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static string WriteFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string Sheet(string name) => WriteFile(Path.Combine(_root, "out", name), name);

    [Fact]
    public void Sheets_live_in_Pictures_cast_sheets_and_photos_one_level_up()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        Assert.Equal(Path.Combine(pictures, "cast", "sheets"), CastSheetLibrary.DefaultFolder);
        Assert.Equal(Path.Combine(pictures, "cast"), new CastSheetLibrary().PhotoFolder);
        Assert.Equal(Path.Combine(_root, "cast"), new CastSheetLibrary(_sheets + Path.DirectorySeparatorChar).PhotoFolder);
    }

    [Fact]
    public async Task An_own_clothes_sheet_is_found_under_different_words_and_filed_in_the_folder()
    {
        var library = new CastSheetLibrary(_sheets);
        await library.RecordAsync(_photo, Sheet("sheet_1_a.png"), 1, "man", "black tank top, low-cut jeans.", ownClothes: true);

        var match = await new CastSheetLibrary(_sheets).FindInOwnClothesAsync(_photo, "a black tank top and jeans");

        Assert.NotNull(match);
        Assert.Equal("sheet_1_a.png", match!.Entry.SheetFile);
        Assert.True(match.Entry.OwnClothes);
        Assert.True(File.Exists(Path.Combine(_sheets, "sheet_1_a.png")));
    }

    [Fact]
    public async Task A_sheet_built_with_no_outfit_counts_but_one_dressed_for_a_story_does_not()
    {
        var library = new CastSheetLibrary(_sheets);
        await library.RecordAsync(_photo, Sheet("sheet_1_story.png"), 1, "man", "a red silk skirt");
        Assert.Null(await library.FindInOwnClothesAsync(_photo, "black tank top"));

        await library.RecordAsync(_photo, Sheet("sheet_1_plain.png"), 1, "man", null);
        var match = await library.FindInOwnClothesAsync(_photo, "black tank top");

        Assert.Equal("sheet_1_plain.png", match?.Entry.SheetFile);
    }

    [Fact]
    public async Task The_exact_outfit_wins_over_a_newer_sheet_in_other_words()
    {
        var library = new CastSheetLibrary(_sheets);
        await library.RecordAsync(_photo, Sheet("sheet_1_exact.png"), 1, "man", "Black tank top, jeans.", ownClothes: true);
        await library.RecordAsync(_photo, Sheet("sheet_1_newer.png"), 1, "man", "tank top in black", ownClothes: true);

        var match = await library.FindInOwnClothesAsync(_photo, "black  tank top, jeans");

        Assert.Equal("sheet_1_exact.png", match?.Entry.SheetFile);
    }

    [Fact]
    public async Task A_file_name_alone_never_brings_back_a_sheet_in_other_clothes()
    {
        // A back-filled entry: the build log only ever knew the photo's file name.
        Directory.CreateDirectory(_sheets);
        File.WriteAllText(Path.Combine(_sheets, "sheet_1_log.png"), "log");
        File.WriteAllText(Path.Combine(_sheets, "sheet-index.json"), JsonSerializer.Serialize(new
        {
            Version = 1,
            LastLogScanUtc = DateTime.UtcNow,
            Entries = new[] { new { SheetFile = "sheet_1_log.png", SourceFile = "me.png", Slot = 1, BuiltUtc = DateTime.UtcNow } }
        }));

        var library = new CastSheetLibrary(_sheets);

        Assert.Null(await library.FindInOwnClothesAsync(_stranger, "black tank top"));
        Assert.Null(await library.FindInOwnClothesAsync(_stranger, null));
    }

    [Fact]
    public async Task Moved_photo_still_matches_on_its_bytes()
    {
        var library = new CastSheetLibrary(_sheets);
        await library.RecordAsync(_photo, Sheet("sheet_2_a.png"), 2, "woman", "grey hoodie", ownClothes: true);
        var moved = WriteFile(Path.Combine(_root, "cast", "renamed.png"), "me");

        var match = await library.FindInOwnClothesAsync(moved, null);

        Assert.Equal(CastSheetLibrary.MatchKind.SourceHash, match?.Confidence);
    }

    [Theory]
    [InlineData("Black tank top, jeans.", "black  tank top, jeans", true)]
    [InlineData("black tank top", "black tank top, jeans", false)]
    [InlineData("", "", false)]
    public void SameOutfit_ignores_case_spacing_and_the_full_stop(string a, string b, bool same) =>
        Assert.Equal(same, CastSheetLibrary.SameOutfit(a, b));
}

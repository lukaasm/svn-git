namespace Sg.Core.Tests;

public sealed class CheckoutAppearanceTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sg-icons-" + Guid.NewGuid().ToString("N"));
    readonly SgRoot _root;
    static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a1V8AAAAASUVORK5CYII=");

    public CheckoutAppearanceTests() => _root = SgRoot.Create(_dir,
        new SgConfig { Checkouts = [new() { Name = "Fort" }, new() { Name = "Dashboard" }] }, new CollectingLog());

    [Fact]
    public void ImagesPersistAsPortableReferencesAndResetKeepsOtherCheckoutsImage()
    {
        CheckoutAppearance.SetIcon(_root, "Fort", Png);
        CheckoutAppearance.SetIcon(_root, "Dashboard", Png);
        var path = CheckoutAppearance.IconPath(_root, _root.Checkout("Fort"))!;
        Assert.Equal(Png, File.ReadAllBytes(path));
        var saved = SgConfig.Load(_root.ConfigPath);
        Assert.Equal(_root.Checkout("Fort").Icon, saved.Checkouts[0].Icon);
        Assert.False(Path.IsPathRooted(saved.Checkouts[0].Icon!));
        CheckoutAppearance.SetIcon(_root, "Fort", null);
        Assert.Equal("", SgConfig.Load(_root.ConfigPath).Checkouts[0].Icon);
        Assert.True(File.Exists(path));
        CheckoutAppearance.SetIcon(_root, "Dashboard", null);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("../outside.png")]
    [InlineData("C:/outside.png")]
    [InlineData("https://example.com/icon.png")]
    [InlineData("icons/../../../outside.png")]
    public void ConfigCannotNameAnExternalImage(string name)
    {
        _root.Checkout("Fort").Icon = name;
        Assert.Null(CheckoutAppearance.IconPath(_root, _root.Checkout("Fort")));
        CheckoutAppearance.SetIcon(_root, "Fort", null);
        Assert.Equal("", _root.Checkout("Fort").Icon);
    }

    [Fact]
    public void FailedImageValidationLeavesTheExistingIconIntact()
    {
        CheckoutAppearance.SetIcon(_root, "Fort", Png);
        var before = File.ReadAllText(_root.ConfigPath);
        Assert.Throws<SgException>(() => CheckoutAppearance.SetIcon(_root, "Fort", "not an image"u8.ToArray()));
        Assert.Equal(before, File.ReadAllText(_root.ConfigPath));
        Assert.Equal(Png, File.ReadAllBytes(CheckoutAppearance.IconPath(_root, _root.Checkout("Fort"))!));
    }

    [Fact]
    public void FailedConfigSaveKeepsThePreviousSelectionInMemory()
    {
        File.Delete(_root.ConfigPath);
        Directory.CreateDirectory(_root.ConfigPath);
        var error = Record.Exception(() => CheckoutAppearance.SetIcon(_root, "Fort", Png));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Null(_root.Checkout("Fort").Icon);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}

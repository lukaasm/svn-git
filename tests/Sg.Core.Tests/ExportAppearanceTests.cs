using System.IO.Compression;
using System.Text.Json.Nodes;

namespace Sg.Core.Tests;

public sealed partial class ExportTests
{
    static readonly byte[] IconPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a1V8AAAAASUVORK5CYII=");

    [Fact]
    public void Appearance_travels_to_a_differently_named_checkout_without_entering_source_history()
    {
        f.Setup();
        var wt = MakeBranch("portable-icon");
        CheckoutAppearance.SetIcon(f.Root, f.Co.Name, IconPng);
        var file = File_();
        Export.Write(f.Root, wt, file);
        var meta = Export.Read(file);
        Assert.True(meta.HasAppearance);
        Assert.Equal(IconPng, meta.AppearanceIcon);
        Assert.DoesNotContain("appearanceIcon", System.Text.Json.JsonSerializer.Serialize(meta, SgConfig.JsonOptions));
        using (var zip = ZipFile.OpenRead(file))
        {
            Assert.Single(zip.Entries, e => e.FullName == "appearance.json");
            Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("icons/"));
        }

        var (far, co) = Far();
        Ops.UpdateCheckout(far, co, new Ops.CheckoutEdit(Name: "OtherMachine"));
        var imported = Export.Import(far, file);
        Assert.True(imported.Ok, imported.Why);
        Assert.Equal(2, imported.Applied);
        Assert.True(imported.CheckoutAppearanceRestored);
        Assert.Null(imported.CheckoutAppearanceWarning);
        Assert.Equal(IconPng, File.ReadAllBytes(CheckoutAppearance.IconPath(far, co)!));
        Assert.Equal(f.Co.Icon, SgConfig.Load(far.ConfigPath).Checkouts.Single().Icon);
        Assert.DoesNotContain("appearance.json", far.Git.Out(imported.Path, "ls-files"));
        Assert.Equal(TaskState.Succeeded, TaskResults.Describe(imported).State);
        Assert.Contains("Checkout appearance restored", TaskResults.Describe(imported).Detail);
    }

    [Fact]
    public void Appearance_explicit_initials_travel_and_imports_preserve_existing_local_choices()
    {
        f.Setup();
        var wt = MakeBranch("appearance-choices");
        var imageFile = File_();
        CheckoutAppearance.SetIcon(f.Root, f.Co.Name, IconPng);
        Export.Write(f.Root, wt, imageFile);
        CheckoutAppearance.SetIcon(f.Root, f.Co.Name, null);
        var resetFile = Path.Combine(f.Base, "reset.sgexport");
        Export.Write(f.Root, wt, resetFile);
        Assert.True(Export.Read(resetFile).HasAppearance);
        Assert.Null(Export.Read(resetFile).AppearanceIcon);

        var (far, co) = Far();
        Assert.True(Export.Import(far, resetFile, asBranch: "initials").CheckoutAppearanceRestored);
        Assert.Equal("", co.Icon);
        Assert.False(Export.Import(far, imageFile, asBranch: "kept-initials").CheckoutAppearanceRestored);
        Assert.Equal("", co.Icon);
        Assert.False(Directory.Exists(Path.Combine(far.StorePath, "icons")));
        CheckoutAppearance.SetIcon(far, co.Name, IconPng);
        Assert.False(Export.Import(far, resetFile, asBranch: "kept-custom").CheckoutAppearanceRestored);
        Assert.Equal(IconPng, File.ReadAllBytes(CheckoutAppearance.IconPath(far, co)!));
    }

    [Fact]
    public void Appearance_invalid_metadata_is_rejected_before_import_and_missing_source_does_not_replace_an_export()
    {
        f.Setup();
        var wt = MakeBranch("invalid-appearance");
        CheckoutAppearance.SetIcon(f.Root, f.Co.Name, IconPng);
        var file = File_();
        Export.Write(f.Root, wt, file);
        var original = File.ReadAllBytes(file);
        var icon = f.Co.Icon;
        foreach (var payload in new[] { "not json", "{\"Format\":2}", "{\"Format\":1,\"Icon\":\"eA==\"}", new string(' ', 800_000), null, "duplicate", "unannounced" })
        {
            File.WriteAllBytes(file, original);
            using (var zip = ZipFile.Open(file, ZipArchiveMode.Update))
            {
                if (payload == "unannounced")
                {
                    var meta = zip.GetEntry("export.json")!;
                    JsonObject json;
                    using (var reader = new StreamReader(meta.Open())) json = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
                    json.Remove("hasAppearance");
                    meta.Delete();
                    using var writer = new StreamWriter(zip.CreateEntry("export.json").Open());
                    writer.Write(json.ToJsonString());
                }
                else
                {
                    if (payload != "duplicate") zip.GetEntry("appearance.json")!.Delete();
                    if (payload != null)
                    {
                        using var writer = new StreamWriter(zip.CreateEntry("appearance.json").Open());
                        writer.Write(payload);
                    }
                }
            }
            Assert.Throws<SgException>(() => Export.Read(file));
            Assert.Throws<SgException>(() => Export.Import(f.Root, file, asBranch: "must-not-exist"));
            Assert.Null(f.Root.Git.RefSha("refs/heads/must-not-exist"));
            Assert.False(Directory.Exists(f.Root.WorktreePathFor("must-not-exist")));
            Assert.Equal(icon, f.Co.Icon);
        }

        File.WriteAllBytes(file, original);
        File.Delete(CheckoutAppearance.IconPath(f.Root, f.Co)!);
        var missing = Assert.Throws<SgException>(() => Export.Write(f.Root, wt, file));
        Assert.Contains("Choose an image again or use initials", missing.Message);
        Assert.Equal(original, File.ReadAllBytes(file));
    }

    [Fact]
    public void Appearance_write_failure_reports_imported_commits_and_a_recoverable_warning()
    {
        f.Setup();
        var wt = MakeBranch("icon-warning");
        CheckoutAppearance.SetIcon(f.Root, f.Co.Name, IconPng);
        var file = File_();
        Export.Write(f.Root, wt, file);
        var (far, co) = Far();
        File.WriteAllText(Path.Combine(far.StorePath, "icons"), "cannot create a folder here");
        var imported = Export.Import(far, file);
        Assert.True(imported.Ok, imported.Why);
        Assert.Equal(2, imported.Applied);
        Assert.False(imported.CheckoutAppearanceRestored);
        Assert.Null(co.Icon);
        Assert.NotNull(imported.CheckoutAppearanceWarning);
        Assert.Equal(TaskState.NeedsAttention, TaskResults.Describe(imported).State);
        Assert.Contains(imported.CheckoutAppearanceWarning, TaskResults.Describe(imported).Detail);
        Assert.Equal("int engine = 2;\n", Read(imported.Path, "schmetterling/engine.cpp"));
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;

namespace Pigeon.Manager.Desktop;

public sealed partial class MainWindow {
    async Task LibrarySmoke(string output, string v1, string v2) {
        // Explicit test-only root; this probe must never act on real server profiles.
        if (!File.Exists(Path.Combine(workspace.Root, ".manager-library-test")) || workspace.Load().Count != 0)
            throw new IOException("LIBRARY_SMOKE_REQUIRES_MARKED_EMPTY_FIXTURE");
        Directory.CreateDirectory(output);
        var checks = new List<object>(); int passed = 0, failed = 0;
        void Check(string name, bool ok) { checks.Add(new { name, pass = ok }); if (ok) passed++; else failed++; }
        async Task Wait(Func<bool> predicate) {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!predicate() && timer.ElapsedMilliseconds < 20000) await Task.Delay(50);
            if (!predicate()) throw new IOException("LIBRARY_SMOKE_WAIT_TIMEOUT");
        }
        try {
            await LibraryRequest(new("shared-library-organize"));
            var imported = await LibraryRequest(new("shared-library-import", Files: [v1, v2]), 120_000);
            var old = imported.Library!.Archived.Single(); var current = imported.Library.Current.Single();
            Check("import-single-current-and-one-history", old.Sha256 == Hashes.FileSha(v1) && current.Sha256 == Hashes.FileSha(v2));
            PluginsNav.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => libraryInitialized && !libraryLoading && libraryArtifacts.Length == 2);
            Check("default-shows-current-only", ShowArchivedPlugins.IsChecked != true && ArtifactList.Items.Count == 1);
            Check("main-directory-single-normal-dll", Directory.GetFiles(workspace.Library, "*.dll").Length == 1
                && !Directory.GetFiles(workspace.Library).Any(f => f.Contains("variant", StringComparison.OrdinalIgnoreCase)));
            ShowArchivedPlugins.IsChecked = true;
            await Wait(() => ArtifactList.Items.Count == 2);
            Check("history-toggle-real-list", ArtifactList.Items.Cast<ArtifactChoice>().Count(r => r.Archived) == 1);
            ArtifactList.SelectedItem = artifactChoices.Single(c => c.Artifact.Sha256 == old.Sha256);
            RestoreLibraryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => !libraryBusy && currentLibraryKeys.Contains(ArtifactKey(old)));
            Check("restore-real-button-host-roundtrip", Hashes.FileSha(Path.Combine(workspace.Library, old.FileName)) == old.Sha256);
            Check("restore-does-not-start-or-create-servers", workspace.Load().Count == 0 && states.Length == 0);
            OrganizeLibraryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Wait(() => !libraryBusy);
            Check("organize-idempotent-after-explicit-rollback", Hashes.FileSha(Path.Combine(workspace.Library, old.FileName)) == old.Sha256);
            ShowArchivedPlugins.IsChecked = false;
            await Wait(() => ArtifactList.Items.Count == 1);
            Check("rollback-current-visible-and-history-hidden", ((ArtifactChoice)ArtifactList.Items[0]!).Artifact.Sha256 == old.Sha256);
            Check("buttons-actually-rendered", OrganizeLibraryButton.IsEffectivelyVisible && RestoreLibraryButton.IsEffectivelyVisible
                && OrganizeLibraryButton.Bounds.Width > 0 && RestoreLibraryButton.Bounds.Width > 0);
            Check("failed-import-action-rendered", CancelLibraryImportButton.IsEffectivelyVisible && CancelLibraryImportButton.Bounds.Width > 0);
            Check("compact-requires-real-stopped-target", !CompactRuntimeButton.IsEnabled);
            Width = 1510; Height = 1000; await Task.Delay(350);
            async Task Snap(string name) {
                using var bitmap = new RenderTargetBitmap(new PixelSize((int)(Bounds.Width * RenderScaling), (int)(Bounds.Height * RenderScaling)), new Vector(96 * RenderScaling, 96 * RenderScaling));
                bitmap.Render(this); bitmap.Save(Path.Combine(output, name + ".png"), PngBitmapEncoderOptions.Default);
            }
            await Snap("library-current");
            ShowArchivedPlugins.IsChecked = true; Width = 1220; Height = 800; await Task.Delay(350);
            await Snap("library-history-minimum");
            Check("minimum-toolbar-no-overlap", OrganizeLibraryButton.TranslatePoint(new Point(), this) is { } point
                && point.X >= 0 && RestoreLibraryButton.TranslatePoint(new Point(RestoreLibraryButton.Bounds.Width, 0), this) is { } edge && edge.X <= Bounds.Width);
        } catch (Exception e) { checks.Add(new { name = "uncaught", pass = false, error = e.ToString() }); failed++; }
        finally {
            try { var stop = await client.Send(new("shutdown-host")); Check("fixture-host-clean-exit-request", stop.Ok); }
            catch (Exception e) { checks.Add(new { name = "host-cleanup", pass = false, error = e.Message }); failed++; }
            Json.Save(Path.Combine(output, "result.json"), new { passed, failed, checks, productionTouched = false, scope = "Actual Avalonia controls and native Host library IPC; empty marked fixture only" });
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime) lifetime.Shutdown(failed == 0 ? 0 : 1);
        }
    }
}

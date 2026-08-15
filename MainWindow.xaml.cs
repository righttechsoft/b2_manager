using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace B2Manager;

public sealed class FileRow
{
    public B2File Source { get; }
    public string FileName => Source.FileName;
    public string SizeDisplay => FormatSize(Source.ContentLength);
    public long Bytes => Source.ContentLength;
    public string ContentType => Source.ContentType;
    public string UploadedDisplay => FormatTimestamp(Source.UploadTimestamp);
    public int PrevVersions { get; }

    public FileRow(B2File source, int prevVersions = 0)
    {
        Source = source;
        PrevVersions = prevVersions;
    }

    internal static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{size:0} {units[unit]}" : $"{size:0.##} {units[unit]}";
    }

    private static string FormatTimestamp(long epochMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(epochMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
}

public sealed class BucketRow : INotifyPropertyChanged
{
    public B2Bucket Source { get; }
    public string BucketId => Source.BucketId;
    public string BucketName => Source.BucketName;
    public string BucketType => Source.BucketType;

    private string _sizeDisplay = "…";
    public string SizeDisplay
    {
        get => _sizeDisplay;
        set
        {
            if (_sizeDisplay == value) return;
            _sizeDisplay = value;
            OnPropertyChanged();
        }
    }

    private long? _sizeBytes;
    public long? SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (_sizeBytes == value) return;
            _sizeBytes = value;
            OnPropertyChanged();
        }
    }

    private string _filesDisplay = "…";
    public string FilesDisplay
    {
        get => _filesDisplay;
        set
        {
            if (_filesDisplay == value) return;
            _filesDisplay = value;
            OnPropertyChanged();
        }
    }

    private int? _fileCount;
    public int? FileCount
    {
        get => _fileCount;
        set
        {
            if (_fileCount == value) return;
            _fileCount = value;
            OnPropertyChanged();
        }
    }

    private string _sizeTooltip = "Not calculated yet";
    public string SizeTooltip
    {
        get => _sizeTooltip;
        set
        {
            if (_sizeTooltip == value) return;
            _sizeTooltip = value;
            OnPropertyChanged();
        }
    }

    public BucketRow(B2Bucket source) => Source = source;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class KeyRow
{
    public B2Key Source { get; }
    public string ApplicationKeyId => Source.ApplicationKeyId;
    public string KeyName => Source.KeyName;
    public string CapabilitiesDisplay => string.Join(", ", Source.Capabilities);
    public string BucketRestrictionDisplay { get; }
    public string NamePrefixDisplay => Source.NamePrefix ?? "";
    public string ExpiryDisplay => Source.ExpirationTimestamp.HasValue
        ? DateTimeOffset.FromUnixTimeMilliseconds(Source.ExpirationTimestamp.Value).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")
        : "(none)";

    public KeyRow(B2Key source, List<B2Bucket> buckets)
    {
        Source = source;
        BucketRestrictionDisplay = source.BucketIds.Count == 0
            ? "(all buckets)"
            : string.Join(", ", source.BucketIds.Select(id => buckets.FirstOrDefault(b => b.BucketId == id)?.BucketName ?? id));
    }
}

public partial class MainWindow : Window
{
    private readonly B2Client _client;
    private List<B2Bucket> _buckets = new();
    private ObservableCollection<BucketRow> _bucketRows = new();
    private readonly Dictionary<string, BucketSizeInfo> _sizeCache = SizeCache.Load();
    private CancellationTokenSource _sizingCts = new();
    private int _busyDepth;

    public MainWindow(B2Client client)
    {
        InitializeComponent();
        _client = client;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Closed(object? sender, EventArgs e) => _sizingCts.Cancel();

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "Loading buckets…");
            await RefreshBucketsAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy, string message = "Working...")
    {
        _busyDepth += busy ? 1 : -1;
        bool isBusy = _busyDepth > 0;
        BusyIndicator.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = isBusy ? "Working..." : "Ready";
        MainTabs.IsEnabled = !isBusy;

        if (busy && _busyDepth == 1)
        {
            BusyMessage.Text = message;
            BusyProgress.IsIndeterminate = true;
            BusyOverlay.Visibility = Visibility.Visible;
        }
        else if (!busy && _busyDepth == 0)
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void ReportProgress(string message, double fraction)
    {
        BusyMessage.Text = message;
        BusyProgress.IsIndeterminate = false;
        BusyProgress.Minimum = 0;
        BusyProgress.Maximum = 1;
        BusyProgress.Value = fraction;
    }

    private static void ShowError(Exception ex) =>
        MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);

    // ---- Buckets ----

    // ponytail: sizes are only re-listed when older than this; listing every version of every
    // bucket on each launch would be slow and burns billable class-C transactions.
    private static readonly TimeSpan SizeCacheTtl = TimeSpan.FromHours(24);

    private async System.Threading.Tasks.Task RefreshBucketsAsync(bool forceRecalc = false)
    {
        _sizingCts.Cancel();
        _sizingCts = new CancellationTokenSource();

        _buckets = await _client.ListBucketsAsync();

        var rows = new ObservableCollection<BucketRow>();
        foreach (var bucket in _buckets)
        {
            var row = new BucketRow(bucket);
            if (_sizeCache.TryGetValue(bucket.BucketId, out var cached))
            {
                row.SizeDisplay = FileRow.FormatSize(cached.Bytes);
                row.SizeBytes = cached.Bytes;
                row.FilesDisplay = cached.FileCount.ToString();
                row.FileCount = cached.FileCount;
                row.SizeTooltip = FormatCacheAge(cached.ComputedAtUnixMs);
            }
            rows.Add(row);
        }
        _bucketRows = rows;
        BucketsGrid.ItemsSource = _bucketRows;

        var selectedBucket = FilesBucketCombo.SelectedItem as B2Bucket;
        FilesBucketCombo.ItemsSource = _buckets;
        if (selectedBucket != null)
        {
            var match = _buckets.FirstOrDefault(b => b.BucketId == selectedBucket.BucketId);
            FilesBucketCombo.SelectedItem = match;
        }

        _ = RunBackgroundSizingAsync(_buckets, rows, forceRecalc, _sizingCts.Token);
    }

    private static string FormatCacheAge(long computedAtUnixMs) =>
        "Calculated " + DateTimeOffset.FromUnixTimeMilliseconds(computedAtUnixMs).LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    private async System.Threading.Tasks.Task<bool> ComputeBucketSizeAsync(B2Bucket bucket, BucketRow row, CancellationToken token)
    {
        try
        {
            var versions = await _client.ListFileVersionsAsync(bucket.BucketId);
            if (token.IsCancellationRequested) return false;

            long bytes = versions.Sum(v => v.ContentLength);
            // Count distinct visible files, matching what the Files tab lists — not raw version rows.
            int fileCount = versions
                .GroupBy(v => v.FileName)
                .Count(g => g.OrderByDescending(v => v.UploadTimestamp).First().Action == "upload");
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            _sizeCache[bucket.BucketId] = new BucketSizeInfo
            {
                Bytes = bytes,
                FileCount = fileCount,
                VersionCount = versions.Count,
                ComputedAtUnixMs = now
            };

            row.SizeDisplay = FileRow.FormatSize(bytes);
            row.SizeBytes = bytes;
            row.FilesDisplay = fileCount.ToString();
            row.FileCount = fileCount;
            row.SizeTooltip = FormatCacheAge(now);
            return true;
        }
        catch
        {
            if (!token.IsCancellationRequested)
            {
                row.SizeDisplay = "?";
                row.SizeBytes = null;
                row.FilesDisplay = "?";
                row.FileCount = null;
                row.SizeTooltip = "Size could not be calculated";
            }
            return false;
        }
    }

    private async System.Threading.Tasks.Task RunBackgroundSizingAsync(List<B2Bucket> buckets, ObservableCollection<BucketRow> rows, bool forceRecalc, CancellationToken token)
    {
        long staleBefore = DateTimeOffset.UtcNow.Subtract(SizeCacheTtl).ToUnixTimeMilliseconds();
        bool changed = false;
        for (int i = 0; i < buckets.Count; i++)
        {
            if (token.IsCancellationRequested) return;
            if (!forceRecalc
                && _sizeCache.TryGetValue(buckets[i].BucketId, out var cached)
                && cached.ComputedAtUnixMs >= staleBefore)
                continue;
            if (await ComputeBucketSizeAsync(buckets[i], rows[i], token))
                changed = true;
        }
        if (changed && !token.IsCancellationRequested)
            SizeCache.Save(_sizeCache);
    }

    private void RecomputeBucketSizeInBackground(B2Bucket bucket)
    {
        var row = _bucketRows.FirstOrDefault(r => r.BucketId == bucket.BucketId);
        if (row == null) return;
        _ = RecomputeSingleBucketAsync(bucket, row, _sizingCts.Token);
    }

    private async System.Threading.Tasks.Task RecomputeSingleBucketAsync(B2Bucket bucket, BucketRow row, CancellationToken token)
    {
        if (await ComputeBucketSizeAsync(bucket, row, token))
            SizeCache.Save(_sizeCache);
    }

    private void BucketsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (BucketsGrid.SelectedItem is not BucketRow row) return;
        var bucket = _buckets.FirstOrDefault(b => b.BucketId == row.BucketId);
        if (bucket == null) return;

        FilesBucketCombo.SelectedItem = bucket;
        MainTabs.SelectedItem = FilesTabItem;
    }

    private async void BucketsRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try { SetBusy(true, "Loading buckets…"); await RefreshBucketsAsync(); }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async void BucketsRecalcButton_Click(object sender, RoutedEventArgs e)
    {
        try { SetBusy(true, "Recalculating bucket sizes…"); await RefreshBucketsAsync(forceRecalc: true); }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async void BucketsCreateButton_Click(object sender, RoutedEventArgs e)
    {
        bool busy = false;
        try
        {
            var fields = new List<FieldSpec>
            {
                new("name", "Bucket name", FieldType.Text),
                new("type", "Bucket type", FieldType.Combo) { ComboItems = new List<string> { "allPrivate", "allPublic" }, Default = "allPrivate" }
            };
            var dialog = new FormDialog("Create Bucket", fields, "Create") { Owner = this };
            if (dialog.ShowDialog() != true) return;

            string name = dialog.Values["name"].Trim();
            string type = dialog.Values["type"];
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("Bucket name is required.", "Create Bucket", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            busy = true;
            SetBusy(true, "Creating bucket…");
            await _client.CreateBucketAsync(name, type);
            await RefreshBucketsAsync();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { if (busy) SetBusy(false); }
    }

    private async void BucketsEditButton_Click(object sender, RoutedEventArgs e)
    {
        bool busy = false;
        try
        {
            if (BucketsGrid.SelectedItem is not BucketRow selectedRow)
            {
                MessageBox.Show("Select a bucket first.", "Edit Bucket", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var bucket = selectedRow.Source;

            var firstRule = bucket.LifecycleRules.Count > 0 ? bucket.LifecycleRules[0] : null;

            var fields = new List<FieldSpec>
            {
                new("type", "Bucket type", FieldType.Combo) { ComboItems = new List<string> { "allPrivate", "allPublic" }, Default = bucket.BucketType },
                new("prefix", "Lifecycle: file name prefix (blank = all files)", FieldType.Text) { Default = firstRule?.FileNamePrefix ?? "" },
                new("daysToHide", "Lifecycle: days from uploading to hiding (blank = never)", FieldType.Text) { Default = firstRule?.DaysFromUploadingToHiding?.ToString() ?? "" },
                new("daysToDelete", "Lifecycle: days from hiding to deleting (blank = never)", FieldType.Text) { Default = firstRule?.DaysFromHidingToDeleting?.ToString() ?? "" }
            };

            string title = $"Edit Bucket - {bucket.BucketName}";
            if (bucket.LifecycleRules.Count > 1)
                title += $"  ({bucket.LifecycleRules.Count - 1} additional lifecycle rule(s) preserved)";

            var dialog = new FormDialog(title, fields, "Save") { Owner = this };
            if (dialog.ShowDialog() != true) return;

            string prefix = dialog.Values["prefix"].Trim();
            string daysToHideText = dialog.Values["daysToHide"].Trim();
            string daysToDeleteText = dialog.Values["daysToDelete"].Trim();

            int? daysToHide = null;
            if (!string.IsNullOrEmpty(daysToHideText))
            {
                if (!int.TryParse(daysToHideText, out int d) || d <= 0)
                {
                    MessageBox.Show("Days from uploading to hiding must be blank or a positive whole number.", "Edit Bucket", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                daysToHide = d;
            }

            int? daysToDelete = null;
            if (!string.IsNullOrEmpty(daysToDeleteText))
            {
                if (!int.TryParse(daysToDeleteText, out int d) || d <= 0)
                {
                    MessageBox.Show("Days from hiding to deleting must be blank or a positive whole number.", "Edit Bucket", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                daysToDelete = d;
            }

            var newRules = new List<B2LifecycleRule>(bucket.LifecycleRules);
            bool ruleRemoved = string.IsNullOrEmpty(prefix) && daysToHide == null && daysToDelete == null;
            if (ruleRemoved)
            {
                if (newRules.Count > 0)
                    newRules.RemoveAt(0);
            }
            else
            {
                var newFirstRule = new B2LifecycleRule
                {
                    FileNamePrefix = prefix,
                    DaysFromUploadingToHiding = daysToHide,
                    DaysFromHidingToDeleting = daysToDelete
                };
                if (newRules.Count > 0)
                    newRules[0] = newFirstRule;
                else
                    newRules.Insert(0, newFirstRule);
            }

            busy = true;
            SetBusy(true, "Saving bucket settings…");
            await _client.UpdateBucketAsync(bucket.BucketId, dialog.Values["type"], newRules);
            await RefreshBucketsAsync();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { if (busy) SetBusy(false); }
    }

    private async void BucketsDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        bool busy = false;
        try
        {
            if (BucketsGrid.SelectedItem is not BucketRow selectedRow)
            {
                MessageBox.Show("Select a bucket first.", "Delete Bucket", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var bucket = selectedRow.Source;

            var result = MessageBox.Show(
                $"Delete bucket '{bucket.BucketName}'? This cannot be undone.",
                "Delete Bucket", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            busy = true;
            SetBusy(true, "Deleting bucket…");
            await _client.DeleteBucketAsync(bucket.BucketId);
            await RefreshBucketsAsync();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { if (busy) SetBusy(false); }
    }

    // ---- Files ----

    private async void FilesBucketCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        try
        {
            SetBusy(true, "Loading files…");
            await RefreshFilesAsync();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async System.Threading.Tasks.Task RefreshFilesAsync()
    {
        if (FilesBucketCombo.SelectedItem is not B2Bucket bucket)
        {
            FilesGrid.ItemsSource = null;
            return;
        }

        var versions = await _client.ListFileVersionsAsync(bucket.BucketId);
        FilesGrid.ItemsSource = versions
            .GroupBy(v => v.FileName)
            .Select(g => g.OrderByDescending(v => v.UploadTimestamp).ToList())
            .Where(entries => entries[0].Action == "upload")
            .Select(entries => new FileRow(entries[0], entries.Count - 1))
            .ToList();
    }

    private async void FilesRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        bool busy = false;
        try
        {
            if (FilesBucketCombo.SelectedItem is not B2Bucket)
            {
                MessageBox.Show("Select a bucket first.", "Files", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            busy = true;
            SetBusy(true, "Loading files…");
            await RefreshFilesAsync();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { if (busy) SetBusy(false); }
    }

    private async void FilesUploadButton_Click(object sender, RoutedEventArgs e)
    {
        bool busy = false;
        try
        {
            if (FilesBucketCombo.SelectedItem is not B2Bucket bucket)
            {
                MessageBox.Show("Select a bucket first.", "Upload", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var openDialog = new OpenFileDialog();
            if (openDialog.ShowDialog(this) != true) return;

            string localPath = openDialog.FileName;
            string defaultName = System.IO.Path.GetFileName(localPath);

            var fields = new List<FieldSpec>
            {
                new("remoteName", "Remote file name", FieldType.Text) { Default = defaultName }
            };
            var nameDialog = new FormDialog("Upload File", fields, "Upload") { Owner = this };
            if (nameDialog.ShowDialog() != true) return;

            string remoteName = nameDialog.Values["remoteName"].Trim();
            if (string.IsNullOrEmpty(remoteName))
            {
                MessageBox.Show("Remote file name is required.", "Upload", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            busy = true;
            SetBusy(true, $"Uploading {remoteName}…");

            long total = new System.IO.FileInfo(localPath).Length;
            var progress = new Progress<long>(done =>
            {
                double fraction = total > 0 ? (double)done / total : 0;
                ReportProgress($"Uploading {remoteName} — {FileRow.FormatSize(done)} of {FileRow.FormatSize(total)}", fraction);
            });

            await _client.UploadFileAsync(bucket.BucketId, localPath, remoteName, progress);
            await RefreshFilesAsync();
            RecomputeBucketSizeInBackground(bucket);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { if (busy) SetBusy(false); }
    }

    private async void FilesDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        bool busy = false;
        try
        {
            if (FilesBucketCombo.SelectedItem is not B2Bucket bucket)
            {
                MessageBox.Show("Select a bucket first.", "Download", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (FilesGrid.SelectedItem is not FileRow row)
            {
                MessageBox.Show("Select a file first.", "Download", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (FilesGrid.SelectedItems.Count > 1)
            {
                MessageBox.Show("Select a single file to download.", "Download", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var saveDialog = new SaveFileDialog
            {
                FileName = System.IO.Path.GetFileName(row.FileName)
            };
            if (saveDialog.ShowDialog(this) != true) return;

            busy = true;
            SetBusy(true, $"Downloading {row.FileName}…");

            long total = row.Bytes;
            var progress = new Progress<long>(done =>
            {
                if (total > 0)
                    ReportProgress($"Downloading {row.FileName} — {FileRow.FormatSize(done)} of {FileRow.FormatSize(total)}", (double)done / total);
                else
                    BusyMessage.Text = $"Downloading {row.FileName} — {FileRow.FormatSize(done)}";
            });

            await _client.DownloadFileAsync(bucket.BucketName, row.FileName, saveDialog.FileName, progress, knownSize: row.Bytes);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { if (busy) SetBusy(false); }
    }

    private async void FilesDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        bool busy = false;
        try
        {
            if (FilesBucketCombo.SelectedItem is not B2Bucket bucket)
            {
                MessageBox.Show("Select a bucket first.", "Delete File", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // Snapshot the selection: refreshing the grid below invalidates SelectedItems.
            var rows = FilesGrid.SelectedItems.Cast<FileRow>().ToList();
            if (rows.Count == 0)
            {
                MessageBox.Show("Select one or more files first.", "Delete Files", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string what = rows.Count == 1
                ? $"file '{rows[0].FileName}'"
                : $"{rows.Count} files";
            bool deleteAllVersions = false;

            if (rows.Any(r => r.PrevVersions > 0))
            {
                var fields = new List<FieldSpec>
                {
                    new("scope", $"Delete {what}? This permanently deletes the selected version(s) and cannot be undone.", FieldType.Combo)
                    {
                        ComboItems = new List<string> { "Most recent version only", "All versions" },
                        Default = "Most recent version only"
                    }
                };
                var scopeDialog = new FormDialog("Delete Files", fields, "Delete") { Owner = this };
                if (scopeDialog.ShowDialog() != true) return;
                deleteAllVersions = scopeDialog.Values["scope"] == "All versions";
            }
            else
            {
                var result = MessageBox.Show(
                    $"Delete {what}? This permanently deletes the current version (unlike hiding, it cannot be undone).",
                    "Delete Files", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;
            }

            busy = true;
            SetBusy(true, "Deleting files…");

            var toDelete = new List<(string FileName, string FileId)>();
            if (deleteAllVersions)
            {
                foreach (var row in rows)
                {
                    var versions = await _client.ListFileVersionsAsync(bucket.BucketId, row.FileName);
                    toDelete.AddRange(versions.Where(v => v.FileName == row.FileName).Select(v => (v.FileName, v.FileId)));
                }
            }
            else
            {
                toDelete.AddRange(rows.Select(r => (r.FileName, r.Source.FileId)));
            }

            // Keep going after a failure so one bad file doesn't strand the rest half-deleted.
            var failures = new List<string>();
            for (int i = 0; i < toDelete.Count; i++)
            {
                var item = toDelete[i];
                ReportProgress($"Deleting {i + 1} of {toDelete.Count}…", (double)(i + 1) / toDelete.Count);
                try { await _client.DeleteFileVersionAsync(item.FileName, item.FileId); }
                catch (Exception ex) { failures.Add($"{item.FileName}: {ex.Message}"); }
            }

            await RefreshFilesAsync();
            RecomputeBucketSizeInBackground(bucket);

            if (failures.Count > 0)
                MessageBox.Show(
                    $"Deleted {toDelete.Count - failures.Count} of {toDelete.Count} files. Failed:\n\n" + string.Join("\n", failures.Take(10)),
                    "Delete Files", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { if (busy) SetBusy(false); }
    }

    private async void FilesVersionsButton_Click(object sender, RoutedEventArgs e)
    {
        try { await OpenVersionsWindowAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void FilesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FilesGrid.SelectedItem is not FileRow) return;
        try { await OpenVersionsWindowAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async System.Threading.Tasks.Task OpenVersionsWindowAsync()
    {
        if (FilesBucketCombo.SelectedItem is not B2Bucket bucket)
        {
            MessageBox.Show("Select a bucket first.", "Versions", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (FilesGrid.SelectedItems.Count > 1)
        {
            MessageBox.Show("Select a single file.", "Versions", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (FilesGrid.SelectedItem is not FileRow row)
        {
            MessageBox.Show("Select a file first.", "Versions", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Shown with the main window not busy: it's a modal dialog waiting on the user, not a long-running operation.
        var dialog = new VersionsWindow(_client, bucket, row.FileName) { Owner = this };
        dialog.ShowDialog();

        if (dialog.AnyDeleted)
        {
            try
            {
                SetBusy(true, "Loading files…");
                await RefreshFilesAsync();
            }
            finally { SetBusy(false); }
            RecomputeBucketSizeInBackground(bucket);
        }
    }

    // ---- Keys ----

    private async System.Threading.Tasks.Task RefreshKeysAsync()
    {
        var keys = await _client.ListKeysAsync();
        KeysGrid.ItemsSource = keys.Select(k => new KeyRow(k, _buckets)).ToList();
    }

    private async void KeysRefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try { SetBusy(true, "Loading keys…"); await RefreshKeysAsync(); }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async void KeysCreateButton_Click(object sender, RoutedEventArgs e)
    {
        bool busy = false;
        try
        {
            var bucketNames = new List<string> { "(none)" };
            bucketNames.AddRange(_buckets.Select(b => b.BucketName));

            var fields = new List<FieldSpec>
            {
                new("name", "Key name", FieldType.Text),
                new("capabilities", "Capabilities (comma-separated)", FieldType.Text)
                    { Default = "listBuckets,listFiles,readFiles,writeFiles,deleteFiles" },
                new("bucket", "Restrict to bucket", FieldType.Combo) { ComboItems = bucketNames, Default = "(none)" },
                new("expiryDays", "Expires after (days, blank = never)", FieldType.Text)
            };
            var dialog = new FormDialog("Create Application Key", fields, "Create") { Owner = this };
            if (dialog.ShowDialog() != true) return;

            string name = dialog.Values["name"].Trim();
            if (string.IsNullOrEmpty(name))
            {
                MessageBox.Show("Key name is required.", "Create Application Key", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var capabilities = dialog.Values["capabilities"]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            string bucketSelection = dialog.Values["bucket"];
            string? bucketId = bucketSelection == "(none)" || string.IsNullOrEmpty(bucketSelection)
                ? null
                : _buckets.FirstOrDefault(b => b.BucketName == bucketSelection)?.BucketId;

            long? validDurationSeconds = null;
            string expiryText = dialog.Values["expiryDays"].Trim();
            if (!string.IsNullOrEmpty(expiryText))
            {
                if (!int.TryParse(expiryText, out int days) || days <= 0)
                {
                    MessageBox.Show("Expiry days must be a positive whole number.", "Create Application Key", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                validDurationSeconds = days * 86400L;
            }

            busy = true;
            SetBusy(true, "Creating key…");
            var created = await _client.CreateKeyAsync(name, capabilities, bucketId, validDurationSeconds);
            SetBusy(false);
            busy = false;

            // Shown with the main window not busy: the overlay must not sit behind the one-time secret dialog.
            var secretFields = new List<FieldSpec>
            {
                new("secret", "Application Key (shown once - save it now)", FieldType.ReadOnly) { Default = created.ApplicationKey }
            };
            var secretDialog = new FormDialog("Application Key Created", secretFields, "Close") { Owner = this };
            secretDialog.ShowDialog();

            busy = true;
            SetBusy(true, "Loading keys…");
            await RefreshKeysAsync();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { if (busy) SetBusy(false); }
    }

    private async void KeysDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        bool busy = false;
        try
        {
            if (KeysGrid.SelectedItem is not KeyRow row)
            {
                MessageBox.Show("Select a key first.", "Delete Key", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string message = row.ApplicationKeyId == _client.CurrentKeyId
                ? $"'{row.KeyName}' is the key currently signed in. Deleting it will prevent future logins with these stored credentials. Continue?"
                : $"Delete key '{row.KeyName}'?";

            var result = MessageBox.Show(message, "Delete Key", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            busy = true;
            SetBusy(true, "Deleting key…");
            await _client.DeleteKeyAsync(row.ApplicationKeyId);
            await RefreshKeysAsync();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { if (busy) SetBusy(false); }
    }
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace B2Manager;

public enum FieldType { Text, Password, Combo, ReadOnly }

public sealed class FieldSpec
{
    public string Key { get; }
    public string Label { get; }
    public FieldType Type { get; }
    public string Default { get; init; } = "";
    public List<string>? ComboItems { get; init; }

    public FieldSpec(string key, string label, FieldType type)
    {
        Key = key;
        Label = label;
        Type = type;
    }
}

/// <summary>Reusable code-built modal dialog: label+field rows, OK/Cancel, exposes Values.</summary>
public sealed class FormDialog : Window
{
    private readonly List<FieldSpec> _fields;
    private readonly Dictionary<string, Control> _controls = new();

    public Dictionary<string, string> Values { get; } = new();

    public FormDialog(string title, IEnumerable<FieldSpec> fields, string okText = "OK")
    {
        _fields = new List<FieldSpec>(fields);

        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        Width = 420;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var root = new Grid { Margin = new Thickness(16) };
        root.ColumnDefinitions.Add(new ColumnDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var fieldsPanel = new StackPanel();
        Grid.SetRow(fieldsPanel, 0);
        root.Children.Add(fieldsPanel);

        foreach (var field in _fields)
        {
            var row = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 10) };

            var label = new TextBlock { Text = field.Label, Margin = new Thickness(0, 0, 0, 4) };
            row.Children.Add(label);

            Control control;
            switch (field.Type)
            {
                case FieldType.Password:
                    var pwd = new PasswordBox { Password = field.Default };
                    control = pwd;
                    break;

                case FieldType.Combo:
                    var combo = new ComboBox { IsEditable = false };
                    if (field.ComboItems != null)
                        foreach (var item in field.ComboItems)
                            combo.Items.Add(item);
                    if (!string.IsNullOrEmpty(field.Default) && combo.Items.Contains(field.Default))
                        combo.SelectedItem = field.Default;
                    else if (combo.Items.Count > 0)
                        combo.SelectedIndex = 0;
                    control = combo;
                    break;

                case FieldType.ReadOnly:
                    var roPanel = new DockPanel();
                    var roBox = new TextBox
                    {
                        Text = field.Default,
                        IsReadOnly = true,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    var copyBtn = new Button { Content = "Copy", Width = 60, Margin = new Thickness(6, 0, 0, 0) };
                    copyBtn.Click += (_, _) =>
                    {
                        Clipboard.SetText(roBox.Text);
                    };
                    DockPanel.SetDock(copyBtn, Dock.Right);
                    roPanel.Children.Add(copyBtn);
                    roPanel.Children.Add(roBox);
                    row.Children.Add(roPanel);
                    _controls[field.Key] = roBox;
                    fieldsPanel.Children.Add(row);
                    continue;

                case FieldType.Text:
                default:
                    control = new TextBox { Text = field.Default };
                    break;
            }

            row.Children.Add(control);
            _controls[field.Key] = control;
            fieldsPanel.Children.Add(row);
        }

        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(buttonPanel, 1);

        var okButton = new Button { Content = okText, Width = 80, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        okButton.Click += (_, _) =>
        {
            CollectValues();
            DialogResult = true;
        };

        var cancelButton = new Button { Content = "Cancel", Width = 80, IsCancel = true };

        buttonPanel.Children.Add(okButton);
        buttonPanel.Children.Add(cancelButton);
        root.Children.Add(buttonPanel);

        Content = root;
    }

    private void CollectValues()
    {
        foreach (var field in _fields)
        {
            var control = _controls[field.Key];
            string value = control switch
            {
                PasswordBox pwd => pwd.Password,
                ComboBox combo => combo.SelectedItem as string ?? "",
                TextBox tb => tb.Text,
                _ => ""
            };
            Values[field.Key] = value;
        }
    }
}

/// <summary>Code-only login window: first-run credential entry, or password-only prompt on subsequent runs.</summary>
public sealed class LoginWindow : Window
{
    private bool _firstRun;
    private bool _busy;

    private TextBox? _keyIdBox;
    private PasswordBox? _appKeyBox;
    private PasswordBox? _passwordBox;
    private PasswordBox? _confirmBox;
    private TextBlock _statusText = new();
    private Button _submitButton = new();
    private Button _resetButton = new();

    public B2Client? AuthorizedClient { get; private set; }

    public LoginWindow()
    {
        _firstRun = !CredentialStore.Exists();

        Title = "B2 Manager - Login";
        SizeToContent = SizeToContent.WidthAndHeight;
        Width = 380;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Build();
    }

    private void Build()
    {
        var root = new StackPanel { Margin = new Thickness(16) };

        if (_firstRun)
        {
            root.Children.Add(new TextBlock { Text = "Enter your Backblaze B2 master application key.", Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap });

            root.Children.Add(new TextBlock { Text = "Key ID", Margin = new Thickness(0, 0, 0, 4) });
            _keyIdBox = new TextBox();
            root.Children.Add(_keyIdBox);

            root.Children.Add(new TextBlock { Text = "Application Key", Margin = new Thickness(0, 10, 0, 4) });
            _appKeyBox = new PasswordBox();
            root.Children.Add(_appKeyBox);

            root.Children.Add(new TextBlock { Text = "Password (to encrypt locally)", Margin = new Thickness(0, 10, 0, 4) });
            _passwordBox = new PasswordBox();
            root.Children.Add(_passwordBox);

            root.Children.Add(new TextBlock { Text = "Confirm Password", Margin = new Thickness(0, 10, 0, 4) });
            _confirmBox = new PasswordBox();
            root.Children.Add(_confirmBox);

            _submitButton = new Button { Content = "Save && Login", IsDefault = true, Margin = new Thickness(0, 14, 0, 0) };
        }
        else
        {
            root.Children.Add(new TextBlock { Text = "Enter your password to unlock stored credentials.", Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap });

            root.Children.Add(new TextBlock { Text = "Password", Margin = new Thickness(0, 0, 0, 4) });
            _passwordBox = new PasswordBox();
            root.Children.Add(_passwordBox);

            _submitButton = new Button { Content = "Login", IsDefault = true, Margin = new Thickness(0, 14, 0, 0) };
        }

        _statusText = new TextBlock
        {
            Foreground = Brushes.Red,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed
        };
        root.Children.Add(_statusText);

        _submitButton.Click += SubmitButton_Click;
        root.Children.Add(_submitButton);

        var bottomRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };

        if (!_firstRun)
        {
            _resetButton = new Button { Content = "Reset credentials" };
            _resetButton.Click += ResetButton_Click;
            bottomRow.Children.Add(_resetButton);
        }

        var exitButton = new Button { Content = "Exit", Margin = new Thickness(8, 0, 0, 0) };
        exitButton.Click += (_, _) => { DialogResult = false; };
        bottomRow.Children.Add(exitButton);

        root.Children.Add(bottomRow);

        Content = root;
    }

    private void SetStatus(string message)
    {
        _statusText.Text = message;
        _statusText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _submitButton.IsEnabled = !busy;
        if (!_firstRun)
            _resetButton.IsEnabled = !busy;
    }

    private async void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        try
        {
            SetBusy(true);
            SetStatus("");

            if (_firstRun)
            {
                await HandleFirstRunSubmitAsync();
            }
            else
            {
                await HandleLoginSubmitAsync();
            }
        }
        catch (CryptographicException)
        {
            SetStatus("Incorrect password. Please try again.");
        }
        catch (B2ApiException ex)
        {
            SetStatus($"Backblaze error: {ex.Message}");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async System.Threading.Tasks.Task HandleFirstRunSubmitAsync()
    {
        string keyId = _keyIdBox!.Text.Trim();
        string appKey = _appKeyBox!.Password;
        string password = _passwordBox!.Password;
        string confirm = _confirmBox!.Password;

        if (string.IsNullOrEmpty(keyId) || string.IsNullOrEmpty(appKey))
        {
            SetStatus("Key ID and Application Key are required.");
            return;
        }

        if (string.IsNullOrEmpty(password) || password != confirm)
        {
            SetStatus("Passwords do not match.");
            return;
        }

        var client = new B2Client();
        await client.AuthorizeAsync(keyId, appKey);

        CredentialStore.Save(new Credentials { KeyId = keyId, ApplicationKey = appKey }, password);

        AuthorizedClient = client;
        DialogResult = true;
    }

    private async System.Threading.Tasks.Task HandleLoginSubmitAsync()
    {
        string password = _passwordBox!.Password;

        var credentials = CredentialStore.Load(password);

        var client = new B2Client();
        await client.AuthorizeAsync(credentials.KeyId, credentials.ApplicationKey);

        AuthorizedClient = client;
        DialogResult = true;
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "This deletes your saved credentials. You will need to re-enter your application key.",
            "Reset credentials",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.OK)
            return;

        CredentialStore.Reset();

        // Rebuild this window in place as a first-run dialog.
        _firstRun = true;
        Content = null;
        Build();
    }
}

internal sealed class VersionRow
{
    public B2File Source { get; }
    public string UploadedDisplay => DateTimeOffset.FromUnixTimeMilliseconds(Source.UploadTimestamp).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
    public long UploadTimestamp => Source.UploadTimestamp;
    public string SizeDisplay => FileRow.FormatSize(Source.ContentLength);
    public long Bytes => Source.ContentLength;
    public string Action => Source.Action;
    public string FileId => Source.FileId;

    public VersionRow(B2File source) => Source = source;
}

/// <summary>Code-only window listing every version of one file, sortable by date/size, with delete support.</summary>
public sealed class VersionsWindow : Window
{
    private readonly B2Client _client;
    private readonly B2Bucket _bucket;
    private readonly string _fileName;

    private readonly TextBlock _infoText = new() { Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
    private readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        SelectionMode = DataGridSelectionMode.Extended,
        SelectionUnit = DataGridSelectionUnit.FullRow,
        CanUserSortColumns = true
    };
    private readonly DataGridTextColumn _uploadedColumn;
    private readonly Button _deleteButton = new() { Content = "Delete Selected", Width = 120, Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _closeButton = new() { Content = "Close", Width = 80 };
    private readonly TextBlock _busyMessage = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ProgressBar _busyProgress = new() { Width = 160, Height = 16, VerticalAlignment = VerticalAlignment.Center, IsIndeterminate = true };
    private readonly StackPanel _busyPanel;

    public bool AnyDeleted { get; private set; }

    public VersionsWindow(B2Client client, B2Bucket bucket, string fileName)
    {
        _client = client;
        _bucket = bucket;
        _fileName = fileName;

        Title = $"Versions - {fileName}";
        Width = 700;
        Height = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _uploadedColumn = new DataGridTextColumn { Header = "Uploaded", Binding = new Binding("UploadedDisplay"), SortMemberPath = "UploadTimestamp", Width = 150 };
        _grid.Columns.Add(_uploadedColumn);
        _grid.Columns.Add(new DataGridTextColumn { Header = "Size", Binding = new Binding("SizeDisplay"), SortMemberPath = "Bytes", Width = 90 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Action", Binding = new Binding("Action"), SortMemberPath = "Action", Width = 70 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "File ID", Binding = new Binding("FileId"), SortMemberPath = "FileId", Width = new DataGridLength(1, DataGridLengthUnitType.Star) });

        var root = new DockPanel { Margin = new Thickness(10) };

        DockPanel.SetDock(_infoText, Dock.Top);
        root.Children.Add(_infoText);

        var buttonPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        _deleteButton.Click += DeleteButton_Click;
        _closeButton.Click += (_, _) => Close();
        buttonPanel.Children.Add(_deleteButton);
        buttonPanel.Children.Add(_closeButton);
        DockPanel.SetDock(buttonPanel, Dock.Bottom);
        root.Children.Add(buttonPanel);

        _busyPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };
        _busyPanel.Children.Add(_busyMessage);
        _busyPanel.Children.Add(_busyProgress);
        DockPanel.SetDock(_busyPanel, Dock.Bottom);
        root.Children.Add(_busyPanel);

        root.Children.Add(_grid);

        Content = root;

        Loaded += VersionsWindow_Loaded;
    }

    private void SetBusy(bool busy, string message = "Working...")
    {
        _deleteButton.IsEnabled = !busy;
        _closeButton.IsEnabled = !busy;
        _busyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            _busyMessage.Text = message;
            _busyProgress.IsIndeterminate = true;
        }
    }

    private void ReportProgress(string message, double fraction)
    {
        _busyMessage.Text = message;
        _busyProgress.IsIndeterminate = false;
        _busyProgress.Minimum = 0;
        _busyProgress.Maximum = 1;
        _busyProgress.Value = fraction;
    }

    private async void VersionsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true, "Loading versions…");
            await RefreshVersionsAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async System.Threading.Tasks.Task RefreshVersionsAsync()
    {
        // Keep whatever sort the user picked — they may be working down a size-sorted list.
        var sort = _grid.Items.SortDescriptions.Count > 0
            ? _grid.Items.SortDescriptions[0]
            : new SortDescription(_uploadedColumn.SortMemberPath, ListSortDirection.Descending);

        var versions = await _client.ListFileVersionsAsync(_bucket.BucketId, _fileName);
        var exact = versions.Where(v => v.FileName == _fileName).ToList();

        _grid.ItemsSource = exact.Select(v => new VersionRow(v)).ToList();

        _grid.Items.SortDescriptions.Clear();
        _grid.Items.SortDescriptions.Add(sort);
        foreach (var col in _grid.Columns)
            col.SortDirection = col.SortMemberPath == sort.PropertyName ? sort.Direction : null;

        long totalBytes = exact.Sum(v => v.ContentLength);
        _infoText.Text = $"{_fileName}  -  {exact.Count} version(s), {FileRow.FormatSize(totalBytes)} total";
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Snapshot the selection: refreshing the grid below invalidates SelectedItems.
            var rows = _grid.SelectedItems.Cast<VersionRow>().ToList();
            if (rows.Count == 0)
            {
                MessageBox.Show("Select one or more versions first.", "Delete Versions", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show(
                $"Delete {rows.Count} version(s) of '{_fileName}'? This permanently deletes them (unlike hiding, it cannot be undone).",
                "Delete Versions", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            SetBusy(true, "Deleting versions…");

            // Keep going after a failure so one bad version doesn't strand the rest half-deleted.
            var failures = new List<string>();
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                ReportProgress($"Deleting {i + 1} of {rows.Count}…", (double)(i + 1) / rows.Count);
                try
                {
                    await _client.DeleteFileVersionAsync(_fileName, row.FileId);
                    AnyDeleted = true;
                }
                catch (Exception ex) { failures.Add($"{row.UploadedDisplay}: {ex.Message}"); }
            }

            await RefreshVersionsAsync();

            if (failures.Count > 0)
                MessageBox.Show(
                    $"Deleted {rows.Count - failures.Count} of {rows.Count} versions. Failed:\n\n" + string.Join("\n", failures.Take(10)),
                    "Delete Versions", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }
}

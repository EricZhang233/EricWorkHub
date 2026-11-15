using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace EricWorkHub;

public sealed partial class MainWindow : Window
{
    public ObservableCollection<OfficeAppItem> OfficeApps { get; } = new();
    public ObservableCollection<RecentItemViewModel> RecentItems { get; } = new();
    private readonly DispatcherQueueTimer _timer;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HubService _hub = new();
    private readonly OperationService _operations = new();
    private readonly OfficeManagerService _officeManager = new();
    private readonly IconService _icons = new();
    private readonly ForegroundRefreshPolicy _refresh = new();
    private bool _closed, _artCraftSelected, _officeManagerBusy, _applicationsLoaded;
    private XamlRoot? _titleBarXamlRoot;
    private IntPtr _windowIcon;
    private WindowStateTracker? _windowState;

    public MainWindow()
    {
        InitializeComponent();
        DocumentPanel.ResizeRequested += DocumentPanel_ResizeRequested;
        InitializeTitleBar();
        LoadWindowState();
        AppWindow.Changed += WindowChanged;
        Activated += OnWindowActivated;
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = ForegroundRefreshPolicy.Interval;
        _timer.Tick += OnTimer;

        ApplyApps(_hub.Snapshot.Apps, false);
        ApplyApplicationTab();
    }

    private void InitializeTitleBar()
    {
        if (Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicController.IsSupported()) SystemBackdrop = new DesktopAcrylicBackdrop();
        ExtendsContentIntoTitleBar = true;
        Title = "EricWorkHub";
        TitleBarText.Text = Title;
        SetTitleBar(TitleBarDragRegion);
        TitleBarGrid.Loaded += TitleBarGrid_Loaded;
        TitleBarGrid.SizeChanged += TitleBarGrid_SizeChanged;
        VersionText.Text = AppVersion.DisplayVersion;
        AppWindow.TitleBar.ButtonBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Windows.UI.Color.FromArgb(0, 0, 0, 0);
        try
        {
            _windowIcon = AppIconService.GetDefaultIconHandle();
            if (_windowIcon != IntPtr.Zero)
            {
                IntPtr window = WindowNative.GetWindowHandle(this);
                SendMessage(window, 0x0080, (IntPtr)1, _windowIcon);
                SendMessage(window, 0x0080, IntPtr.Zero, _windowIcon);
            }
            TitleBarIcon.Source = AppIconService.GetBitmapImage();
        }
        catch (Exception ex) { LogService.Write("Window", "Window icon unavailable", ex, level: LogService.LogLevel.Warn); }
    }

    private void TitleBarGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_titleBarXamlRoot != null) _titleBarXamlRoot.Changed -= TitleBarXamlRoot_Changed;
        _titleBarXamlRoot = TitleBarGrid.XamlRoot;
        if (_titleBarXamlRoot != null) _titleBarXamlRoot.Changed += TitleBarXamlRoot_Changed;
        UpdateTitleBarInsets();
    }

    private void TitleBarGrid_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleBarInsets();
    private void TitleBarXamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateTitleBarInsets();
    private void UpdateTitleBarInsets()
    {
        if (TitleBarGrid.XamlRoot == null || _closed) return;
        try
        {
            double scale = TitleBarGrid.XamlRoot.RasterizationScale;
            if (scale <= 0) return;
            double right = AppWindow.TitleBar.RightInset / scale + 12;
            if (Math.Abs(TitleBarActionsPanel.Margin.Right - right) < 0.1) return;
            TitleBarActionsPanel.Margin = new Thickness(0, 0, right, 0);
            LogService.Write("UI", $"Title bar caption inset updated right={right:F1} scale={scale:F2}");
        }
        catch (Exception ex) { LogService.Write("UI", "Title bar caption inset update failed", ex, level: LogService.LogLevel.Warn); }
    }

    private void MenuAuthorIcon_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image) image.Source = AppIconService.GetBitmapImage();
    }
    private void MenuVersionText_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock text) text.Text = AppVersion.DisplayVersion;
    }
    private void AuthorText_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock text) text.Text = Text.T("Author");
    }
    private async void ProjectLink_Click(object sender, RoutedEventArgs e) => RenderOperation(await _operations.OpenProjectAsync(false));
    private async void ReleasesLink_Click(object sender, RoutedEventArgs e) => RenderOperation(await _operations.OpenProjectAsync(true));

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr parameter, IntPtr value);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);

    private void OnTimer(DispatcherQueueTimer sender, object args)
    {
        if (!_refresh.RunTimer || _closed) return;
        _ = RefreshAsync();
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        bool gained = _refresh.SetActive(args.WindowActivationState != WindowActivationState.Deactivated);
        if (_refresh.RunTimer) _timer.Start();
        else _timer.Stop();
        if (gained) _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (!_refresh.TryBegin()) return;
        BusyIndicator.IsActive = true;
        try
        {
            var snapshot = await _hub.RefreshAsync(true, _shutdown.Token, ready =>
            {
                if (_closed) return;
                _applicationsLoaded = true;
                ApplyApps(ready.Apps, false);
                ApplyRecent(ready.Documents);
                UpdateAppsEmptyState();
            });
            if (!_closed && snapshot != null)
            {
                ApplyRecent(snapshot.Documents);
                Notification.IsOpen = snapshot.Warnings.Count > 0;
                if (snapshot.Warnings.Count > 0) ShowWarning(string.Join(Text.T("MessageSeparator"), snapshot.Warnings));
                UpdateAppsEmptyState();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally
        {
            bool again = _refresh.Complete();
            if (!_closed) BusyIndicator.IsActive = false;
            if (again) _ = RefreshAsync();
        }
    }

    private void ApplyApps(IReadOnlyList<DetectedApp> apps, bool force)
    {
        _icons.SetApplications(apps, force);
        StableList.Apply(OfficeApps, apps, item => item.Model.Definition.Key, app => app.Definition.Key, app =>
        {
            var item = new OfficeAppItem(app);
            _ = LoadAppIconAsync(item);
            return item;
        }, (item, app) =>
        {
            bool reload = force || item.Icon == null || item.Model.Executable != app.Executable || item.Model.AppUserModelId != app.AppUserModelId;
            item.Update(app);
            if (reload) _ = LoadAppIconAsync(item);
        });
    }

    private async Task LoadAppIconAsync(OfficeAppItem item)
    {
        int request = ++item.IconRequest;
        try
        {
            var app = item.Model;
            var icon = await _icons.ForAppAsync(app, _shutdown.Token);
            if (!_closed && item.IconRequest == request && ReferenceEquals(item.Model, app)) item.Icon = icon;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex) { LogService.Write("Icon", "Application icon failed", ex, level: LogService.LogLevel.Warn); }
    }

    private void ApplyRecent(IReadOnlyList<RecentItem> items)
    {
        StableList.Apply(RecentItems, items, item => item.Model.Identity, item => item.Identity,
            item => new RecentItemViewModel(item), (view, model) => view.Update(model));
        foreach (var item in RecentItems)
            if (RecentList.ContainerFromItem(item) is ListViewItem) _ = LoadDocumentIconAsync(item);
    }

    private void UpdateAppsEmptyState()
    {
        AppsEmpty.Visibility = !_artCraftSelected && _applicationsLoaded && OfficeApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyApplicationTab()
    {
        MsOfficeTab.IsChecked = !_artCraftSelected;
        ArtCraftTab.IsChecked = _artCraftSelected;
        Grid.SetColumn(ApplicationSwitchSelection, _artCraftSelected ? 1 : 0);
        MsOfficeTabLabel.FontWeight = _artCraftSelected ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold;
        ArtCraftTabLabel.FontWeight = _artCraftSelected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        MsOfficeTabLabel.Opacity = _artCraftSelected ? 0.6 : 1;
        ArtCraftTabLabel.Opacity = _artCraftSelected ? 1 : 0.6;
        OfficeList.Visibility = _artCraftSelected ? Visibility.Collapsed : Visibility.Visible;
        ArtCraftPanel.Visibility = _artCraftSelected ? Visibility.Visible : Visibility.Collapsed;
        UpdateAppsEmptyState();
    }

    private void SelectApplicationTab(bool artCraft)
    {
        bool changed = _artCraftSelected != artCraft;
        _artCraftSelected = artCraft;
        ApplyApplicationTab();
        if (changed) LogService.Write("UI", $"Application tab selected tab={(artCraft ? "ArtCraft" : "MsOffice")}");
    }

    private void ApplicationTab_Click(object sender, RoutedEventArgs args)
    {
        if (_closed) return;
        bool artCraft = ReferenceEquals(sender, ArtCraftTab);
        bool alreadySelected = _artCraftSelected == artCraft;
        SelectApplicationTab(artCraft);
        if (alreadySelected && !artCraft) ShowOfficeManagerMenu();
    }

    private void ApplicationTab_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        args.Handled = true;
        if (ReferenceEquals(sender, MsOfficeTab)) ShowOfficeManagerMenu();
    }

    private void ApplicationTab_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        bool? artCraft = args.Key switch
        {
            Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Home => false,
            Windows.System.VirtualKey.Right or Windows.System.VirtualKey.Down or Windows.System.VirtualKey.End => true,
            _ => null
        };
        if (!artCraft.HasValue) return;
        SelectApplicationTab(artCraft.Value);
        (_artCraftSelected ? ArtCraftTab : MsOfficeTab).Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    private async void RecentList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not RecentItemViewModel item) return;
        await LoadDocumentIconAsync(item);
    }

    private async Task LoadDocumentIconAsync(RecentItemViewModel item)
    {
        if (!item.Preview.TryBegin(out int request)) return;
        try
        {
            var icon = await _icons.ForDocumentAsync(item.Model, _shutdown.Token);
            if (item.Preview.Complete(request, icon != null) && !_closed) item.Icon = icon;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { item.Preview.Complete(request, false); }
        catch (Exception ex) { item.Preview.Complete(request, false); LogService.Write("Icon", "Visible document icon failed", ex, level: LogService.LogLevel.Warn); }
    }

    private void Document_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement element || element.DataContext is not RecentItemViewModel item) return;
        args.Handled = true;
        var target = DocumentActions.Menu(item.Model);
        var menu = new MenuFlyout();
        var reveal = new MenuFlyoutItem { Text = Text.T(target.IsWeb ? "OpenWebLocation" : "RevealDocument"), Icon = new FontIcon { Glyph = "\uE838" } };
        var copy = new MenuFlyoutItem { Text = Text.T(target.IsLink ? "CopyLink" : "CopyPath"), Icon = new FontIcon { Glyph = "\uE8C8" } };
        reveal.Click += async (_, _) => RenderOperation(await _operations.OpenContainingLocationAsync(item.Model));
        copy.Click += (_, _) =>
        {
            try
            {
                var content = new Windows.ApplicationModel.DataTransfer.DataPackage();
                content.SetText(target.CopyText);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(content);
                Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
                LogService.Write("Document", "Document target copied to clipboard");
            }
            catch (Exception ex) { LogService.Write("Document", "Copy target failed", ex, level: LogService.LogLevel.Warn); ShowWarning(Text.T("CopyFailed")); }
        };
        menu.Items.Add(reveal);
        menu.Items.Add(copy);
        if (args.TryGetPosition(element, out var position)) menu.ShowAt(element, new FlyoutShowOptions { Position = position });
        else menu.ShowAt(element);
    }
    private async void OfficeList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not OfficeAppItem app) return;
        RenderOperation(await _operations.OpenAppAsync(app.Model));
    }

    private void ShowOfficeManagerMenu()
    {
        try
        {
            if (_closed) return;
            var menu = new MenuFlyout();
            var repair = new MenuFlyoutItem { Text = Text.T("OfficeManagerRepair"), Icon = new FontIcon { Glyph = "\uE90F" }, IsEnabled = !_officeManagerBusy };
            var install = new MenuFlyoutItem { Text = Text.T("OfficeManagerInstall"), Icon = new FontIcon { Glyph = "\uE896" }, IsEnabled = !_officeManagerBusy };
            repair.Click += async (_, _) => await RunOfficeManagerAsync(OfficeManagerAction.Repair);
            install.Click += async (_, _) => await RunOfficeManagerAsync(OfficeManagerAction.Install);
            menu.Items.Add(repair);
            menu.Items.Add(install);
            menu.ShowAt(MsOfficeTab);
            LogService.Write("OfficeManager", "Office maintenance menu opened from application tab");
        }
        catch (Exception ex)
        {
            LogService.Write("OfficeManager", "Office manager menu unavailable", ex, level: LogService.LogLevel.Error);
            if (!_closed) ShowWarning(Text.T("OfficeManagerFailed"));
        }
    }

    private async Task RunOfficeManagerAsync(OfficeManagerAction action)
    {
        if (_officeManagerBusy) return;
        _officeManagerBusy = true;
        try { RenderOfficeManager(await _officeManager.RunAsync(action, _shutdown.Token)); }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally { _officeManagerBusy = false; }
    }

    private void RenderOfficeManager(OfficeManagerResult result)
    {
        if (_closed) return;
        if (result.Process is { } process)
        {
            if (!OfficeWindowActivator.Activate(process)) ShowOfficeManagerMessage(Text.T("OfficeManagerRunning"), false);
        }
        else if (result.Message != null) ShowOfficeManagerMessage(result.Message, result.IsError);
    }
    private void ShowOfficeManagerMessage(string message, bool error)
    {
        Notification.Message = message;
        Notification.Severity = error ? InfoBarSeverity.Warning : InfoBarSeverity.Informational;
        Notification.IsOpen = true;
    }
    private async void RecentList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RecentItemViewModel item)
            RenderOperation(await _operations.OpenDocumentAsync(item.Model));
    }
    private async void OpenLogsButton_Click(object sender, RoutedEventArgs e) => RenderOperation(await _operations.OpenLogsAsync());
    private void RenderOperation(string? error) { if (!_closed && error != null) ShowWarning(error); }
    private void ShowWarning(string message) { Notification.Message = message; Notification.Severity = InfoBarSeverity.Warning; Notification.IsOpen = true; }

    private void DocumentPanel_ResizeRequested(object? sender, double delta) => SetColumnWidth(LeftColumn.Width.Value + delta);
    private void SetColumnWidth(double width) => LeftColumn.Width = new GridLength(Math.Clamp(width, LeftColumn.MinWidth, LeftColumn.MaxWidth));

    private void LoadWindowState()
    {
        try
        {
            var areas = DisplayArea.FindAll();
            var displays = new List<WindowBounds>();
            for (int index = 0; index < areas.Count; index++)
            {
                var work = areas[index].WorkArea;
                displays.Add(new(work.X, work.Y, work.Width, work.Height));
            }
            if (displays.Count == 0) throw new InvalidOperationException("No display work area available");
            var state = WindowStateService.Load(new(AppWindow.Position.X, AppWindow.Position.Y, 900, 650), displays);
            var bounds = WindowPlacement.Fit(state.Bounds, displays);
            AppWindow.MoveAndResize(new(bounds.X, bounds.Y, bounds.Width, bounds.Height));
            uint dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
            bounds = WindowPlacement.Restore(state.Bounds, state.Dpi, dpi, displays);
            AppWindow.MoveAndResize(new(bounds.X, bounds.Y, bounds.Width, bounds.Height));
            _windowState = new(state with { Bounds = bounds, Dpi = dpi });
            SetColumnWidth(state.ColumnWidth);
            if (state.Maximized && AppWindow.Presenter is OverlappedPresenter presenter) presenter.Maximize();
        }
        catch (Exception ex) { LogService.Write("Window", "Window bounds restore failed", ex, level: LogService.LogLevel.Warn); }
    }

    private void WindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (_closed || _windowState == null || sender.Presenter is not OverlappedPresenter presenter) return;
        WindowMode mode = presenter.State switch
        {
            OverlappedPresenterState.Maximized => WindowMode.Maximized,
            OverlappedPresenterState.Minimized => WindowMode.Minimized,
            _ => WindowMode.Normal
        };
        _windowState.Observe(mode, new(sender.Position.X, sender.Position.Y, sender.Size.Width, sender.Size.Height), GetDpiForWindow(WindowNative.GetWindowHandle(this)));
        if (mode == WindowMode.Minimized) { _refresh.SetActive(false); _timer.Stop(); }
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        WindowChanged(AppWindow, null!);
        _closed = true;
        DocumentPanel.ResizeRequested -= DocumentPanel_ResizeRequested;
        _refresh.Close();
        AppWindow.Changed -= WindowChanged;
        _timer.Stop();
        _timer.Tick -= OnTimer;
        Activated -= OnWindowActivated;
        _shutdown.Cancel();
        TitleBarGrid.Loaded -= TitleBarGrid_Loaded;
        TitleBarGrid.SizeChanged -= TitleBarGrid_SizeChanged;
        if (_titleBarXamlRoot != null) _titleBarXamlRoot.Changed -= TitleBarXamlRoot_Changed;
        if (_windowIcon != IntPtr.Zero) { DestroyIcon(_windowIcon); _windowIcon = IntPtr.Zero; }
        if (_windowState != null) WindowStateService.Save(_windowState.State with { ColumnWidth = LeftColumn.Width.Value });
        LogService.Write("App", "Main window closed; refresh cancelled");
        LogService.FlushAndStop();
    }
}

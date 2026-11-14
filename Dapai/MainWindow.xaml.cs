using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Dapai;

public partial class MainWindow : Window
{
    private readonly AppSettings settings;
    private readonly ObservableCollection<LaunchItem> launchItems;
    private readonly KeyboardBlocker keyboard = new();
    private readonly DispatcherTimer englishTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private readonly TrayIcon? tray;
    private DisplayControl.Snapshot? displaySnapshot;
    private IntPtr inputSnapshot;
    private (bool Win, bool Alt)? keySnapshot;
    private bool gameMode;
    private bool initializing = true;
    private bool refreshingAudio;
    private bool operating;
    private bool exiting;
    private bool stacked;

    public MainWindow()
    {
        settings = ConfigStore.Load();
        launchItems = new(settings.Apps);

        InitializeComponent();
        foreach (var item in launchItems)
            InitializeLaunchItem(item);
        LaunchList.ItemsSource = launchItems;
        UpdateLaunchVisibility();

        var ui = settings.UiState;
        BlockWin.IsChecked = ui.BlockWin;
        BlockAlt.IsChecked = ui.BlockAltShift;
        ForceEnglish.IsChecked = ui.ForceEnglish;
        ManageDisplay.IsChecked = ui.ManageDisplay;
        DisplayChoice.ItemsSource = DisplayControl.Modes.Keys;
        DisplayChoice.SelectedItem = ui.DisplayChoice;
        SwitchAudio.IsChecked = ui.SwitchAudio;
        RefreshAudio(initial: true);

        englishTimer.Tick += (_, _) =>
        {
            if (gameMode && ForceEnglish.IsChecked == true)
                RunOperation("切换英文输入失败", ForceEnglishWithSnapshot, showError: false);
        };
        englishTimer.Start();
        tray = TrayIcon.TryCreate(ShowFromTray, Optimize, async () => await RestoreAsync(showErrors: true), ExitApplication);
        initializing = false;
    }

    private void WindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = ActualWidth < 860;
        if (LaunchPanel is null || narrow == stacked)
            return;

        stacked = narrow;
        SettingsColumn.Width = new GridLength(narrow ? 1 : 54, GridUnitType.Star);
        GutterColumn.Width = new GridLength(narrow ? 0 : 20);
        LaunchColumn.Width = new GridLength(narrow ? 0 : 46, GridUnitType.Star);
        Grid.SetColumn(LaunchPanel, narrow ? 0 : 2);
        Grid.SetRow(LaunchPanel, narrow ? 1 : 0);
        LaunchPanel.Margin = new Thickness(0, narrow ? 18 : 0, 0, 0);
    }

    private void SettingChanged(object sender, RoutedEventArgs e)
    {
        if (initializing)
            return;

        var ui = settings.UiState;
        ui.BlockWin = BlockWin.IsChecked == true;
        ui.BlockAltShift = BlockAlt.IsChecked == true;
        ui.ForceEnglish = ForceEnglish.IsChecked == true;
        ui.ManageDisplay = ManageDisplay.IsChecked == true;
        ui.DisplayChoice = DisplayChoice.SelectedItem as string ?? DisplayControl.DefaultName;
        ui.SwitchAudio = SwitchAudio.IsChecked == true;
        SaveSettings();
    }

    private void AudioChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing || refreshingAudio || AudioChoice.SelectedItem is not AudioDevice device)
            return;

        settings.UiState.DefaultAudioChoice = device.Name;
        settings.UiState.AudioChoice = device.Name;
        SaveSettings();
    }

    private void RefreshAudioClicked(object sender, RoutedEventArgs e) => RefreshAudio(initial: false);

    private void RefreshAudio(bool initial)
    {
        refreshingAudio = true;
        try
        {
            var selectedId = (AudioChoice.SelectedItem as AudioDevice)?.Id;
            var devices = AudioControl.ListDevices(out string? systemDefaultId);
            var ui = settings.UiState;
            AudioChoice.ItemsSource = devices;
            var selected = devices.Find(device => device.Id == selectedId)
                ?? devices.Find(device => device.Name == ui.DefaultAudioChoice)
                ?? devices.Find(device => device.Name == ui.AudioChoice)
                ?? devices.Find(device => device.Id == systemDefaultId)
                ?? devices.FirstOrDefault();
            AudioChoice.SelectedItem = selected;
            if (selected is not null)
                ui.AudioChoice = selected.Name;
            AppLog.Write($"输出设备列表已刷新，共 {devices.Count} 个设备。");
            if (!initial)
            {
                SaveSettings();
                Info("输出设备列表已刷新。");
            }
        }
        catch (Exception ex)
        {
            AudioChoice.ItemsSource = null;
            AppLog.Write("获取输出设备列表失败。", ex);
            if (!initial)
                Warn("提示", $"获取输出设备列表失败。\n{ex.Message}");
        }
        finally
        {
            refreshingAudio = false;
        }
    }

    private void AddLaunchItem(object sender, RoutedEventArgs e)
    {
        var item = new LaunchItem { Name = $"启动项{launchItems.Count + 1}" };
        InitializeLaunchItem(item);
        launchItems.Add(item);
        UpdateLaunchVisibility();
        SaveSettings();
    }

    private void BrowseLaunchItem(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LaunchItem item })
            return;

        var dialog = new OpenFileDialog
        {
            Title = $"选择 {item.Name} 的可执行文件",
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (!string.IsNullOrWhiteSpace(item.Path))
        {
            try
            {
                string path = LaunchService.ResolvePath(item.Path);
                if (File.Exists(path))
                    dialog.FileName = path;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
            {
                AppLog.Write("无法定位启动项原有文件。", ex);
            }
        }
        if (dialog.ShowDialog(this) == true)
            item.Path = dialog.FileName;
    }

    private void RemoveLaunchItem(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LaunchItem item })
            return;

        item.PropertyChanged -= LaunchItemChanged;
        launchItems.Remove(item);
        UpdateLaunchVisibility();
        SaveSettings();
    }

    private void MoveLaunchItemUp(object sender, RoutedEventArgs e) => MoveLaunchItem(sender, -1);

    private void MoveLaunchItemDown(object sender, RoutedEventArgs e) => MoveLaunchItem(sender, 1);

    private void MoveLaunchItem(object sender, int offset)
    {
        if (sender is not FrameworkElement { DataContext: LaunchItem item })
            return;

        int index = launchItems.IndexOf(item);
        int destination = index + offset;
        if (index < 0 || destination < 0 || destination >= launchItems.Count)
            return;

        launchItems.Move(index, destination);
        UpdateLaunchVisibility();
        SaveSettings();
    }

    private void InitializeLaunchItem(LaunchItem item)
    {
        item.Icon = LaunchService.ReadIcon(item.Path);
        item.PropertyChanged += LaunchItemChanged;
    }

    private void LaunchItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not LaunchItem item)
            return;

        if (e.PropertyName == nameof(LaunchItem.Path))
            item.Icon = LaunchService.ReadIcon(item.Path);

        if (e.PropertyName is nameof(LaunchItem.Name) or nameof(LaunchItem.Path) or nameof(LaunchItem.Enabled))
            SaveSettings();
    }

    private void UpdateLaunchVisibility()
    {
        LaunchCard.Visibility = launchItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LaunchEmpty.Visibility = launchItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < launchItems.Count; i++)
        {
            launchItems[i].CanMoveUp = i > 0;
            launchItems[i].CanMoveDown = i < launchItems.Count - 1;
        }
    }

    private void SaveSettings()
    {
        settings.Apps = launchItems.ToList();
        ConfigStore.Save(settings);
    }

    private void OptimizeClicked(object sender, RoutedEventArgs e) => Optimize();

    private void Optimize()
    {
        if (operating || exiting)
            return;

        operating = true;
        try
        {
            var ui = settings.UiState;
            AppLog.Write("开始执行一键优化。");
            keySnapshot ??= (keyboard.BlockWin, keyboard.BlockAltShift);
            if (ui.ManageDisplay)
            {
                RunOperation("切换显示模式失败", () =>
                {
                    uint mode = DisplayControl.Modes[ui.DisplayChoice];
                    var current = DisplayControl.Capture();
                    if (!DisplayControl.HasMultipleDisplays(current))
                    {
                        AppLog.Write("只有一块可用屏幕，保留当前显示模式。");
                        return;
                    }
                    displaySnapshot ??= current;
                    DisplayControl.Apply(mode);
                    AppLog.Write($"显示模式已切换到：{ui.DisplayChoice}");
                });
            }
            if (ui.SwitchAudio)
            {
                if (AudioChoice.SelectedItem is AudioDevice device)
                {
                    RunOperation("切换音频输出失败", () =>
                    {
                        AudioControl.SetDefault(device.Id);
                        AppLog.Write($"默认音频设备已切换到：{device.Name}");
                    });
                }
                else
                    Warn("提示", "未选择默认音频设备。");
            }
            RunOperation("设置键盘屏蔽失败", () => keyboard.Set(ui.BlockWin, ui.BlockAltShift));
            if (ui.ForceEnglish)
            {
                RunOperation("切换英文输入失败", () =>
                {
                    ForceEnglishWithSnapshot();
                    AppLog.Write("已强制切换前台窗口到英文输入。");
                });
            }
            RunOperation("快捷启动失败", () => LaunchService.Launch(launchItems, Warn));
            SetGameMode(true);
            SaveSettings();
        }
        finally
        {
            operating = false;
        }
    }

    private void ForceEnglishWithSnapshot()
    {
        if (inputSnapshot == IntPtr.Zero)
            inputSnapshot = WindowsSession.ForegroundLayout();
        WindowsSession.ForceEnglish();
    }

    private async void RestoreClicked(object sender, RoutedEventArgs e) => await RestoreAsync(showErrors: true);

    private async Task<Exception?> RestoreAsync(bool showErrors)
    {
        if (operating || (showErrors && exiting))
            return null;

        Exception? displayError = null;
        operating = true;
        try
        {
            AppLog.Write(showErrors ? "开始执行一键恢复。" : "退出前自动恢复。");
            gameMode = false;
            ModeStatus.Text = "正在恢复…";
            var previousKeys = keySnapshot.GetValueOrDefault();
            RunOperation("恢复键盘状态失败", () =>
            {
                keyboard.Set(previousKeys.Win, previousKeys.Alt);
                keySnapshot = null;
            }, showErrors);
            if (displaySnapshot is { } display)
            {
                try
                {
                    await DisplayControl.RestoreAsync(display);
                    displaySnapshot = null;
                }
                catch (Exception ex)
                {
                    displayError = ex;
                    AppLog.Write("恢复显示模式失败，保留优化前的配置。", ex);
                    if (showErrors)
                        Warn("恢复显示模式失败", ex.Message);
                }
            }
            if (inputSnapshot != IntPtr.Zero)
            {
                RunOperation("恢复输入法布局失败", () =>
                {
                    WindowsSession.RestoreLayout(inputSnapshot);
                    inputSnapshot = IntPtr.Zero;
                }, showErrors);
            }
            SaveSettings();
            return displayError;
        }
        finally
        {
            ModeStatus.Text = displaySnapshot is null ? "普通模式" : "显示配置待恢复";
            operating = false;
        }
    }

    private void SetGameMode(bool active)
    {
        gameMode = active;
        ModeStatus.Text = active ? "游戏模式已启用" : "普通模式";
    }

    private void RunOperation(string error, Action action, bool showError = true)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            AppLog.Write(error, ex);
            if (showError)
                Warn("提示", $"{error}。\n{ex.Message}");
        }
    }

    internal void ShowFromTray()
    {
        if (exiting)
            return;
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (exiting)
        {
            e.Cancel = operating;
            return;
        }
        e.Cancel = true;
        if (tray is { IsAvailable: true })
        {
            Hide();
            AppLog.Write("窗口已隐藏到托盘。");
        }
        else
            Dispatcher.BeginInvoke(ExitApplication);
    }

    private async void ExitApplication()
    {
        if (exiting || operating)
            return;

        exiting = true;
        AppLog.Write("准备退出程序。");
        if (gameMode || displaySnapshot is not null || inputSnapshot != IntPtr.Zero || keySnapshot is not null)
        {
            var displayError = await RestoreAsync(showErrors: false);
            if (displayError is not null && !ConfirmExitAfterDisplayFailure(displayError))
            {
                exiting = false;
                return;
            }
        }
        else
            SaveSettings();

        englishTimer.Stop();
        keyboard.Dispose();
        tray?.Dispose();
        Application.Current.Shutdown();
    }

    private bool ConfirmExitAfterDisplayFailure(Exception error)
    {
        string message = $"{error.Message}\n\n要保留当前显示状态并退出吗？";
        const string title = "恢复显示模式失败";
        var result = IsVisible
            ? MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    private void Info(string message) => ShowMessage("提示", message, MessageBoxImage.Information);

    private void Warn(string title, string message) => ShowMessage(title, message, MessageBoxImage.Warning);

    private void ShowMessage(string title, string message, MessageBoxImage icon)
    {
        if (IsVisible)
            MessageBox.Show(this, message, title, MessageBoxButton.OK, icon);
        else
            MessageBox.Show(message, title, MessageBoxButton.OK, icon);
    }
}

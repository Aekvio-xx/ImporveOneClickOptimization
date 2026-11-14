using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Dapai;

internal sealed class AppSettings
{
    public UiSettings UiState { get; set; } = new();
    public List<LaunchItem> Apps { get; set; } =
    [
        new() { Name = "加速器" },
        new() { Name = "语音软件" },
        new() { Name = "其他工具" },
    ];
}

internal sealed class UiSettings
{
    public bool BlockWin { get; set; } = true;
    public bool BlockAltShift { get; set; } = true;
    public bool ForceEnglish { get; set; } = true;
    public bool ManageDisplay { get; set; } = true;
    public string DisplayChoice { get; set; } = DisplayControl.DefaultName;
    public bool SwitchAudio { get; set; } = true;
    public string DefaultAudioChoice { get; set; } = "";
    public string AudioChoice { get; set; } = "";
}

internal sealed class LaunchItem : INotifyPropertyChanged
{
    private string name = "";
    private string path = "";
    private bool enabled;
    private bool canMoveUp, canMoveDown;
    private ImageSource? icon;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name
    {
        get => name;
        set => Set(ref name, value ?? "");
    }

    public string Path
    {
        get => path;
        set
        {
            if (!Set(ref path, value ?? ""))
                return;
            PropertyChanged?.Invoke(this, new(nameof(FileLabel)));
            PropertyChanged?.Invoke(this, new(nameof(FileDescription)));
        }
    }

    public bool Enabled
    {
        get => enabled;
        set => Set(ref enabled, value);
    }

    [JsonIgnore]
    public bool CanMoveUp
    {
        get => canMoveUp;
        set => Set(ref canMoveUp, value);
    }

    [JsonIgnore]
    public bool CanMoveDown
    {
        get => canMoveDown;
        set => Set(ref canMoveDown, value);
    }

    [JsonIgnore]
    public ImageSource? Icon
    {
        get => icon;
        set
        {
            if (Set(ref icon, value))
                PropertyChanged?.Invoke(this, new(nameof(UsesPlaceholder)));
        }
    }

    [JsonIgnore]
    public bool UsesPlaceholder => Icon is null;

    [JsonIgnore]
    public string FileLabel => string.IsNullOrWhiteSpace(Path)
        ? "未选择文件"
        : System.IO.Path.GetFileName(Path);

    [JsonIgnore]
    public string FileDescription => string.IsNullOrWhiteSpace(Path) ? FileLabel : Path;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new(property));
        return true;
    }
}

internal static class ConfigStore
{
    private static readonly string Path = System.IO.Path.Combine(AppContext.BaseDirectory, "config.json");
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AppSettings Load()
    {
        if (!File.Exists(Path))
            return new();

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Options) ?? new();
            settings.UiState ??= new();
            settings.Apps ??= new();
            settings.Apps.RemoveAll(item => item is null);
            var ui = settings.UiState;
            ui.DefaultAudioChoice = ui.DefaultAudioChoice?.Trim() ?? "";
            ui.AudioChoice = ui.AudioChoice?.Trim() ?? "";
            if (ui.DefaultAudioChoice.Length == 0)
                ui.DefaultAudioChoice = ui.AudioChoice;
            if (ui.AudioChoice.Length == 0)
                ui.AudioChoice = ui.DefaultAudioChoice;
            if (!DisplayControl.Modes.ContainsKey(ui.DisplayChoice ?? ""))
                ui.DisplayChoice = DisplayControl.DefaultName;
            AppLog.Write("配置已加载。");
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Write("加载配置失败，使用默认配置。", ex);
            return new();
        }
    }

    public static void Save(AppSettings settings)
    {
        string temporary = Path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
            File.Move(temporary, Path, overwrite: true);
            AppLog.Write("配置已保存。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("保存配置失败。", ex);
        }
    }
}

internal static class AppLog
{
    private static readonly string DirectoryPath = System.IO.Path.Combine(AppContext.BaseDirectory, "logs");

    public static void Write(string message, Exception? error = null)
    {
        var now = DateTime.Now;
        string line = $"{now:yyyy-MM-dd HH:mm:ss} | {(error is null ? "INFO" : "ERROR")} | {message}";
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.AppendAllText(System.IO.Path.Combine(DirectoryPath, $"{now:yyyy-MM-dd}.log"),
                line + Environment.NewLine + (error is null ? "" : error + Environment.NewLine));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine(line);
        }
    }
}

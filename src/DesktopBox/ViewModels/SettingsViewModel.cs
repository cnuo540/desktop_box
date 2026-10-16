using System;
using CommunityToolkit.Mvvm.ComponentModel;
using DesktopBox.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopBox.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IStartupService _startup;
    private readonly IThemeService _theme;
    private readonly IPersistenceService _store;
    private readonly ILocalizerService _localizer;
    private bool _persistedFollowSystemTheme;
    private bool _persistedIsDark;
    private string _persistedLanguage = "auto";
    private bool _persistedAutoStart;
    private bool _isRollingBack;

    public event EventHandler<Exception>? PersistenceFailed;

    [ObservableProperty] private bool _autoStart;
    [ObservableProperty] private bool _followSystemTheme;
    [ObservableProperty] private bool _isDark;
    [ObservableProperty] private string _language = "auto";

    /// <summary>语言下拉选中索引:0=auto(跟随系统), 1=简体中文, 2=English。</summary>
    public int LangIndex
    {
        get => Language switch { "zh-CN" => 1, "en-US" => 2, _ => 0 };
        set => Language = value switch { 1 => "zh-CN", 2 => "en-US", _ => "auto" };
    }

    /// <summary>卷起盒子的悬停自动展开开关。状态与持久化都归 MainViewModel(BoxControl 运行时读同一个值),
    /// 这里只做设置界面的双向代理:勾选即时生效并自动保存。</summary>
    public bool HoverExpandCollapsedBoxes
    {
        get => Main.HoverExpandCollapsedBoxes;
        set
        {
            if (Main.HoverExpandCollapsedBoxes == value) return;
            try
            {
                Main.HoverExpandCollapsedBoxes = value;
                OnPropertyChanged();
            }
            catch (Exception ex)
            {
                ReportPersistenceFailure(ex);
            }
        }
    }

    /// <summary>盒子背景的不透明度(0.1~1.0,持久化)。只作用于背景色 —— 图标和文字不跟着变淡。
    /// 真正的状态与保存都归 MainViewModel(和托盘菜单里那个滑条读写同一个值),
    /// 这里只做设置面板的双向代理:拖动即时生效、由 MainViewModel 防抖存盘。</summary>
    public double BoxOpacity
    {
        get => Main.BoxOpacity;
        set
        {
            var clamped = Math.Clamp(value, 0.1, 1.0);
            if (Math.Abs(Main.BoxOpacity - clamped) < 0.0005) return;
            try
            {
                Main.BoxOpacity = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BoxOpacityPercent));
            }
            catch (Exception ex)
            {
                ReportPersistenceFailure(ex);
            }
        }
    }

    /// <summary>滑条右侧的百分比读数(1.0 → "100%")。</summary>
    public string BoxOpacityPercent => $"{Math.Round(BoxOpacity * 100)}%";

    /// <summary>设置面板底部显示的版本号。取自程序集,跟着 csproj 的 &lt;Version&gt; 走,不必手改这里。</summary>
    public string VersionText
    {
        get
        {
            var version = typeof(SettingsViewModel).Assembly.GetName().Version;
            return version is null ? "DesktopBox" : $"DesktopBox v{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    private MainViewModel? _main;
    private MainViewModel Main =>
        _main ??= App.Services.GetRequiredService<MainViewModel>();

    public SettingsViewModel(IStartupService startup, IThemeService theme, IPersistenceService store, ILocalizerService localizer)
    {
        _startup = startup;
        _theme = theme;
        _store = store;
        _localizer = localizer;

        var cfg = store.Load();
        // Prefer live registry state; config is a durable preference used to re-apply after upgrades/moves.
        _autoStart = startup.IsEnabled() || cfg.Settings.AutoStart;
        _followSystemTheme = cfg.Settings.FollowSystemTheme;
        _isDark = _followSystemTheme ? _theme.IsSystemDark()
            : cfg.Settings.Theme.Equals("Dark", StringComparison.OrdinalIgnoreCase);
        _language = string.IsNullOrEmpty(cfg.Settings.Language) ? "auto" : cfg.Settings.Language;
        _persistedFollowSystemTheme = _followSystemTheme;
        _persistedIsDark = _isDark;
        _persistedLanguage = _language;
        _persistedAutoStart = _autoStart;
    }

    partial void OnAutoStartChanged(bool value)
    {
        if (_isRollingBack) return;
        try
        {
            if (value) _startup.Enable();
            else _startup.Disable();

            var cfg = _store.Load();
            cfg.Settings.AutoStart = value;
            _store.Save(cfg);
            _persistedAutoStart = value;
        }
        catch (Exception ex)
        {
            _isRollingBack = true;
            try { AutoStart = _persistedAutoStart; }
            finally { _isRollingBack = false; }
            ReportPersistenceFailure(ex);
        }
    }

    partial void OnFollowSystemThemeChanged(bool value)
    {
        if (!_isRollingBack) ApplyChanges();
    }

    partial void OnIsDarkChanged(bool value)
    {
        if (!_isRollingBack && !FollowSystemTheme) ApplyChanges();
    }

    partial void OnLanguageChanged(string value)
    {
        if (_isRollingBack) return;
        try
        {
            // Persist before applying so a failed write cannot leave UI and disk disagreeing.
            var cfg = _store.Load();
            cfg.Settings.Language = value;
            _store.Save(cfg);
            _persistedLanguage = value;
            _localizer.Apply(value);
        }
        catch (Exception ex)
        {
            _isRollingBack = true;
            try { Language = _persistedLanguage; }
            finally { _isRollingBack = false; }
            OnPropertyChanged(nameof(LangIndex));
            ReportPersistenceFailure(ex);
        }
    }

    public void ApplyChanges()
    {
        try
        {
            var cfg = _store.Load();
            cfg.Settings.FollowSystemTheme = FollowSystemTheme;
            if (!FollowSystemTheme) cfg.Settings.Theme = IsDark ? "Dark" : "Light";
            _store.Save(cfg);
            _persistedFollowSystemTheme = FollowSystemTheme;
            _persistedIsDark = IsDark;
        }
        catch (Exception ex)
        {
            _isRollingBack = true;
            try
            {
                FollowSystemTheme = _persistedFollowSystemTheme;
                IsDark = _persistedIsDark;
            }
            finally { _isRollingBack = false; }
            ReportPersistenceFailure(ex);
            return;
        }

        if (FollowSystemTheme)
        {
            _theme.ApplySystem();
        }
        else
        {
            _theme.StopFollowingSystem();
            _theme.Apply(IsDark ? "Dark" : "Light");
        }
    }

    private void ReportPersistenceFailure(Exception exception)
    {
        App.LogError(exception, "SettingsViewModel.Save");
        PersistenceFailed?.Invoke(this, exception);
    }
}

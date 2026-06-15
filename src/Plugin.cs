using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

using DamageMeter.Windows;

namespace DamageMeter;

public sealed class Plugin : IDalamudPlugin
{
    // ── Injected services ─────────────────────────────────────────────────────
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IPluginLog              Log             { get; private set; } = null!;
    [PluginService] internal static ICommandManager         CommandManager  { get; private set; } = null!;
    [PluginService] internal static IChatGui                ChatGui         { get; private set; } = null!;
    [PluginService] internal static IClientState            ClientState     { get; private set; } = null!;
    [PluginService] internal static IObjectTable            ObjectTable     { get; private set; } = null!;
    [PluginService] internal static ICondition              Condition       { get; private set; } = null!;
    [PluginService] internal static IFramework              Framework       { get; private set; } = null!;
    [PluginService] internal static IDataManager            DataManager     { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider    GameInterop     { get; private set; } = null!;
    [PluginService] internal static ITextureProvider        TextureProvider { get; private set; } = null!;
    [PluginService] internal static IPartyList              PartyList       { get; private set; } = null!;
    [PluginService] internal static IFlyTextGui             FlyTextGui      { get; private set; } = null!;

    // ── Cross-assembly static accessor (for DamageMeterUmbra) ─────────────────
    public static Plugin? Instance { get; private set; }

    // ── Plugin state ──────────────────────────────────────────────────────────
    internal Configuration Config  { get; }
    public   CombatTracker Tracker { get; }

    internal readonly MainWindow     _mainWindow;
    internal readonly HistoryWindow  _historyWindow;
    internal readonly SettingsWindow _settingsWindow;
    internal readonly StatusApi      _statusApi;
    internal readonly IpcBridge      _ipcBridge;

    private const string CmdMain     = "/dm";
    private const string CmdHistory  = "/dmhistory";
    private const string CmdSettings = "/dmsettings";

    // ── Constructor ───────────────────────────────────────────────────────────
    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.MigrateIfNeeded();

        var configDir = PluginInterface.GetPluginConfigDirectory();

        Tracker = new CombatTracker(
            GameInterop, Log, Condition, ObjectTable,
            ClientState, Framework, DataManager, PartyList,
            FlyTextGui, ChatGui, Config, configDir);

        _mainWindow     = new MainWindow(this);
        _historyWindow  = new HistoryWindow(this);
        _settingsWindow = new SettingsWindow(this);
        _statusApi      = new StatusApi(this);
        _ipcBridge      = new IpcBridge(this);

        // Clear any history-window pin the moment a fresh combat session starts —
        // the user clearly wants live data once they swing again.
        Tracker.OnSessionStarted += _ => _historyWindow.ClearPin();

        CommandManager.AddHandler(CmdMain, new Dalamud.Game.Command.CommandInfo(OnMainCommand)
        {
            HelpMessage = "Toggle the Damage Meter window."
        });
        CommandManager.AddHandler(CmdHistory, new Dalamud.Game.Command.CommandInfo(OnHistoryCommand)
        {
            HelpMessage = "Open the Damage Meter session history."
        });
        CommandManager.AddHandler(CmdSettings, new Dalamud.Game.Command.CommandInfo(OnSettingsCommand)
        {
            HelpMessage = "Open Damage Meter settings."
        });

        PluginInterface.UiBuilder.Draw          += OnDraw;
        PluginInterface.UiBuilder.OpenMainUi    += OnOpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi  += OnOpenSettings;

        Instance = this;
        Log.Info("DamageMeter: Plugin loaded.");
    }

    // ── Commands ──────────────────────────────────────────────────────────────
    private void OnMainCommand(string cmd, string args)     => _mainWindow.IsVisible     = !_mainWindow.IsVisible;
    private void OnHistoryCommand(string cmd, string args)  => _historyWindow.IsVisible  = !_historyWindow.IsVisible;
    private void OnSettingsCommand(string cmd, string args) => _settingsWindow.IsVisible = !_settingsWindow.IsVisible;

    private void OnOpenMainUi()   => _mainWindow.IsVisible     = true;
    private void OnOpenSettings() => _settingsWindow.IsVisible = true;

    // ── Draw loop ─────────────────────────────────────────────────────────────
    private void OnDraw()
    {
        _mainWindow.Draw();
        _historyWindow.Draw();
        _settingsWindow.Draw();
    }

    // ── Save config ───────────────────────────────────────────────────────────
    internal void SaveConfig() => PluginInterface.SavePluginConfig(Config);

    // ── Dispose ───────────────────────────────────────────────────────────────
    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw         -= OnDraw;
        PluginInterface.UiBuilder.OpenMainUi   -= OnOpenMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OnOpenSettings;

        CommandManager.RemoveHandler(CmdMain);
        CommandManager.RemoveHandler(CmdHistory);
        CommandManager.RemoveHandler(CmdSettings);

        _mainWindow.Dispose();
        _historyWindow.Dispose();
        _settingsWindow.Dispose();
        _statusApi.Dispose();

        _ipcBridge.Dispose();
        Tracker.Dispose();
        Instance = null;

        Log.Info("DamageMeter: Plugin unloaded.");
    }
}

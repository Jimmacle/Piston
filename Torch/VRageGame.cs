using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Havok;
using NLog;
using NLog.Fluent;
using Sandbox;
using Sandbox.Engine.Analytics;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Networking;
using Sandbox.Engine.Platform.VideoMode;
using Sandbox.Engine.Utils;
using Sandbox.Game;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using SpaceEngineers.Game;
using SpaceEngineers.Game.GUI;
using Steamworks;
using Torch.API;
using Torch.Utils;
using VRage;
using VRage.Audio;
using VRage.Dedicated;
using VRage.EOS;
using VRage.FileSystem;
using VRage.Game;
using VRage.Game.ObjectBuilder;
using VRage.Game.SessionComponents;
using VRage.GameServices;
using VRage.Mod.Io;
using VRage.Plugins;
using VRage.Scripting;
using VRage.Steam;
using VRage.Utils;
using VRageRender;
using MyRenderProfiler = VRage.Profiler.MyRenderProfiler;

namespace Torch
{
    public class VRageGame
    {
        private static readonly ILogger _log = LogManager.GetCurrentClassLogger();

#pragma warning disable 649
        [ReflectedGetter(Name = "m_plugins", Type = typeof(MyPlugins))]
        private static readonly Func<List<IPlugin>> _getVRagePluginList;

        [ReflectedGetter(Name = "Static", TypeName = "Sandbox.Game.Audio.MyMusicController, Sandbox.Game")]
        private static readonly Func<object> _getMusicControllerStatic;


        [ReflectedSetter(Name = "Static", TypeName = "Sandbox.Game.Audio.MyMusicController, Sandbox.Game")]
        private static readonly Action<object> _setMusicControllerStatic;


        [ReflectedMethod(Name = "Unload", TypeName = "Sandbox.Game.Audio.MyMusicController, Sandbox.Game")]
        private static readonly Action<object> _musicControllerUnload;

//        [ReflectedGetter(Name = "UpdateLayerDescriptors", Type = typeof(MyReplicationServer))]
//        private static readonly Func<MyReplicationServer.UpdateLayerDesc[]> _layerSettings;

#pragma warning restore 649

        private readonly TorchBase _torch;
        private readonly Action _tweakGameSettings;
        private readonly string _userDataPath;
        private readonly string _appName;
        private readonly uint _appSteamId;
        private readonly string[] _runArgs;
        private SpaceEngineersGame _game;
        private readonly Thread _updateThread;

        private bool _startGame = false;
        private readonly AutoResetEvent _commandChanged = new AutoResetEvent(false);
        private bool _destroyGame = false;

        private readonly AutoResetEvent _stateChangedEvent = new AutoResetEvent(false);
        private GameState _state;

        public enum GameState
        {
            Creating,
            Stopped,
            Running,
            Destroyed
        }

        internal VRageGame(TorchBase torch, Action tweakGameSettings, string appName, uint appSteamId,
            string userDataPath, string[] runArgs)
        {
            _torch = torch;
            _tweakGameSettings = tweakGameSettings;
            _appName = appName;
            _appSteamId = appSteamId;
            _userDataPath = userDataPath;
            _runArgs = runArgs;
            _updateThread = new Thread(Run);
            _updateThread.Start();
        }

        private void StateChange(GameState s)
        {
            if (_state == s)
                return;
            _state = s;
            _stateChangedEvent.Set();
        }

        private void Run()
        {
            StateChange(GameState.Creating);
            try
            {
                Create();
                _destroyGame = false;
                while (!_destroyGame)
                {
                    StateChange(GameState.Stopped);
                    _commandChanged.WaitOne();
                    if (_startGame)
                    {
                        _startGame = false;
                        DoStart();
                    }
                }
            }
            finally
            {
                Destroy();
                StateChange(GameState.Destroyed);
            }
        }

        private void Create()
        {
            bool dedicated = true;
            Environment.SetEnvironmentVariable("SteamAppId", _appSteamId.ToString());
            SpaceEngineersGame.SetupBasicGameInfo();
            SpaceEngineersGame.SetupPerGameSettings();
            MyFinalBuildConstants.APP_VERSION = MyPerGameSettings.BasicGameInfo.GameVersion;
            MySessionComponentExtDebug.ForceDisable = true;
            MyPerGameSettings.SendLogToKeen = false;
            // SpaceEngineersGame.SetupAnalytics();
            
            //not implemented by keen.. removed in cross-play update
            //MyVRage.Platform.InitScripting(MyVRageScripting.Create());
            _ = MyVRage.Platform.Scripting;
            
            MyFileSystem.ExePath = Path.GetDirectoryName(typeof(SpaceEngineersGame).Assembly.Location);

            _tweakGameSettings();

            MyFileSystem.Reset();
            MyInitializer.InvokeBeforeRun(_appSteamId, _appName, _userDataPath);

            _log.Info("Loading Dedicated Config");
            // object created in SpaceEngineersGame.SetupPerGameSettings()
            MySandboxGame.ConfigDedicated.Load();
            MyPlatformGameSettings.CONSOLE_COMPATIBLE = MySandboxGame.ConfigDedicated.ConsoleCompatibility;
            
            //Type.GetType("VRage.Steam.MySteamService, VRage.Steam").GetProperty("IsActive").GetSetMethod(true).Invoke(service, new object[] {SteamAPI.Init()});
            _log.Info("Initializing network services");

            var isEos = TorchBase.Instance.Config.UgcServiceType == UGCServiceType.EOS;

            if (isEos)
            {
                _log.Info("Running on Epic Online Services.");
                _log.Warn("Steam workshop will not work with current settings. Some functions might not work properly!");
            }

            var aggregator = new MyServerDiscoveryAggregator();
            MyServiceManager.Instance.AddService<IMyServerDiscovery>(aggregator);
            
            IMyGameService service;
            if (isEos)
            {
                service = MyEOSService.Create();

                MyEOSService.InitNetworking(dedicated,
                    "Space Engineers",
                    service,
                    "xyza7891A4WeGrpP85BTlBa3BSfUEABN",
                    "ZdHZVevSVfIajebTnTmh5MVi3KPHflszD9hJB7mRkgg",
                    "24b1cd652a18461fa9b3d533ac8d6b5b",
                    "1958fe26c66d4151a327ec162e4d49c8",
                    "07c169b3b641401496d352cad1c905d6",
                    "https://retail.epicgames.com/",
                    MyEOSService.CreatePlatform(),
                    MySandboxGame.ConfigDedicated.VerboseNetworkLogging,
                    Enumerable.Empty<string>(),
                    aggregator,
                    MyMultiplayer.Channels);

                var mockingInventory = new MyMockingInventory(service);
                MyServiceManager.Instance.AddService<IMyInventoryService>(mockingInventory);
            }
            else
            {
                service = MySteamGameService.Create(dedicated, _appSteamId);
                MyGameService.WorkshopService.AddAggregate(MySteamUgcService.Create(_appSteamId, service));
                MySteamGameService.InitNetworking(dedicated,
                    service,
                    "Space Engineers",
                    aggregator);
            }

            MyServiceManager.Instance.AddService(service);
            
            MyGameService.WorkshopService.AddAggregate(MyModIoService.Create(service, "spaceengineers", "264",
                "1fb4489996a5e8ffc6ec1135f9985b5b", "331", "f2b64abe55452252b030c48adc0c1f0e",
                MyPlatformGameSettings.UGC_TEST_ENVIRONMENT, true));
            
            if (!isEos && !MyGameService.HasGameServer)
            {
                _log.Warn("Network service is not running! Please reinstall dedicated server.");
                return;
            }
            
            _log.Info("Initializing services");
            MyServiceManager.Instance.AddService<IMyMicrophoneService>(new MyNullMicrophone());

            MyNetworkMonitor.Init();
            
            _log.Info("Services initialized");
            MySandboxGame.InitMultithreading();
            // MyInitializer.InitCheckSum();


            // Hook into the VRage plugin system for updates.
            _getVRagePluginList().Add(_torch);

            if (!MySandboxGame.IsReloading)
                MyFileSystem.InitUserSpecific(dedicated ? null : MyGameService.UserId.ToString());
            MySandboxGame.IsReloading = dedicated;
            
            // render init
            {
                IMyRender renderer = null;
                if (dedicated)
                {
                    renderer = new MyNullRender();
                }
                else
                {
                    MyPerformanceSettings preset = MyGuiScreenOptionsGraphics.GetPreset(MyRenderPresetEnum.NORMAL);
                    MyRenderProxy.Settings.User = MyVideoSettingsManager.GetGraphicsSettingsFromConfig(ref preset, false)
                        .PerformanceSettings.RenderSettings;
                    MyStringId graphicsRenderer = MySandboxGame.Config.GraphicsRenderer;
                    if (graphicsRenderer == MySandboxGame.DirectX11RendererKey)
                    {
                        renderer = new MyDX11Render(new MyRenderSettings?(MyRenderProxy.Settings));
                        if (!renderer.IsSupported)
                        {
                            MySandboxGame.Log.WriteLine(
                                "DirectX 11 renderer not supported. No renderer to revert back to.");
                            renderer = null;
                        }
                    }
                    if (renderer == null)
                    {
                        throw new MyRenderException(
                            "The current version of the game requires a Dx11 card. \\n For more information please see : http://blog.marekrosa.org/2016/02/space-engineers-news-full-source-code_26.html",
                            MyRenderExceptionEnum.GpuNotSupported);
                    }
                    MySandboxGame.Config.GraphicsRenderer = graphicsRenderer;
                }
                MyRenderProxy.Initialize(renderer);
                MyRenderProfiler.SetAutocommit(false);
                //This broke services?
                //MyRenderProfiler.InitMemoryHack("MainEntryPoint");
            }

            // Loads object builder serializers. Intuitive, right?
            _log.Info("Setting up serializers");
            MyPlugins.RegisterGameAssemblyFile(MyPerGameSettings.GameModAssembly);
            if (MyPerGameSettings.GameModBaseObjBuildersAssembly != null)
                MyPlugins.RegisterBaseGameObjectBuildersAssemblyFile(MyPerGameSettings.GameModBaseObjBuildersAssembly);
            MyPlugins.RegisterGameObjectBuildersAssemblyFile(MyPerGameSettings.GameModObjBuildersAssembly);
            MyPlugins.RegisterSandboxAssemblyFile(MyPerGameSettings.SandboxAssembly);
            MyPlugins.RegisterSandboxGameAssemblyFile(MyPerGameSettings.SandboxGameAssembly);
            //typeof(MySandboxGame).GetMethod("Preallocate", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            MyGlobalTypeMetadata.Static.Init(false);
        }

        private void Destroy()
        {
            _game.Dispose();
            _game = null;

            MyGameService.ShutDown();

            _getVRagePluginList().Remove(_torch);

            MyInitializer.InvokeAfterRun();
        }

        private void DoStart()
        {
            _game = new SpaceEngineersGame(_runArgs);

            if (MySandboxGame.FatalErrorDuringInit)
            {
                throw new InvalidOperationException("Failed to start sandbox game: see Keen log for details");
            }
            try
            {
                StateChange(GameState.Running);
                _game.Run();
            }
            finally
            {
                StateChange(GameState.Stopped);
            }
        }

        private void DoDisableAutoload()
        {
            if (MySandboxGame.ConfigDedicated is MyConfigDedicated<MyObjectBuilder_SessionSettings> config)
            {
                var tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
                Directory.CreateDirectory(tempDirectory);
                config.LoadWorld = null;
                config.PremadeCheckpointPath = tempDirectory;
            }
        }


#pragma warning disable 649
        [ReflectedMethod(Name = "StartServer")]
        private static Action<MySession, MyMultiplayerBase> _hostServerForSession;
#pragma warning restore 649

        private void DoLoadSession(string sessionPath)
        {
            if (!Path.IsPathRooted(sessionPath))
                sessionPath = Path.Combine(MyFileSystem.SavesPath, sessionPath);

            if (!Sandbox.Engine.Platform.Game.IsDedicated)
            {
                MySessionLoader.LoadSingleplayerSession(sessionPath);
                return;
            }
            MyObjectBuilder_Checkpoint checkpoint = MyLocalCache.LoadCheckpoint(sessionPath, out ulong checkpointSize);
            /*if (MySession.IsCompatibleVersion(checkpoint))
            {
                var downloadResult = MyWorkshop.DownloadWorldModsBlocking(checkpoint.Mods.Select(b =>
                {
                    b.PublishedServiceName = ModItemUtils.GetDefaultServiceName(); 
                    return b;
                }).ToList(), null);
                if (downloadResult.Success)
                {
                    MyLog.Default.WriteLineAndConsole("Mods Downloaded");
                    // MySpaceAnalytics.Instance.SetEntry(MyGameEntryEnum.Load);
                    MySession.Load(sessionPath, checkpoint, checkpointSize);
                    _hostServerForSession(MySession.Static, MyMultiplayer.Static);
                }
                else
                {
                    MyLog.Default.WriteLineAndConsole("Unable to download mods");
                    MyLog.Default.WriteLineAndConsole("Missing Mods:");
                    downloadResult.MismatchMods?.ForEach(b => MyLog.Default.WriteLineAndConsole($"\t{b.Title} ({b.Id})"));
                }
            }
            else
                MyLog.Default.WriteLineAndConsole(MyTexts.Get(MyCommonTexts.DialogTextIncompatibleWorldVersion)
                    .ToString());*/
        }

        private void DoJoinSession(ulong lobbyId)
        {
            MyJoinGameHelper.JoinGame(lobbyId);
        }

        private void DoUnloadSession()
        {
            if (!Sandbox.Engine.Platform.Game.IsDedicated)
            {
                MyScreenManager.CloseAllScreensExcept(null);
                MyGuiSandbox.Update(16);
            }
            if (MySession.Static != null)
            {
                MySession.Static.Unload();
                MySession.Static = null;
            }
            {
                var musicCtl = _getMusicControllerStatic();
                if (musicCtl != null)
                {
                    _musicControllerUnload(musicCtl);
                    _setMusicControllerStatic(null);
                    MyAudio.Static.MusicAllowed = true;
                }
            }
            if (MyMultiplayer.Static != null)
            {
                MyMultiplayer.Static.Dispose();
            }
            if (!Sandbox.Engine.Platform.Game.IsDedicated)
            {
                MyGuiSandbox.AddScreen(MyGuiSandbox.CreateScreen(MyPerGameSettings.GUI.MainMenu));
            }
        }

        private void DoStop()
        {
            ParallelTasks.Parallel.Scheduler.WaitForTasksToFinish(TimeSpan.FromSeconds(10.0));
            MySandboxGame.Static.Exit();
        }

        /// <summary>
        /// Signals the game to stop itself.
        /// </summary>
        public void SignalStop()
        {
            _startGame = false;
            _game.Invoke(DoStop, $"{nameof(VRageGame)}::{nameof(SignalStop)}");
        }

        /// <summary>
        /// Signals the game to start itself
        /// </summary>
        public void SignalStart()
        {
            _startGame = true;
            _commandChanged.Set();
        }

        /// <summary>
        /// Signals the game to destroy itself
        /// </summary>
        public void SignalDestroy()
        {
            _destroyGame = true;
            SignalStop();
            _commandChanged.Set();
        }

        public Task LoadSession(string path)
        {
            return _torch.InvokeAsync(()=>DoLoadSession(path));
        }

        public Task JoinSession(ulong lobbyId)
        {
            return _torch.InvokeAsync(()=>DoJoinSession(lobbyId));
        }

        public Task UnloadSession()
        {
            return _torch.InvokeAsync(DoUnloadSession);
        }

        /// <summary>
        /// Waits for the game to transition to the given state
        /// </summary>
        /// <param name="state">State to transition to</param>
        /// <param name="timeout">Timeout</param>
        /// <returns></returns>
        public bool WaitFor(GameState state, TimeSpan? timeout = null)
        {
            // Kinda icky, but we can't block the update and expect the state to change.
            if (Thread.CurrentThread == _updateThread)
                return _state == state;

            DateTime? end = timeout.HasValue ? (DateTime?) (DateTime.Now + timeout.Value) : null;
            while (_state != state && (!end.HasValue || end > DateTime.Now + TimeSpan.FromSeconds(1)))
                if (end.HasValue)
                    _stateChangedEvent.WaitOne(end.Value - DateTime.Now);
                else
                    _stateChangedEvent.WaitOne();
            return _state == state;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using ProtoBuf.Meta;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Networking;
using Sandbox.Engine.Platform.VideoMode;
using Sandbox.Engine.Utils;
using Sandbox.Game;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using Sandbox.Graphics.GUI;
using Sandbox.ModAPI;
using SpaceEngineers.Game;
using SpaceEngineers.Game.GUI;
using Torch.API;
using Torch.API.Managers;
using Torch.API.ModAPI;
using Torch.API.Session;
using Torch.Commands;
using Torch.Event;
using Torch.Managers;
using Torch.Managers.ChatManager;
using Torch.Managers.PatchManager;
using Torch.Patches;
using Torch.Utils;
using Torch.Session;
using VRage;
using VRage.Collections;
using VRage.FileSystem;
using VRage.Game;
using VRage.Game.Common;
using VRage.Game.Components;
using VRage.Game.ObjectBuilder;
using VRage.Game.SessionComponents;
using VRage.GameServices;
using VRage.Library;
using VRage.ObjectBuilders;
using VRage.Platform.Windows;
using VRage.Plugins;
using VRage.Scripting;
using VRage.Steam;
using VRage.Utils;
using VRageRender;

[assembly: InternalsVisibleTo("Torch.Server")]

namespace Torch
{
    /// <summary>
    /// Base class for code shared between the Torch client and server.
    /// </summary>
    public abstract class TorchBase : ViewModel, ITorchBase, IPlugin
    {
        static TorchBase()
        {
            MyVRageWindows.Init("SpaceEngineersDedicated", MySandboxGame.Log, null, false);
            ReflectedManager.Process(typeof(TorchBase).Assembly);
            ReflectedManager.Process(typeof(ITorchBase).Assembly);
            PatchManager.AddPatchShim(typeof(GameStatePatchShim));
            PatchManager.AddPatchShim(typeof(GameAnalyticsPatch));
            PatchManager.AddPatchShim(typeof(KeenLogPatch));
            PatchManager.CommitInternal();
            RegisterCoreAssembly(typeof(ITorchBase).Assembly);
            RegisterCoreAssembly(typeof(TorchBase).Assembly);
            RegisterCoreAssembly(Assembly.GetEntryAssembly());
            //exceptions in English, please
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
        }

        /// <summary>
        /// Hack because *keen*.
        /// Use only if necessary, prefer dependency injection.
        /// </summary>
        [Obsolete("This is a hack, don't use it.")]
        public static ITorchBase Instance { get; private set; }

        /// <inheritdoc />
        public ITorchConfig Config { get; protected set; }

        /// <inheritdoc />
        public InformationalVersion TorchVersion { get; }

        /// <inheritdoc />
        public Version GameVersion { get; private set; }

        /// <inheritdoc />
        public string[] RunArgs { get; set; }

        /// <inheritdoc />
        [Obsolete("Use GetManager<T>() or the [Dependency] attribute.")]
        public IPluginManager Plugins { get; protected set; }

        /// <inheritdoc />
        public ITorchSession CurrentSession => Managers?.GetManager<ITorchSessionManager>()?.CurrentSession;

        /// <inheritdoc />
        public event Action SessionLoading;

        /// <inheritdoc />
        public event Action SessionLoaded;

        /// <inheritdoc />
        public event Action SessionUnloading;

        /// <inheritdoc />
        public event Action SessionUnloaded;

        /// <summary>
        /// Common log for the Torch instance.
        /// </summary>
        protected static Logger Log { get; } = LogManager.GetLogger("Torch");

        /// <inheritdoc/>
        public IDependencyManager Managers { get; }

        private bool _init;

        /// <summary>
        /// 
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if a TorchBase instance already exists.</exception>
        protected TorchBase()
        {
            RegisterCoreAssembly(GetType().Assembly);
            if (Instance != null)
                throw new InvalidOperationException("A TorchBase instance already exists.");

            Instance = this;

            var versionString = Assembly.GetEntryAssembly()
                                      .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                                      .InformationalVersion;
            
            if (!InformationalVersion.TryParse(versionString, out InformationalVersion version))
                throw new TypeLoadException("Unable to parse the Torch version from the assembly.");

            TorchVersion = version;

            RunArgs = new string[0];

            Managers = new DependencyManager();

            Plugins = new PluginManager(this);

            var sessionManager = new TorchSessionManager(this);
            sessionManager.AddFactory((x) => Sync.IsServer ? new ChatManagerServer(this) : new ChatManagerClient(this));
            sessionManager.AddFactory((x) => Sync.IsServer ? new CommandManager(this) : null);
            sessionManager.AddFactory((x) => new EntityManager(this));

            Managers.AddManager(sessionManager);
            Managers.AddManager(new PatchManager(this));
            Managers.AddManager(new FilesystemManager(this));
            Managers.AddManager(new UpdateManager(this));
            Managers.AddManager(new EventManager(this));
            Managers.AddManager(Plugins);
            TorchAPI.Instance = this;

            GameStateChanged += (game, state) =>
            {
                if (state == TorchGameState.Created)
                {
                    // If the attached assemblies change (MySandboxGame.ctor => MySandboxGame.ParseArgs => MyPlugins.RegisterFromArgs)
                    // attach assemblies to object factories again.
                    ObjectFactoryInitPatch.ForceRegisterAssemblies();
                    // safe to commit here; all important static ctors have run
                    PatchManager.CommitInternal();
                }
            };

            sessionManager.SessionStateChanged += (session, state) =>
            {
                switch (state)
                {
                    case TorchSessionState.Loading:
                        SessionLoading?.Invoke();
                        break;
                    case TorchSessionState.Loaded:
                        SessionLoaded?.Invoke();
                        break;
                    case TorchSessionState.Unloading:
                        SessionUnloading?.Invoke();
                        break;
                    case TorchSessionState.Unloaded:
                        SessionUnloaded?.Invoke();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(state), state, null);
                }
            };
        }

        [Obsolete("Prefer using Managers.GetManager for global managers")]
        public T GetManager<T>() where T : class, IManager
        {
            return Managers.GetManager<T>();
        }

        [Obsolete("Prefer using Managers.AddManager for global managers")]
        public bool AddManager<T>(T manager) where T : class, IManager
        {
            return Managers.AddManager(manager);
        }

        public bool IsOnGameThread()
        {
            return Thread.CurrentThread.ManagedThreadId == MySandboxGame.Static.UpdateThread.ManagedThreadId;
        }

        #region Game Actions

        /// <summary>
        /// Invokes an action on the game thread.
        /// </summary>
        /// <param name="action"></param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Invoke(Action action, [CallerMemberName] string caller = "")
        {
            MySandboxGame.Static.Invoke(action, caller);
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void InvokeBlocking(Action action, int timeoutMs = -1, [CallerMemberName] string caller = "")
        {
            if (Thread.CurrentThread == MySandboxGame.Static.UpdateThread)
            {
                Debug.Assert(false, $"{nameof(InvokeBlocking)} should not be called on the game thread.");
                // ReSharper disable once HeuristicUnreachableCode
                action.Invoke();
                return;
            }

            // ReSharper disable once ExplicitCallerInfoArgument
            Task task = InvokeAsync(action, caller);
            if (!task.Wait(timeoutMs))
                throw new TimeoutException("The game action timed out");
            if (task.IsFaulted && task.Exception != null)
                throw task.Exception;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public Task<T> InvokeAsync<T>(Func<T> action, [CallerMemberName] string caller = "")
        {
            var ctx = new TaskCompletionSource<T>();
            MySandboxGame.Static.Invoke(() =>
            {
                try
                {
                    ctx.SetResult(action.Invoke());
                }
                catch (Exception e)
                {
                    ctx.SetException(e);
                }
                finally
                {
                    Debug.Assert(ctx.Task.IsCompleted);
                }
            }, caller);
            return ctx.Task;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public Task InvokeAsync(Action action, [CallerMemberName] string caller = "")
        {
            var ctx = new TaskCompletionSource<bool>();
            MySandboxGame.Static.Invoke(() =>
            {
                try
                {
                    action.Invoke();
                    ctx.SetResult(true);
                }
                catch (Exception e)
                {
                    ctx.SetException(e);
                }
                finally
                {
                    Debug.Assert(ctx.Task.IsCompleted);
                }
            }, caller);
            return ctx.Task;
        }

#endregion

#region Torch Init/Destroy

        protected abstract uint SteamAppId { get; }
        protected abstract string SteamAppName { get; }

        /// <inheritdoc />
        public virtual void Init()
        {
            Debug.Assert(!_init, "Torch instance is already initialized.");
            SpaceEngineersGame.SetupBasicGameInfo();
            SpaceEngineersGame.SetupPerGameSettings();
            ObjectFactoryInitPatch.ForceRegisterAssemblies();

            Debug.Assert(MyPerGameSettings.BasicGameInfo.GameVersion != null, "MyPerGameSettings.BasicGameInfo.GameVersion != null");
            GameVersion = new MyVersion(MyPerGameSettings.BasicGameInfo.GameVersion.Value);

            try
            {
                Console.Title = $"{Config.InstanceName} - Torch {TorchVersion}, SE {GameVersion}";
            }
            catch
            {
                // Running without a console
            }

#if DEBUG
            Log.Info("DEBUG");
#else
            Log.Info("RELEASE");
#endif
            Log.Info($"Torch Version: {TorchVersion}");
            Log.Info($"Game Version: {GameVersion}");
            Log.Info($"Executing assembly: {Assembly.GetEntryAssembly().FullName}");
            Log.Info($"Executing directory: {AppDomain.CurrentDomain.BaseDirectory}");

            Managers.GetManager<PluginManager>().LoadPlugins();
            Game = new VRageGame(this, TweakGameSettings, SteamAppName, SteamAppId, Config.InstancePath, RunArgs);
            if (!Game.WaitFor(VRageGame.GameState.Stopped))
                Log.Warn("Failed to wait for game to be initialized");
            Managers.Attach();
            _init = true;

            if (GameState >= TorchGameState.Created && GameState < TorchGameState.Unloading)
                // safe to commit here; all important static ctors have run
                PatchManager.CommitInternal();
        }

        /// <summary>
        /// Dispose callback for VRage plugin.  Do not use.
        /// </summary>
        [Obsolete("Do not use; only there for VRage capability")]
        public void Dispose()
        {
        }

        /// <inheritdoc />
        public virtual void Destroy()
        {
            Managers.Detach();
            Game.SignalDestroy();
            if (!Game.WaitFor(VRageGame.GameState.Destroyed))
                Log.Warn("Failed to wait for the game to be destroyed");
            Game = null;
        }

#endregion

        protected VRageGame Game { get; private set; }

        /// <summary>
        /// Called after the basic game information is filled, but before the game is created.
        /// </summary>
        protected virtual void TweakGameSettings()
        {
        }


        private int _inProgressSaves = 0;
        /// <inheritdoc/>
        public virtual Task<GameSaveResult> Save(int timeoutMs = -1, bool exclusive = false)
        {
            if (exclusive)
            {
                if (MyAsyncSaving.InProgress || _inProgressSaves > 0)
                {
                    Log.Error("Failed to save game, game is already saving");
                    return null;
                }
            }
            
            Interlocked.Increment(ref _inProgressSaves);
            return TorchAsyncSaving.Save(this, timeoutMs).ContinueWith((task, torchO) =>
            {
                var torch = (TorchBase) torchO;
                Interlocked.Decrement(ref torch._inProgressSaves);
                if (task.IsFaulted)
                {
                    Log.Error(task.Exception, "Failed to save game");
                    return GameSaveResult.UnknownError;
                }
                if (task.Result != GameSaveResult.Success)
                    Log.Error($"Failed to save game: {task.Result}");
                else
                    Log.Info("Saved game");
                return task.Result;
            }, this, TaskContinuationOptions.RunContinuationsAsynchronously);
        }

        /// <inheritdoc/> 
        public virtual void Start()
        {
            Game.SignalStart();
            if (!Game.WaitFor(VRageGame.GameState.Running))
                Log.Warn("Failed to wait for the game to be started");
            Invoke(() => Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US"));
        }

        /// <inheritdoc />
        public virtual void Stop()
        {
            LogManager.Flush();
            Game.SignalStop();
            if (!Game.WaitFor(VRageGame.GameState.Stopped))
                Log.Warn("Failed to wait for the game to be stopped");
        }

        /// <inheritdoc />
        public abstract void Restart(bool save = true);

        /// <inheritdoc />
        public virtual void Init(object gameInstance)
        {
        }

        /// <inheritdoc />
        public virtual void Update()
        {
            Managers.GetManager<IPluginManager>().UpdatePlugins();
        }


        private TorchGameState _gameState = TorchGameState.Unloaded;

        /// <inheritdoc/>
        public TorchGameState GameState
        {
            get => _gameState;
            internal set
            {
                _gameState = value;
                GameStateChanged?.Invoke(MySandboxGame.Static, _gameState);
            }
        }

        /// <inheritdoc/>
        public event TorchGameStateChangedDel GameStateChanged;

        private static readonly HashSet<Assembly> _registeredCoreAssemblies = new HashSet<Assembly>();

        /// <summary>
        /// Registers a core (Torch) assembly with the system, including its
        /// <see cref="EventManager"/> shims, <see cref="PatchManager"/> shims, and <see cref="ReflectedManager"/> components.
        /// </summary>
        /// <param name="asm">Assembly to register</param>
        internal static void RegisterCoreAssembly(Assembly asm)
        {
            lock (_registeredCoreAssemblies)
                if (_registeredCoreAssemblies.Add(asm))
                {
                    ReflectedManager.Process(asm);
                    EventManager.AddDispatchShims(asm);
                    PatchManager.AddPatchShims(asm);
                }
        }

        private static readonly HashSet<Assembly> _registeredAuxAssemblies = new HashSet<Assembly>();
        private static readonly TimeSpan _gameStateChangeTimeout = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Registers an auxillary (plugin) assembly with the system, including its
        /// <see cref="ReflectedManager"/> related components.
        /// </summary>
        /// <param name="asm">Assembly to register</param>
        internal static void RegisterAuxAssembly(Assembly asm)
        {
            lock (_registeredAuxAssemblies)
                if (_registeredAuxAssemblies.Add(asm))
                {
                    ReflectedManager.Process(asm);
                    PatchManager.AddPatchShims(asm);
                }
        }
    }
}
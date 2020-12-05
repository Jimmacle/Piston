using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using Torch.API;
using Torch.API.Plugins;

namespace Torch
{
    public abstract class TorchPluginBase : ITorchPlugin
    {
        public string StoragePath { get; internal set; }
        public PluginManifest Manifest { get; internal set; }
        public Guid Id => Manifest.Guid;
        public string Version => Manifest.Version;
        public string Name => Manifest.Name;
        public ITorchBase Torch { get; internal set; }
        public event Action<PluginState, PluginState> StateChanged;

        public virtual void Init(ITorchBase torch)
        {
            Torch = torch;
        }

        public virtual void Update() { }

        private PluginState _state;

        public PluginState State
        {
            get => _state;
            internal set
            {
                PluginState oldState = _state;
                _state = value;
                OnStateChange(oldState, _state);
            }
        }

        private void OnStateChange(PluginState oldState, PluginState newState)
        {
            StateChanged?.Invoke(oldState, newState);
            switch (newState)
            {
                case PluginState.NotInitialized:
                case PluginState.DisabledError:
                case PluginState.DisabledUser:
                case PluginState.UpdateRequired:
                case PluginState.NotInstalled:
                case PluginState.MissingDependency:
                case PluginState.Enabled:
                    break;

                case PluginState.DisableRequested:
                    Torch.Config.Plugins.Remove(Id);
                    Torch.Config.DisabledPlugins.Add(Id);
                    Torch.Config.Save();
                    break;
                case PluginState.EnableRequested:
                    Torch.Config.DisabledPlugins.Remove(Id);
                    Torch.Config.Plugins.Add(Id);
                    Torch.Config.Save();
                    break;
                case PluginState.UninstallRequested:
                    Torch.Config.Plugins.Remove(Id);
                    Torch.Config.DisabledPlugins.Remove(Id);
                    Torch.Config.Save();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(newState), newState, null);
            }
        }

        public virtual void Dispose() { }

    }
}

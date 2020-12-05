using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NLog;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using Torch.API;
using Torch.API.Managers;
using Torch.API.Plugins;
using Torch.Managers;
using Torch.Utils;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Network;

namespace Torch.Commands
{
    public delegate void CommandExecutingDel(Command command, IMyPlayer player, bool hasPermission, ref bool? hasPermissionOverride);
    
    public class CommandManager : Manager
    {
        public char Prefix { get; set; }
        public CommandTree Commands { get; set; } = new CommandTree();
        public event CommandExecutingDel OnCommandExecuting;
        private Logger _log = LogManager.GetCurrentClassLogger();
        [Dependency]
        private IChatManagerServer _chatManager;

        public CommandManager(ITorchBase torch, char prefix = '!') : base(torch)
        {
            Prefix = prefix;
        }

        public override void Attach()
        {
            RegisterCommandModule(typeof(TorchCommands));
            _chatManager.MessageProcessing += HandleCommand;
        }

        public bool HasPermission(ulong steamId, Command command)
        {
            var userLevel = MySession.Static.GetUserPromoteLevel(steamId);
            return userLevel >= command.MinimumPromoteLevel;
        }

        public bool IsCommand(string command)
        {
            return !string.IsNullOrEmpty(command) && command[0] == Prefix;
        }

        public void RegisterCommandModule(Type moduleType, ITorchPlugin plugin = null)
        {
            if (!moduleType.IsSubclassOf(typeof(CommandModule)))
                return;

            foreach (var method in moduleType.GetMethods())
            {
                var commandAttrib = method.GetCustomAttribute<CommandAttribute>();
                if (commandAttrib == null)
                    continue;

                var command = new Command(plugin, method);
                var cmdPath = string.Join(".", command.Path);
                _log.Info($"Registering command '{cmdPath}'");

                if (!Commands.AddCommand(command))
                    _log.Error($"Command path {cmdPath} is already registered.");
            }
        }

        public void UnregisterPluginCommands(ITorchPlugin plugin)
        {
            // TODO
        }

        public void RegisterPluginCommands(ITorchPlugin plugin)
        {
            if (plugin.State != PluginState.Enabled)
                return;

            var assembly = plugin.GetType().Assembly;
            foreach (var type in assembly.ExportedTypes)
            {
                RegisterCommandModule(type, plugin);
            }
        }

        private List<TorchChatMessage> HandleCommandFromServerInternal(string message, Action<TorchChatMessage> subscriber = null)
        {
            var cmdText = new string(message.Skip(1).ToArray());
            var command = Commands.GetCommand(cmdText, out string argText);
            if (command == null)
                return null;
            var cmdPath = string.Join(".", command.Path);

            var splitArgs = Regex.Matches(argText, "(\"[^\"]+\"|\\S+)").Cast<Match>().Select(x => x.ToString().Replace("\"", "")).ToList();
            _log.Trace($"Invoking {cmdPath} for server.");
            var context = new ConsoleCommandContext(Torch, command.Plugin, Sync.MyId, argText, splitArgs);
            if (subscriber != null)
                context.OnResponse += subscriber;
            if (command.TryInvoke(context))
                _log.Info($"Server ran command '{message}'");
            else
                context.Respond($"Invalid Syntax: {command.SyntaxHelp}");
            return context.Responses;
        }

        /// <summary>
        /// Invokes the given command string as the server, subscribing to responses using the given callback.
        /// </summary>
        /// <returns>true if the command was run, false if not</returns>
        public bool HandleCommandFromServer(string message, Action<TorchChatMessage> subscriber)
        {
            return HandleCommandFromServerInternal(message, subscriber) != null;
        }

        public List<TorchChatMessage> HandleCommandFromServer(string message)
        {
            return HandleCommandFromServerInternal(message, null);
        }

        public void HandleCommand(TorchChatMessage msg, ref bool consumed)
        {
            if (msg.AuthorSteamId.HasValue)
                HandleCommand(msg.Message, msg.AuthorSteamId.Value, ref consumed);
        }

        public void HandleCommand(string message, ulong steamId, ref bool consumed, bool serverConsole = false)
        {
            if (message.Length < 1 || message[0] != Prefix)
                return;

            consumed = true;

            var player = Torch.CurrentSession.Managers.GetManager<IMultiplayerManagerBase>().GetPlayerBySteamId(steamId);
            if (player == null)
            {
                _log.Error($"Command {message} invoked by nonexistant player");
                return;
            }

            var cmdText = new string(message.Skip(1).ToArray());
            var command = Commands.GetCommand(cmdText, out string argText);

            if (command != null)
            {
                var cmdPath = string.Join(".", command.Path);

                var defaultPermission = HasPermission(steamId, command);
                bool? cancel = null;
                OnCommandExecuting?.Invoke(command, player, defaultPermission, ref cancel);

                if (cancel.HasValue)
                {
                    if (!cancel.Value)
                        return;
                }
                else
                {
                    if (!defaultPermission)
                    {
                        _log.Info($"{player.DisplayName} tried to use command {cmdPath} without permission");
                        _chatManager.SendMessageAsOther(null, $"You need to be a {command.MinimumPromoteLevel} or higher to use that command.", targetSteamId: steamId);
                        return;
                    }
                }

                var splitArgs = Regex.Matches(argText, "(\"[^\"]+\"|\\S+)").Cast<Match>().Select(x => x.ToString().Replace("\"", "")).ToList();
                _log.Trace($"Invoking {cmdPath} for player {player.DisplayName}");
                var context = new CommandContext(Torch, command.Plugin, steamId, argText, splitArgs);
                Torch.Invoke(() =>
                {
                    if (command.TryInvoke(context))
                        _log.Info($"Player {player.DisplayName} ran command '{message}'");
                    else
                        context.Respond($"Invalid Syntax: {command.SyntaxHelp}");
                });
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using NLog;
using NLog.Fluent;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Engine.Networking;
using Sandbox.Game.Gui;
using Sandbox.Game.World;
using Steamworks;
using Torch.API;
using Torch.API.Managers;
using Torch.Managers;
using Torch.Utils;
using Torch.ViewModels;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.GameServices;
using VRage.Network;
using VRage.Steam;

namespace Torch.Server.Managers
{
    public class MultiplayerManagerDedicated : MultiplayerManagerBase, IMultiplayerManagerServer
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

#pragma warning disable 649
        [ReflectedGetter(Name = "m_members")]
        private static Func<MyDedicatedServerBase, List<ulong>> _members;

        [ReflectedGetter(Name = "m_waitingForGroup")]
        private static Func<MyDedicatedServerBase, HashSet<ulong>> _waitingForGroup;
#pragma warning restore 649

        /// <inheritdoc />
        public IReadOnlyList<ulong> BannedPlayers => MySandboxGame.ConfigDedicated.Banned;

        private Dictionary<ulong, ulong> _gameOwnerIds = new Dictionary<ulong, ulong>();

        [Dependency]
        private InstanceManager _instanceManager;

        /// <inheritdoc />
        public MultiplayerManagerDedicated(ITorchBase torch) : base(torch)
        {
        }

        /// <inheritdoc />
        public void KickPlayer(ulong steamId) => Torch.Invoke(() => MyMultiplayer.Static.KickClient(steamId));

        /// <inheritdoc />
        public void BanPlayer(ulong steamId, bool banned = true)
        {
            Torch.Invoke(() =>
            {
                MyMultiplayer.Static.BanClient(steamId, banned);
            });
        }

        /// <inheritdoc />
        public void PromoteUser(ulong steamId)
        {
            Torch.Invoke(() =>
            {
                var p = MySession.Static.GetUserPromoteLevel(steamId);
                if (p < MyPromoteLevel.Admin) //cannot promote to owner by design
                    //MySession.Static.SetUserPromoteLevel(steamId, p + 1);
                    MyGuiScreenPlayers.PromoteImplementation(steamId, true);
            });
        }

        /// <inheritdoc />
        public void DemoteUser(ulong steamId)
        {
            Torch.Invoke(() =>
            {
                var p = MySession.Static.GetUserPromoteLevel(steamId);
                if (p > MyPromoteLevel.None && p < MyPromoteLevel.Owner) //owner cannot be demoted by design
                    //MySession.Static.SetUserPromoteLevel(steamId, p - 1);
                    MyGuiScreenPlayers.PromoteImplementation(steamId, false);
            });
        }

        /// <inheritdoc />
        public MyPromoteLevel GetUserPromoteLevel(ulong steamId)
        {
            return MySession.Static.GetUserPromoteLevel(steamId);
        }

        internal void RaiseClientBanned(ulong user, bool banned)
        {
            PlayerBanned?.Invoke(user, banned);
            Torch.Invoke(() =>
                         {
                             if(_gameOwnerIds.TryGetValue(user, out ulong owner))
                                 MyMultiplayer.Static.BanClient(owner, banned);
                         });
        }

        internal void RaiseClientKicked(ulong user)
        {
            PlayerKicked?.Invoke(user);
        }

        /// <inheritdoc />
        public bool IsBanned(ulong steamId) => _isClientBanned.Invoke(MyMultiplayer.Static, steamId) ||
                                               MySandboxGame.ConfigDedicated.Banned.Contains(steamId);



        public bool IsProfiling(ulong steamId) => _Profiling.Invoke((MyDedicatedServerBase)MyMultiplayer.Static, steamId);

        /// <inheritdoc />
        public event Action<ulong> PlayerKicked;

        /// <inheritdoc />
        public event Action<ulong, bool> PlayerBanned;

        /// <inheritdoc />
        public event Action<ulong, MyPromoteLevel> PlayerPromoted;

        internal void RaisePromoteChanged(ulong steamId, MyPromoteLevel level)
        {
            PlayerPromoted?.Invoke(steamId, level);
        }

        /// <inheritdoc/>
        public override void Attach()
        {
            base.Attach();
            if (Torch.Config.UgcServiceType == UGCServiceType.Steam)
            {
                _gameServerValidateAuthTicketReplacer = _gameServerValidateAuthTicketFactory.Invoke();
                _gameServerUserGroupStatusReplacer = _gameServerUserGroupStatusFactory.Invoke();
            }
            else
            {
                _gameServerValidateAuthTicketReplacer = _eosServerValidateAuthTicketFactory.Invoke();
                _gameServerUserGroupStatusReplacer = _eosServerUserGroupStatusFactory.Invoke();
            }
            _gameServerValidateAuthTicketReplacer.Replace(
                new Action<ulong, JoinResult, ulong, string>(ValidateAuthTicketResponse), MyGameService.GameServer);
            _gameServerUserGroupStatusReplacer.Replace(new Action<ulong, ulong, bool, bool>(UserGroupStatusResponse),
                MyGameService.GameServer);
            _log.Info("Inserted authentication intercept");
        }

        /// <inheritdoc/>
        public override void Detach()
        {
            if (_gameServerValidateAuthTicketReplacer != null && _gameServerValidateAuthTicketReplacer.Replaced)
                _gameServerValidateAuthTicketReplacer.Restore(MyGameService.GameServer);
            if (_gameServerUserGroupStatusReplacer != null && _gameServerUserGroupStatusReplacer.Replaced)
                _gameServerUserGroupStatusReplacer.Restore(MyGameService.GameServer);
            _log.Info("Removed authentication intercept");
            base.Detach();
        }


#pragma warning disable 649
        [ReflectedEventReplace("VRage.Steam.MySteamGameServer, VRage.Steam", "ValidateAuthTicketResponse",
            typeof(MyDedicatedServerBase), "GameServer_ValidateAuthTicketResponse")]
        private static Func<ReflectedEventReplacer> _gameServerValidateAuthTicketFactory;

        [ReflectedEventReplace("VRage.Steam.MySteamGameServer, VRage.Steam", "UserGroupStatusResponse",
            typeof(MyDedicatedServerBase), "GameServer_UserGroupStatus")]
        private static Func<ReflectedEventReplacer> _gameServerUserGroupStatusFactory;
        
        [ReflectedEventReplace("VRage.EOS.MyEOSGameServer, VRage.EOS", "ValidateAuthTicketResponse",
            typeof(MyDedicatedServerBase), "GameServer_ValidateAuthTicketResponse")]
        private static Func<ReflectedEventReplacer> _eosServerValidateAuthTicketFactory;

        [ReflectedEventReplace("VRage.EOS.MyEOSGameServer, VRage.EOS", "UserGroupStatusResponse",
            typeof(MyDedicatedServerBase), "GameServer_UserGroupStatus")]
        private static Func<ReflectedEventReplacer> _eosServerUserGroupStatusFactory;

        private ReflectedEventReplacer _gameServerValidateAuthTicketReplacer;
        private ReflectedEventReplacer _gameServerUserGroupStatusReplacer;
#pragma warning restore 649

        #region CustomAuth

#pragma warning disable 649
        [ReflectedStaticMethod(Type = typeof(MyDedicatedServerBase), Name = "ConvertSteamIDFrom64")]
        private static Func<ulong, string> _convertSteamIDFrom64;

        [ReflectedStaticMethod(Type = typeof(MyGameService), Name = "GetServerAccountType")]
        private static Func<ulong, MyGameServiceAccountType> _getServerAccountType;

        [ReflectedMethod(Name = "ClientIsProfiling")]
        private static Func<MyDedicatedServerBase, ulong, bool> _Profiling;

        [ReflectedMethod(Name = "UserAccepted")]
        private static Action<MyDedicatedServerBase, ulong> _userAcceptedImpl;

        [ReflectedMethod(Name = "UserRejected")]
        private static Action<MyDedicatedServerBase, ulong, JoinResult> _userRejected;

        [ReflectedMethod(Name = "IsClientBanned")]
        private static Func<MyMultiplayerBase, ulong, bool> _isClientBanned;

        [ReflectedMethod(Name = "IsClientKicked")]
        private static Func<MyMultiplayerBase, ulong, bool> _isClientKicked;

        [ReflectedMethod(Name = "RaiseClientKicked")]
        private static Action<MyMultiplayerBase, ulong> _raiseClientKicked;
#pragma warning restore 649

        private const int _waitListSize = 32;
        private readonly List<WaitingForGroup> _waitingForGroupLocal = new List<WaitingForGroup>(_waitListSize);

        private struct WaitingForGroup
        {
            public readonly ulong SteamId;
            public readonly JoinResult Response;
            public readonly ulong SteamOwner;

            public WaitingForGroup(ulong id, JoinResult response, ulong owner)
            {
                SteamId = id;
                Response = response;
                SteamOwner = owner;
            }
        }

        //Largely copied from SE
        private void ValidateAuthTicketResponse(ulong steamId, JoinResult response, ulong steamOwner, string serviceName)
        {           
            var state = new MyP2PSessionState();
            Sandbox.Engine.Networking.MyGameService.Peer2Peer.GetSessionState(steamId, ref state);
            var ip = new IPAddress(BitConverter.GetBytes(state.RemoteIP).Reverse().ToArray());

            Torch.CurrentSession.KeenSession.PromotedUsers.TryGetValue(steamId, out MyPromoteLevel promoteLevel);

            _log.Debug($"ValidateAuthTicketResponse(user={steamId}, response={response}, owner={steamOwner}, permissions={promoteLevel})");

            _log.Info($"Connection attempt by {steamId} from {ip}");

            if (IsProfiling(steamId))
            {
                _log.Warn($"Rejecting user {steamId} for using Profiler/ModSDK!");
                UserRejected(steamId, JoinResult.ProfilingNotAllowed);
            }
            
            if (Players.ContainsKey(steamId))
            {
                _log.Warn($"Player {steamId} has already joined!");
                UserRejected(steamId, JoinResult.AlreadyJoined);
            }
            else if (Torch.CurrentSession.KeenSession.OnlineMode == MyOnlineModeEnum.OFFLINE &&
                     promoteLevel < MyPromoteLevel.Admin)
            {
                _log.Warn($"Rejecting user {steamId}, world is set to offline and user is not admin.");
                UserRejected(steamId, JoinResult.TicketCanceled);
            }
            else if (MySandboxGame.ConfigDedicated.GroupID == 0uL)
                RunEvent(new ValidateAuthTicketEvent(steamId, steamOwner, response, 0, true, false));
            else if (_getServerAccountType(MySandboxGame.ConfigDedicated.GroupID) != MyGameServiceAccountType.Clan)
                UserRejected(steamId, JoinResult.GroupIdInvalid);
            else if (MyGameService.GameServer.RequestGroupStatus(steamId, MySandboxGame.ConfigDedicated.GroupID))
                lock (_waitingForGroupLocal)
                {
                    if (_waitingForGroupLocal.Count >= _waitListSize)
                        _waitingForGroupLocal.RemoveAt(0);
                    _waitingForGroupLocal.Add(new WaitingForGroup(steamId, response, steamOwner));
                }
            else
                UserRejected(steamId, JoinResult.SteamServersOffline);
        }

        private void RunEvent(ValidateAuthTicketEvent info)
        {
            JoinResult internalAuth;


            if (IsBanned(info.SteamOwner) || IsBanned(info.SteamID))
                internalAuth = JoinResult.BannedByAdmins;
            else if (_isClientKicked(MyMultiplayer.Static, info.SteamID) ||
                     _isClientKicked(MyMultiplayer.Static, info.SteamOwner))
                internalAuth = JoinResult.KickedRecently;
            else if (info.SteamResponse == JoinResult.OK)
            {
                var config = (TorchConfig) Torch.Config;
                if (config.EnableWhitelist && !config.Whitelist.Contains(info.SteamID))
                {
                    _log.Warn($"Rejecting user {info.SteamID} because they are not whitelisted in Torch.cfg.");
                    internalAuth = JoinResult.NotInGroup;
                }
                else if (MySandboxGame.ConfigDedicated.Reserved.Contains(info.SteamID))
                    internalAuth = JoinResult.OK;
                //Admins can bypass member limit
                else if (MySandboxGame.ConfigDedicated.Administrators.Contains(info.SteamID.ToString()) ||
                         MySandboxGame.ConfigDedicated.Administrators.Contains(_convertSteamIDFrom64(info.SteamID)))
                    internalAuth = JoinResult.OK;
                //Server counts as a client, so subtract 1 from MemberCount
                else if (MyMultiplayer.Static.MemberLimit > 0 &&
                         MyMultiplayer.Static.MemberCount - 1 >= MyMultiplayer.Static.MemberLimit)
                    internalAuth = JoinResult.ServerFull;
                else if (MySandboxGame.ConfigDedicated.GroupID == 0uL)
                    internalAuth = JoinResult.OK;
                else
                {
                    if (MySandboxGame.ConfigDedicated.GroupID == info.Group && (info.Member || info.Officer))
                        internalAuth = JoinResult.OK;
                    else
                        internalAuth = JoinResult.NotInGroup;
                }
            }
            else
                internalAuth = info.SteamResponse;

            info.FutureVerdict = Task.FromResult(internalAuth);

            MultiplayerManagerDedicatedEventShim.RaiseValidateAuthTicket(ref info);

            info.FutureVerdict.ContinueWith((task) =>
            {
                JoinResult verdict;
                if (task.IsFaulted)
                {
                    _log.Error(task.Exception, $"Future validation verdict faulted");
                    verdict = JoinResult.TicketCanceled;
                }
                else
                    verdict = task.Result;

                Torch.Invoke(() => { CommitVerdict(info.SteamID, verdict); });
            });
        }

        private void CommitVerdict(ulong steamId, JoinResult verdict)
        {
            if (verdict == JoinResult.OK)
                UserAccepted(steamId);
            else
                UserRejected(steamId, verdict);
        }

        private void UserGroupStatusResponse(ulong userId, ulong groupId, bool member, bool officer)
        {
            lock (_waitingForGroupLocal)
                for (var j = 0; j < _waitingForGroupLocal.Count; j++)
                {
                    var wait = _waitingForGroupLocal[j];
                    if (wait.SteamId == userId)
                    {
                        RunEvent(new ValidateAuthTicketEvent(wait.SteamId, wait.SteamOwner, wait.Response, groupId,
                            member, officer));
                        _waitingForGroupLocal.RemoveAt(j);
                        break;
                    }
                }
        }

        private void UserRejected(ulong steamId, JoinResult reason)
        {
            _userRejected.Invoke((MyDedicatedServerBase) MyMultiplayer.Static, steamId, reason);
        }

        private void UserAccepted(ulong steamId)
        {
            _userAcceptedImpl.Invoke((MyDedicatedServerBase) MyMultiplayer.Static, steamId);
            base.RaiseClientJoined(steamId);
        }

        #endregion
    }
}

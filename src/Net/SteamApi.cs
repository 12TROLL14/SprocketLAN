using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace SprocketMP.Net
{
    /// <summary>
    /// Minimal binding to Valve's flat Steam API (steam_api64.dll from the Steamworks SDK, 1.58+), loaded from the
    /// plugin folder at run time. Only what the prototype needs: init with manual callback dispatch, user / friends,
    /// rich presence + invites, SteamNetworkingMessages (P2P through Steam's relays) and the relay status.
    /// </summary>
    public static unsafe class SteamApi
    {
        static IntPtr _lib;
        static readonly System.Collections.Generic.List<string> _missing = new System.Collections.Generic.List<string>();
        /// <summary>A Steam function, or null (and noted as missing) if this steam_api64.dll doesn't have it.</summary>
        static T Fn<T>(string name) where T : Delegate
        {
            if (NativeLibrary.TryGetExport(_lib, name, out var p) && p != IntPtr.Zero) return Marshal.GetDelegateForFunctionPointer<T>(p);
            _missing.Add(name); return null;
        }
        /// <summary>The DLL's file version (e.g. "9.85.43.97"), "" if unknown.</summary>
        public static string DllVersion = "";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_InitFlat(byte* err);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_Void();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool D_SetCfgInt(IntPtr self, int key, int value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool D_Bool();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_Int();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_Ptr();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_PipeVoid(int pipe);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool D_NextCb(int pipe, CallbackMsg* msg);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong D_SelfU64(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_SelfStr(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_SelfU64Str(IntPtr self, ulong id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool D_SetRp(IntPtr self, byte* key, byte* val);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_SelfVoid(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_SelfBytes(IntPtr self, byte* s);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool D_Invite(IntPtr self, ulong friend, byte* connect);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_FriendCount(IntPtr self, int flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong D_FriendByIndex(IntPtr self, int i, int flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool D_GamePlayed(IntPtr self, ulong friend, FriendGameInfo* info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_Send(IntPtr self, byte* identity, byte* data, uint cb, int flags, int channel);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_Recv(IntPtr self, int channel, IntPtr* msgs, int max);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool D_Accept(IntPtr self, byte* identity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_SessInfo(IntPtr self, byte* identity, byte* info, byte* status);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_RelayStatus(IntPtr self, IntPtr details);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_IdSet(byte* identity, ulong id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong D_IdGet(byte* identity);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_MsgRelease(IntPtr msg);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate uint D_AppId(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_SelfBuf(IntPtr self, byte* buf);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_SelfIntRet(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_SetDebug(IntPtr self, int level, IntPtr fn);

        [StructLayout(LayoutKind.Sequential, Pack = 8)] public struct CallbackMsg { public int User; public int Callback; public byte* Param; public int ParamSize; }
        [StructLayout(LayoutKind.Sequential, Pack = 8)] public struct FriendGameInfo { public ulong GameId; public uint Ip; public ushort GamePort, QueryPort; public ulong Lobby; }

        static D_InitFlat _init; static D_Void _shutdown; static D_Bool _running; static D_Ptr _friendsI, _userI, _msgsI, _utilsI, _netUtilsI;
        static D_Void _mdInit; static D_Int _pipe; static D_PipeVoid _runFrame, _freeLast; static D_NextCb _nextCb;
        static D_SelfU64 _getSteamId; static D_SelfStr _persona; static D_SelfU64Str _friendName; static D_SetRp _setRp; static D_SelfVoid _clearRp;
        static D_SelfBytes _inviteDialog; static D_Invite _invite; static D_FriendCount _friendCount; static D_FriendByIndex _friendByIndex; static D_GamePlayed _gamePlayed;
        static D_Send _send; static D_Recv _recv; static D_Accept _accept; static D_SessInfo _sessInfo; static D_SelfVoid _initRelay; static D_RelayStatus _relayStatus;
        // ---- normal dispatch: Steam runs its own internal callbacks in RunCallbacks; ours come through
        //      a fake CCallbackBase (registered with SteamAPI_RegisterCallback) and the networking global callbacks
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_RegCb(IntPtr cb, int id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate bool D_SetGlobalCb(IntPtr self, IntPtr fn);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_RunCallResult(IntPtr self, IntPtr param, byte ioFailure, ulong call);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_RunCallback(IntPtr self, IntPtr param);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_CbSize(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_ParamCb(IntPtr param);
        static D_Void _runCallbacks; static D_RegCb _regCb; static D_SetGlobalCb _setSessReq, _setSessFail;
        public static bool ManualDispatch;
        static Action<int, IntPtr, int> _handler;
        sealed class Registered { public IntPtr Obj, VTable; public D_RunCallResult A; public D_RunCallback B; public D_CbSize C; public int Id, Size; }
        static readonly System.Collections.Generic.List<Registered> _registered = new System.Collections.Generic.List<Registered>();
        static D_ParamCb _sessReqCb, _sessFailCb;

        static void Register(int id, int size)
        {
            var r = new Registered { Id = id, Size = size };
            r.A = (self, param, io, call) => { try { _handler?.Invoke(id, param, size); } catch { } };
            r.B = (self, param) => { try { _handler?.Invoke(id, param, size); } catch { } };
            r.C = self => size;
            // MSVC lays the two Run overloads out in reverse order: Run(param, ioFailure, call), Run(param), GetCallbackSizeBytes
            r.VTable = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(r.VTable, 0, Marshal.GetFunctionPointerForDelegate(r.A));
            Marshal.WriteIntPtr(r.VTable, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(r.B));
            Marshal.WriteIntPtr(r.VTable, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(r.C));
            r.Obj = Marshal.AllocHGlobal(32); for (int i = 0; i < 32; i++) Marshal.WriteByte(r.Obj, i, 0);
            Marshal.WriteIntPtr(r.Obj, 0, r.VTable);          // vtable
            Marshal.WriteByte(r.Obj, 8, 0);                   // m_nCallbackFlags
            Marshal.WriteInt32(r.Obj, 12, id);                // m_iCallback
            _regCb(r.Obj, id);
            _registered.Add(r);   // kept alive (delegates + memory) for the whole session
        }

        // ---- matchmaking (lobby browser) + call results
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong D_CreateLobby(IntPtr self, int type, int max);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong D_SelfCall(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_StrFilter(IntPtr self, byte* key, byte* val, int cmp);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_IntFilter(IntPtr self, int v);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate ulong D_LobbyByIndex(IntPtr self, int i);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr D_GetLobbyData(IntPtr self, ulong lobby, byte* key);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool D_SetLobbyData(IntPtr self, ulong lobby, byte* key, byte* val);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_LeaveLobby(IntPtr self, ulong lobby);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_RegCallResult(IntPtr cb, ulong call);
        static D_Ptr _mmI; static D_CreateLobby _createLobby; static D_SelfCall _requestLobbyList; static D_StrFilter _strFilter; static D_IntFilter _distFilter, _countFilter;
        static D_LobbyByIndex _lobbyByIndex; static D_GetLobbyData _getLobbyData; static D_SetLobbyData _setLobbyData; static D_LeaveLobby _leaveLobby; static D_RegCallResult _regCallResult;
        public static IntPtr Matchmaking;
        public const int CB_LobbyMatchList = 510, CB_LobbyCreated = 513, CB_LobbyJoinRequested = 333, CB_LobbyDataUpdate = 505;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool D_ReqLobbyData(IntPtr self, ulong lobby);
        static D_ReqLobbyData _reqLobbyData;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool D_HasFriend(IntPtr self, ulong id, int flags);
        static D_HasFriend _hasFriend;
        /// <summary>That Steam user is on our friends list.</summary>
        public static bool IsFriend(ulong id) { try { return Ready && id != 0 && _hasFriend != null && _hasFriend(Friends, id, 4); } catch { return false; } }   // k_EFriendFlagImmediate
        /// <summary>Ask Steam for a lobby's data (answered by a LobbyDataUpdate_t callback).</summary>
        public static bool RequestLobbyData(ulong lobby) => Ready && _reqLobbyData != null && _reqLobbyData(Matchmaking, lobby);
        public const int LobbyPrivate = 0, LobbyFriendsOnly = 1, LobbyPublic = 2;

        /// <summary>Waits for an asynchronous Steam call (SteamAPICall_t) through a registered call result.</summary>
        static void CallResult(ulong call, int id, int size, Action<IntPtr, bool> done)
        {
            if (call == 0) { done(IntPtr.Zero, true); return; }
            var r = new Registered { Id = id, Size = size };
            r.A = (self, param, io, c) => { try { done(param, io != 0); } catch { } };
            r.B = (self, param) => { try { done(param, false); } catch { } };
            r.C = self => size;
            r.VTable = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(r.VTable, 0, Marshal.GetFunctionPointerForDelegate(r.A));
            Marshal.WriteIntPtr(r.VTable, IntPtr.Size, Marshal.GetFunctionPointerForDelegate(r.B));
            Marshal.WriteIntPtr(r.VTable, 2 * IntPtr.Size, Marshal.GetFunctionPointerForDelegate(r.C));
            r.Obj = Marshal.AllocHGlobal(32); for (int i = 0; i < 32; i++) Marshal.WriteByte(r.Obj, i, 0);
            Marshal.WriteIntPtr(r.Obj, 0, r.VTable); Marshal.WriteInt32(r.Obj, 12, id);
            _regCallResult(r.Obj, call);
            _registered.Add(r);
        }

        public static void CreateLobby(int type, Action<ulong> done)
        {
            ulong call = _createLobby(Matchmaking, type, 250);
            CallResult(call, CB_LobbyCreated, 16, (p, io) => { ulong lobby = 0; if (!io && p != IntPtr.Zero && *(int*)p == 1) lobby = *(ulong*)((byte*)p + 8); done(lobby); });
        }
        public static void SetLobbyData(ulong lobby, string key, string val) { if (lobby == 0) return; fixed (byte* k = Z(key)) fixed (byte* v = Z(val)) _setLobbyData(Matchmaking, lobby, k, v); }
        public static string GetLobbyData(ulong lobby, string key) { fixed (byte* k = Z(key)) return Utf8((byte*)_getLobbyData(Matchmaking, lobby, k)); }
        public static void LeaveLobby(ulong lobby) { if (lobby != 0 && Ready) _leaveLobby(Matchmaking, lobby); }
        /// <summary>Lobbies of this app with key = value, worldwide.</summary>
        public static void FindLobbies(string key, string val, Action<System.Collections.Generic.List<ulong>> done)
        {
            fixed (byte* k = Z(key)) fixed (byte* v = Z(val)) _strFilter(Matchmaking, k, v, 0);   // k_ELobbyComparisonEqual
            _distFilter(Matchmaking, 3);      // k_ELobbyDistanceFilterWorldwide
            _countFilter(Matchmaking, 200);   // Steam's default is 50
            ulong call = _requestLobbyList(Matchmaking);
            CallResult(call, CB_LobbyMatchList, 4, (p, io) =>
            {
                var l = new System.Collections.Generic.List<ulong>();
                if (!io && p != IntPtr.Zero) { uint n = *(uint*)p; for (int i = 0; i < n; i++) l.Add(_lobbyByIndex(Matchmaking, i)); }
                done(l);
            });
        }

        static D_Ptr _socketsI; static D_SelfIntRet _initAuth; static D_SelfBuf _authStatus, _relayStatusBuf; static D_SetDebug _setDebug;
        public static IntPtr Sockets;
        static D_IdSet _idSet; static D_IdGet _idGet; static D_MsgRelease _release; static D_AppId _appId;

        public static IntPtr Friends, User, Messages, Utils, NetUtils; public static int Pipe;
        public static bool Ready { get; private set; }
        /// <summary>A steam_api64.dll was loaded into the game (it stays loaded even if it turned out unusable).</summary>
        public static bool Loaded => _lib != IntPtr.Zero;
        public static ulong MySteamId { get; private set; }
        public static uint AppId { get; private set; }

        public const int CB_RichPresenceJoinRequested = 337, CB_SessionRequest = 1251, CB_SessionFailed = 1252;
        public const int SendReliable = 8, SendAutoRestart = 32;
        public const int MaxMessage = 512 * 1024;

        public static string Load(string dllPath)
        {
            if (!File.Exists(dllPath)) return "steam_api64.dll not found next to SprocketLAN.dll (see the README)";
            try { DllVersion = $"({new FileInfo(dllPath).Length / 1024} KB)"; } catch { }
            try { _lib = NativeLibrary.Load(dllPath); }
            catch (BadImageFormatException) { return "steam_api64.dll is not a 64-bit DLL (use the 64-bit one, e.g. from redistributable_bin/win64)"; }
            catch (Exception e) { return "steam_api64.dll could not be loaded: " + e.Message; }
            _missing.Clear();
            _init = Fn<D_InitFlat>("SteamAPI_InitFlat"); _shutdown = Fn<D_Void>("SteamAPI_Shutdown"); _running = Fn<D_Bool>("SteamAPI_IsSteamRunning");
            _mdInit = Fn<D_Void>("SteamAPI_ManualDispatch_Init"); _pipe = Fn<D_Int>("SteamAPI_GetHSteamPipe");
            _runFrame = Fn<D_PipeVoid>("SteamAPI_ManualDispatch_RunFrame"); _nextCb = Fn<D_NextCb>("SteamAPI_ManualDispatch_GetNextCallback"); _freeLast = Fn<D_PipeVoid>("SteamAPI_ManualDispatch_FreeLastCallback");
            _friendsI = Fn<D_Ptr>("SteamAPI_SteamFriends_v018");
            if (_friendsI == null)
            {
                // Steamworks 1.61 and older have the previous friends interface; every friends function this mod uses
                // (names, rich presence, invites, friend list) is the same in it, and this DLL's own flat functions match it
                _missing.Remove("SteamAPI_SteamFriends_v018");
                _friendsI = Fn<D_Ptr>("SteamAPI_SteamFriends_v017");
                if (_friendsI != null) MPLog.Info("Steam: this steam_api64.dll has the older friends interface (v017), using it");
            } _userI = Fn<D_Ptr>("SteamAPI_SteamUser_v023"); _msgsI = Fn<D_Ptr>("SteamAPI_SteamNetworkingMessages_SteamAPI_v002");
            _utilsI = Fn<D_Ptr>("SteamAPI_SteamUtils_v010"); _netUtilsI = Fn<D_Ptr>("SteamAPI_SteamNetworkingUtils_SteamAPI_v004");
            _getSteamId = Fn<D_SelfU64>("SteamAPI_ISteamUser_GetSteamID"); _persona = Fn<D_SelfStr>("SteamAPI_ISteamFriends_GetPersonaName");
            _friendName = Fn<D_SelfU64Str>("SteamAPI_ISteamFriends_GetFriendPersonaName"); _setRp = Fn<D_SetRp>("SteamAPI_ISteamFriends_SetRichPresence"); _clearRp = Fn<D_SelfVoid>("SteamAPI_ISteamFriends_ClearRichPresence");
            _inviteDialog = Fn<D_SelfBytes>("SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialogConnectString"); _invite = Fn<D_Invite>("SteamAPI_ISteamFriends_InviteUserToGame");
            _friendCount = Fn<D_FriendCount>("SteamAPI_ISteamFriends_GetFriendCount"); _friendByIndex = Fn<D_FriendByIndex>("SteamAPI_ISteamFriends_GetFriendByIndex"); _gamePlayed = Fn<D_GamePlayed>("SteamAPI_ISteamFriends_GetFriendGamePlayed");
            _send = Fn<D_Send>("SteamAPI_ISteamNetworkingMessages_SendMessageToUser"); _recv = Fn<D_Recv>("SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel");
            _accept = Fn<D_Accept>("SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser"); _sessInfo = Fn<D_SessInfo>("SteamAPI_ISteamNetworkingMessages_GetSessionConnectionInfo");
            _initRelay = Fn<D_SelfVoid>("SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess"); _relayStatus = Fn<D_RelayStatus>("SteamAPI_ISteamNetworkingUtils_GetRelayNetworkStatus");
            _idSet = Fn<D_IdSet>("SteamAPI_SteamNetworkingIdentity_SetSteamID64"); _idGet = Fn<D_IdGet>("SteamAPI_SteamNetworkingIdentity_GetSteamID64");
            _socketsI = Fn<D_Ptr>("SteamAPI_SteamNetworkingSockets_SteamAPI_v012"); _initAuth = Fn<D_SelfIntRet>("SteamAPI_ISteamNetworkingSockets_InitAuthentication");
            _authStatus = Fn<D_SelfBuf>("SteamAPI_ISteamNetworkingSockets_GetAuthenticationStatus"); _relayStatusBuf = Fn<D_SelfBuf>("SteamAPI_ISteamNetworkingUtils_GetRelayNetworkStatus");
            _setDebug = Fn<D_SetDebug>("SteamAPI_ISteamNetworkingUtils_SetDebugOutputFunction");
            _release = Fn<D_MsgRelease>("SteamAPI_SteamNetworkingMessage_t_Release");
            _closeSess = Fn<D_CloseSess>("SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser");
            _mmI = Fn<D_Ptr>("SteamAPI_SteamMatchmaking_v009"); _createLobby = Fn<D_CreateLobby>("SteamAPI_ISteamMatchmaking_CreateLobby");
            _requestLobbyList = Fn<D_SelfCall>("SteamAPI_ISteamMatchmaking_RequestLobbyList"); _strFilter = Fn<D_StrFilter>("SteamAPI_ISteamMatchmaking_AddRequestLobbyListStringFilter");
            _distFilter = Fn<D_IntFilter>("SteamAPI_ISteamMatchmaking_AddRequestLobbyListDistanceFilter"); _countFilter = Fn<D_IntFilter>("SteamAPI_ISteamMatchmaking_AddRequestLobbyListResultCountFilter");
            _lobbyByIndex = Fn<D_LobbyByIndex>("SteamAPI_ISteamMatchmaking_GetLobbyByIndex"); _getLobbyData = Fn<D_GetLobbyData>("SteamAPI_ISteamMatchmaking_GetLobbyData");
            _setLobbyData = Fn<D_SetLobbyData>("SteamAPI_ISteamMatchmaking_SetLobbyData"); _leaveLobby = Fn<D_LeaveLobby>("SteamAPI_ISteamMatchmaking_LeaveLobby");
            _regCallResult = Fn<D_RegCallResult>("SteamAPI_RegisterCallResult");
            _reqLobbyData = Fn<D_ReqLobbyData>("SteamAPI_ISteamMatchmaking_RequestLobbyData");
            _hasFriend = Fn<D_HasFriend>("SteamAPI_ISteamFriends_HasFriend");
            if (_missing.Count > 0)
            {
                // too old (or too new: an interface version was retired) for this mod
                bool old = _missing.Contains("SteamAPI_InitFlat");
                string list = string.Join(", ", _missing.Take(3)) + (_missing.Count > 3 ? $" (+{_missing.Count - 3} more)" : "");
                return $"steam_api64.dll{(DllVersion != "" ? " " + DllVersion : "")} is not compatible: {(old ? "too old, Steamworks SDK 1.58 or newer is needed" : "it lacks functions this mod uses")} (missing {list})";
            }
            _runCallbacks = Fn<D_Void>("SteamAPI_RunCallbacks"); _regCb = Fn<D_RegCb>("SteamAPI_RegisterCallback");
            _setSessReq = Fn<D_SetGlobalCb>("SteamAPI_ISteamNetworkingUtils_SetGlobalCallback_MessagesSessionRequest");
            _setSessFail = Fn<D_SetGlobalCb>("SteamAPI_ISteamNetworkingUtils_SetGlobalCallback_MessagesSessionFailed"); _appId = Fn<D_AppId>("SteamAPI_ISteamUtils_GetAppID");
            return null;
        }

        /// <summary>Starts Steam (manual callback dispatch, so nothing of the game's runs inside Steam's callbacks).</summary>
        public static string Init(Action<int, IntPtr, int> handler)
        {
            if (!_running()) return "Steam is not running";
            _handler = handler;
            if (ManualDispatch) _mdInit();
            var err = stackalloc byte[1024];
            int r = _init(err);
            if (r != 0) return $"SteamAPI_InitFlat failed ({r}): {Utf8(err)}";
            Pipe = _pipe();
            if (!ManualDispatch)
            {
                Register(CB_RichPresenceJoinRequested, 264);
                // session request / failure also as ordinary callbacks, in case the networking global callbacks don't fire
                Register(CB_SessionRequest, 136);
                Register(CB_SessionFailed, 1024);
                // invites sent from Steam's own friends list ("Invite to game") are lobby invites when the host has a lobby
                Register(CB_LobbyJoinRequested, 16);
                Register(CB_LobbyDataUpdate, 24);
                _sessReqCb = p => { try { _handler?.Invoke(CB_SessionRequest, p, 136); } catch { } };
                _sessFailCb = p => { try { _handler?.Invoke(CB_SessionFailed, p, 0); } catch { } };
            }
            Friends = _friendsI(); User = _userI(); Messages = _msgsI(); Utils = _utilsI(); NetUtils = _netUtilsI();
            if (Friends == IntPtr.Zero || User == IntPtr.Zero || Messages == IntPtr.Zero || NetUtils == IntPtr.Zero) return "a Steam interface is missing (wrong steam_api64.dll version?)";
            MySteamId = _getSteamId(User); AppId = Utils != IntPtr.Zero ? _appId(Utils) : 0;
            Sockets = _socketsI();
            Matchmaking = _mmI();
            if (!ManualDispatch)
            {
                _setSessReq(NetUtils, Marshal.GetFunctionPointerForDelegate(_sessReqCb));
                _setSessFail(NetUtils, Marshal.GetFunctionPointerForDelegate(_sessFailCb));
            }
            try { _setDebug(NetUtils, 4, Marshal.GetFunctionPointerForDelegate(_debugCb)); } catch { }   // Steam networking warnings -> our log
            if (Sockets != IntPtr.Zero) try { _initAuth(Sockets); } catch { }   // ask for the certificate now, not on first use
            _initRelay(NetUtils);
            RaiseSendRates();
            Ready = true;
            return null;
        }
        /// <summary>Steam's networking sends at most ~256 KB/s per peer by default and buffers 512 KB: a lobby design
        /// (hundreds of KB to MBs, to every player, through the host) crawled. Raised for this game: start at 1 MB/s,
        /// up to 16 MB/s (Steam still measures the connection and backs off), with a 16 MB send buffer.</summary>
        static void RaiseSendRates()
        {
            try
            {
                if (!NativeLibrary.TryGetExport(_lib, "SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32", out var fp) || fp == IntPtr.Zero) { MPLog.Info("Steam: can't change the send rate (old steam_api64.dll)"); return; }
                var set = Marshal.GetDelegateForFunctionPointer<D_SetCfgInt>(fp);
                const int SendBufferSize = 9, SendRateMin = 10, SendRateMax = 11;
                bool a = set(NetUtils, SendBufferSize, 16 << 20), b = set(NetUtils, SendRateMin, 1 << 20), c = set(NetUtils, SendRateMax, 16 << 20);
                MPLog.Info($"Steam: send rate 1-16 MB/s, 16 MB buffer ({(a && b && c ? "set" : "partly refused")})");
            }
            catch (Exception e) { MPLog.Warn("Steam send rate: " + e.Message); }
        }
        public static void Shutdown() { if (!Ready) return; Ready = false; try { _clearRp(Friends); } catch { } try { _shutdown(); } catch { } }

        public static void Pump(Action<int, IntPtr, int> onCallback)
        {
            if (!Ready) return;
            if (!ManualDispatch) { _runCallbacks(); return; }   // Steam's own internal work + our registered callbacks
            _runFrame(Pipe);
            CallbackMsg m;
            while (_nextCb(Pipe, &m))
            {
                try { onCallback(m.Callback, (IntPtr)m.Param, m.ParamSize); }
                finally { _freeLast(Pipe); }
            }
        }

        public static string PersonaName => Utf8((byte*)_persona(Friends));
        public static string FriendName(ulong id) => Safe.Text(Utf8((byte*)_friendName(Friends, id)), 32);   // a Steam name is shown as plain text
        public static void SetRichPresence(string key, string val) { fixed (byte* k = Z(key)) fixed (byte* v = Z(val)) _setRp(Friends, k, v); }
        public static void OpenInviteDialog(string connect) { fixed (byte* c = Z(connect)) _inviteDialog(Friends, c); }
        public static bool Invite(ulong friend, string connect) { fixed (byte* c = Z(connect)) return _invite(Friends, friend, c); }
        /// <summary>Friends currently in this game (same app id).</summary>
        public static System.Collections.Generic.List<ulong> FriendsInGame()
        {
            var l = new System.Collections.Generic.List<ulong>();
            int n = _friendCount(Friends, 4);   // k_EFriendFlagImmediate
            for (int i = 0; i < n; i++)
            {
                ulong f = _friendByIndex(Friends, i, 4); FriendGameInfo g;
                if (_gamePlayed(Friends, f, &g) && (uint)(g.GameId & 0xFFFFFF) == AppId) l.Add(f);
            }
            return l;
        }

        static void Id(byte* buf, ulong steamId) { for (int i = 0; i < 136; i++) buf[i] = 0; _idSet(buf, steamId); }
        /// <summary>Send to a user over SteamNetworkingMessages (reliable, channel 0). Returns the EResult (1 = OK).</summary>
        public static int Send(ulong to, byte[] data, bool reliable = true) => Send(to, data, 0, data.Length, reliable);
        public static int Send(ulong to, byte[] data, int offset, int count, bool reliable = true)
        {
            var id = stackalloc byte[136]; Id(id, to);
            fixed (byte* d = data) return _send(Messages, id, d + offset, (uint)count, (reliable ? SendReliable : 0) | SendNoNagle | SendAutoRestart, 0);
        }
        public const int SendNoNagle = 1;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] delegate bool D_CloseSess(IntPtr self, byte* identity);
        static D_CloseSess _closeSess;
        public static void CloseSession(ulong peer) { if (!Ready || _closeSess == null) return; var id = stackalloc byte[136]; Id(id, peer); _closeSess(Messages, id); }
        public static void ClearRichPresence() { if (Ready) try { _clearRp(Friends); } catch { } }
        public static int Receive(System.Collections.Generic.List<(ulong from, byte[] data)> into, int max = 64)
        {
            var arr = stackalloc IntPtr[max];
            int n = _recv(Messages, 0, arr, max);
            for (int i = 0; i < n; i++)
            {
                byte* m = (byte*)arr[i];
                IntPtr data = *(IntPtr*)m; int size = *(int*)(m + 8);
                ulong from = _idGet(m + 16);
                var b = new byte[size]; Marshal.Copy(data, b, 0, size);
                into.Add((from, b));
                _release(arr[i]);
            }
            return n;
        }
        /// <summary>Accept the session a SteamNetworkingMessagesSessionRequest_t callback asks about.</summary>
        public static ulong AcceptSession(IntPtr param) { _accept(Messages, (byte*)param); return _idGet((byte*)param); }
        public static ulong AcceptSessionPeek(IntPtr param) => _idGet((byte*)param);
        /// <summary>Connection state, Steam's own ping (ms) and whether it goes through Valve's relays.</summary>
        public static string StateText(int s) => s switch { 0 => "no session", 1 => "connecting", 2 => "finding a route", 3 => "connected", 4 => "closed by the other side", 5 => "problem on this side", -1 => "dead", _ => s.ToString() };
        /// <summary>Like Session, plus the end reason and Steam's explanation when it failed.</summary>
        public static (int state, int ping, bool relayed, int endReason, string endDebug) SessionDetail(ulong peer)
        {
            var id = stackalloc byte[136]; Id(id, peer);
            var info = stackalloc byte[1024]; var st = stackalloc byte[1024];
            for (int i = 0; i < 1024; i++) { info[i] = 0; st[i] = 0; }
            int s = _sessInfo(Messages, id, info, st);
            return (s, *(int*)(st + 4), (*(int*)(info + 440) & 16) != 0, *(int*)(info + 180), Utf8(info + 184));   // m_eEndReason, m_szEndDebug
        }
        public static (int state, int ping, bool relayed) Session(ulong peer)
        {
            var id = stackalloc byte[136]; Id(id, peer);
            var info = stackalloc byte[1024]; var st = stackalloc byte[1024];
            for (int i = 0; i < 1024; i++) { info[i] = 0; st[i] = 0; }
            int s = _sessInfo(Messages, id, info, st);
            int flags = *(int*)(info + 440);           // SteamNetConnectionInfo_t.m_nFlags
            int ping = *(int*)(st + 4);                // SteamNetConnectionRealTimeStatus_t.m_nPing
            return (s, ping, (flags & 16) != 0);       // k_nSteamNetworkConnectionInfoFlags_Relayed
        }
        public static int RelayStatus() => _relayStatus(NetUtils, IntPtr.Zero);

        public static readonly System.Collections.Concurrent.ConcurrentQueue<string> DebugLines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void D_DebugCb(int type, byte* msg);
        static readonly D_DebugCb _debugCb = OnDebug;   // kept alive for as long as Steam may call it
        static void OnDebug(int type, byte* msg) { try { if (DebugLines.Count < 500) DebugLines.Enqueue($"[steamnet {type}] {Utf8(msg)}"); } catch { } }

        /// <summary>Relay network detail: overall, network config download, any relay reachable, Steam's own message.</summary>
        public static (int avail, int config, int anyRelay, string msg) RelayDetail()
        {
            var b = stackalloc byte[512]; for (int i = 0; i < 512; i++) b[i] = 0;
            _relayStatusBuf(NetUtils, b);   // SteamRelayNetworkStatus_t: eAvail, bPingMeasurementInProgress, eAvailNetworkConfig, eAvailAnyRelay, debugMsg[256]
            return (*(int*)b, *(int*)(b + 8), *(int*)(b + 12), Utf8(b + 16));
        }
        /// <summary>Authentication (certificate) status: availability and Steam's message.</summary>
        public static (int avail, string msg) AuthDetail()
        {
            if (Sockets == IntPtr.Zero) return (0, "no sockets interface");
            var b = stackalloc byte[512]; for (int i = 0; i < 512; i++) b[i] = 0;
            int a = _authStatus(Sockets, b);   // SteamNetAuthenticationStatus_t: eAvail, debugMsg[256]
            return (a, Utf8(b + 4));
        }
        public static string RelayStatusText(int s) => s switch { 100 => "ready", 3 => "connecting", 2 => "waiting", 1 => "not tried", -10 => "retrying", -100 => "lost", -101 => "failed", -102 => "cannot try", _ => s.ToString() };

        public static byte[] Z(string s) { var b = Encoding.UTF8.GetBytes(s ?? ""); Array.Resize(ref b, b.Length + 1); return b; }
        public static string Utf8(byte* p) { if (p == null) return ""; int n = 0; while (p[n] != 0 && n < 4096) n++; return Encoding.UTF8.GetString(p, n); }
    }
}

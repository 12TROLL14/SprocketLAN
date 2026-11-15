using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SprocketMP.Net
{
    /// <summary>
    /// F10 › Steam › FIX IT: steam_api64.dll is missing (or unusable) next to SprocketLAN.dll. With the player's OK, look
    /// through their Steam libraries for another game's steam_api64.dll that has every function this mod uses (read from
    /// the file's export table, the DLL is never loaded), and copy the newest one into the plugin folder.
    /// Libraries: Steam's libraryfolders.vdf, found from the game's own folder (…/steamapps/common/Sprocket), Proton's
    /// STEAM_COMPAT_CLIENT_INSTALL_PATH and the usual install places. Under Wine / Proton, Linux paths are reached
    /// through the Z: drive.
    /// Safety: the DLL ends up running inside the game, so only Valve's own file is installed: its Authenticode
    /// signature must verify (WinVerifyTrust: the file is unchanged since it was signed, the chain is trusted; no network
    /// lookups) and the signer must be Valve Corp. The bytes that were checked are the bytes installed (read once,
    /// written under a temporary name in the plugin folder, verified there, then renamed). A matching DLL that can't be
    /// verified is never installed automatically: the player is told where it is. Only ever started by the player's
    /// own click (F10 › FIX IT › YES); nothing from the network can start it.
    /// </summary>
    public static class SteamDllFinder
    {
        public static volatile string Status = "";
        public static volatile bool Busy;
        static volatile bool _retry;

        /// <summary>Show FIX IT? (Steam is off because of the DLL itself, not e.g. because Steam isn't running.)</summary>
        public static bool CanFix => Plugin.SteamEnabled.Value && !SteamNet.Available && !Busy && (SteamNet.Problem ?? "").Contains("steam_api64.dll");

        // every function SteamApi.Load / Init binds (SteamApi.cs): a DLL lacking one would only half work
        static readonly string[] Required = (
            "SteamAPI_GetHSteamPipe SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialogConnectString SteamAPI_ISteamFriends_ClearRichPresence " +
            "SteamAPI_ISteamFriends_GetFriendByIndex SteamAPI_ISteamFriends_GetFriendCount SteamAPI_ISteamFriends_GetFriendGamePlayed SteamAPI_ISteamFriends_GetPersonaName " +
            "SteamAPI_ISteamFriends_HasFriend SteamAPI_ISteamFriends_InviteUserToGame SteamAPI_ISteamFriends_SetRichPresence " +
            "SteamAPI_ISteamMatchmaking_AddRequestLobbyListDistanceFilter SteamAPI_ISteamMatchmaking_AddRequestLobbyListResultCountFilter " +
            "SteamAPI_ISteamMatchmaking_AddRequestLobbyListStringFilter SteamAPI_ISteamMatchmaking_CreateLobby SteamAPI_ISteamMatchmaking_GetLobbyByIndex " +
            "SteamAPI_ISteamMatchmaking_GetLobbyData SteamAPI_ISteamMatchmaking_LeaveLobby SteamAPI_ISteamMatchmaking_RequestLobbyData SteamAPI_ISteamMatchmaking_RequestLobbyList " +
            "SteamAPI_ISteamMatchmaking_SetLobbyData SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser " +
            "SteamAPI_ISteamNetworkingMessages_GetSessionConnectionInfo SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel SteamAPI_ISteamNetworkingMessages_SendMessageToUser " +
            "SteamAPI_ISteamNetworkingSockets_GetAuthenticationStatus SteamAPI_ISteamNetworkingSockets_InitAuthentication SteamAPI_ISteamNetworkingUtils_GetRelayNetworkStatus " +
            "SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess SteamAPI_ISteamNetworkingUtils_SetDebugOutputFunction SteamAPI_ISteamNetworkingUtils_SetGlobalCallback_MessagesSessionFailed " +
            "SteamAPI_ISteamNetworkingUtils_SetGlobalCallback_MessagesSessionRequest SteamAPI_ISteamUtils_GetAppID SteamAPI_InitFlat SteamAPI_IsSteamRunning " +
            "SteamAPI_ManualDispatch_FreeLastCallback SteamAPI_ManualDispatch_GetNextCallback SteamAPI_ManualDispatch_Init SteamAPI_ManualDispatch_RunFrame " +
            "SteamAPI_RegisterCallResult SteamAPI_RegisterCallback SteamAPI_RunCallbacks SteamAPI_Shutdown SteamAPI_SteamFriends_v018 SteamAPI_SteamMatchmaking_v009 " +
            "SteamAPI_SteamNetworkingIdentity_GetSteamID64 SteamAPI_SteamNetworkingIdentity_SetSteamID64 SteamAPI_SteamNetworkingMessage_t_Release " +
            "SteamAPI_SteamNetworkingMessages_SteamAPI_v002 SteamAPI_SteamNetworkingSockets_SteamAPI_v012 SteamAPI_SteamNetworkingUtils_SteamAPI_v004 " +
            "SteamAPI_SteamUser_v023 SteamAPI_SteamUtils_v010").Split(' ');

        public static void Start()
        {
            if (Busy) return;
            Busy = true; Status = "Looking through your Steam libraries...";
            string pluginDir = Path.GetDirectoryName(typeof(SteamDllFinder).Assembly.Location) ?? "";
            string gameRoot = BepInEx.Paths.GameRootPath ?? "";
            new Thread(() => Run(pluginDir, gameRoot)) { IsBackground = true, Name = "SprocketLAN-steamdll" }.Start();
        }

        /// <summary>Main thread: once a DLL was put in place, start Steam again.</summary>
        public static void Tick()
        {
            if (!_retry) return;
            _retry = false;
            if (SteamNet.Retry()) Status += " Starting Steam...";
            else Status += " <color=#fd8>Restart the game to use it.</color>";
        }

        static void Run(string pluginDir, string gameRoot)
        {
            try
            {
                var libs = Libraries(gameRoot);
                MPLog.Info("Steam DLL search: libraries " + (libs.Count == 0 ? "none found" : string.Join(", ", libs)));
                if (libs.Count == 0) { Status = "<color=#f88>Couldn't find your Steam libraries.</color> Copy a 64-bit steam_api64.dll (Steamworks SDK 1.58+) next to SprocketLAN.dll yourself."; return; }
                var found = new List<(string path, string game, DateTime t, bool newest)>(); int seen = 0, bad = 0;
                string own = Path.GetFullPath(pluginDir).TrimEnd('\\', '/');
                foreach (var lib in libs)
                {
                    var common = Path.Combine(lib, "steamapps", "common");
                    if (!Directory.Exists(common)) continue;
                    foreach (var game in SafeDirs(common))
                        foreach (var f in FindDll(game, 0))
                        {
                            if (Path.GetFullPath(Path.GetDirectoryName(f) ?? "").TrimEnd('\\', '/').Equals(own, StringComparison.OrdinalIgnoreCase)) continue;
                            seen++; Status = $"Looking through your Steam libraries... {seen} steam_api64.dll checked";
                            string why = Check(f, out bool newest);
                            if (why == null) found.Add((f, Path.GetFileName(game), File.GetLastWriteTimeUtc(f), newest));
                            else { bad++; MPLog.Debug($"Steam DLL search: {f}: {why}"); }
                        }
                }
                if (found.Count == 0)
                {
                    Status = seen == 0 ? "<color=#f88>No other game in your Steam libraries has a steam_api64.dll.</color>"
                        : $"<color=#f88>{seen} steam_api64.dll found, none recent enough</color> (Steamworks SDK 1.58+ needed). Copy a newer one next to SprocketLAN.dll yourself.";
                    MPLog.Info($"Steam DLL search: {seen} found, none compatible");
                    return;
                }
                // the newest current one first; each candidate is verified (Valve's signature) on the exact bytes installed
                var target = Path.Combine(pluginDir, "steam_api64.dll");
                var temp = Path.Combine(pluginDir, "steam_api64.dll.verify");
                (string path, string game) unverified = default;
                foreach (var c in found.OrderByDescending(x => x.newest).ThenByDescending(x => x.t))
                {
                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(c.path); } catch { continue; }
                    if (Check(bytes, out _) != null) continue;   // changed since the scan
                    try { if (File.Exists(temp)) File.Delete(temp); File.WriteAllBytes(temp, bytes); }
                    catch (Exception e) { MPLog.Warn("Steam DLL search: can't write in the plugin folder: " + e.Message); Status = "<color=#f88>Can't write in the plugin folder: " + Plain(e.Message) + "</color>"; return; }
                    string why = VerifyValve(temp);
                    if (why != null)
                    {
                        MPLog.Info($"Steam DLL search: {c.path} not used: {why}");
                        if (unverified.path == null) unverified = (c.path, c.game);
                        try { File.Delete(temp); } catch { }
                        continue;
                    }
                    if (File.Exists(target))
                    {
                        // a loaded DLL can't be overwritten, but it can be renamed out of the way
                        var old = target + ".old"; int n = 1; while (File.Exists(old)) old = target + ".old" + (++n);
                        File.Move(target, old);
                    }
                    File.Move(temp, target);
                    MPLog.Info($"Steam DLL search: installed {c.path} ({c.game}), Valve signature verified; {found.Count} compatible, {bad} not");
                    Status = $"<color=#8f8>Installed Valve's steam_api64.dll from {Plain(c.game)}.</color>";
                    _retry = true;
                    return;
                }
                Status = $"<color=#f88>Found a matching steam_api64.dll in {Plain(unverified.game)}, but couldn't confirm it's Valve's original, so it wasn't installed.</color> " +
                         "<color=#9aa>If you trust it, copy it next to SprocketLAN.dll yourself (path in the log).</color>";
            }
            catch (Exception e) { Status = "<color=#f88>Search failed: " + e.Message + "</color>"; MPLog.Warn("Steam DLL search: " + e); }
            finally { Busy = false; }
        }

        // ------------------------------------------------------------------------------------------- Steam libraries
        static List<string> Libraries(string gameRoot)
        {
            var roots = new List<string>();
            void Add(string p) { try { if (!string.IsNullOrWhiteSpace(p) && Directory.Exists(p)) { p = Path.GetFullPath(p).TrimEnd('\\', '/'); if (!roots.Any(r => r.Equals(p, StringComparison.OrdinalIgnoreCase))) roots.Add(p); } } catch { } }
            // the library this game is in: …/steamapps/common/<game>
            try { var d = new DirectoryInfo(gameRoot); while (d != null && !d.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase)) d = d.Parent; if (d?.Parent != null) Add(d.Parent.FullName); } catch { }
            // Steam's own folder: Proton tells us (a Linux path); else the usual places
            Add(Native(Environment.GetEnvironmentVariable("STEAM_COMPAT_CLIENT_INSTALL_PATH")));
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrEmpty(home) && home.StartsWith("/")) { Add(Native(home + "/.steam/steam")); Add(Native(home + "/.local/share/Steam")); }
            Add(@"C:\Program Files (x86)\Steam"); Add(@"C:\Program Files\Steam");
            // every library listed in a libraryfolders.vdf
            foreach (var r in roots.ToList())
            {
                var vdf = Path.Combine(r, "steamapps", "libraryfolders.vdf");
                try
                {
                    if (!File.Exists(vdf)) continue;
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"")) Add(Native(m.Groups[1].Value.Replace("\\\\", "\\")));
                }
                catch { }
            }
            return roots;
        }
        /// <summary>A Linux path ("/run/media/...") as Wine sees it (Z:\run\media\...); Windows paths unchanged.</summary>
        static string Native(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return null;
            return p.StartsWith("/") ? "Z:" + p.Replace('/', '\\') : p;
        }
        static readonly HashSet<string> Skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Content", "Paks", "Movies", "Saved", "StreamingAssets", "Localization", "Shaders", "ShaderCache", "Textures", "Sounds", "Audio", "Music", "Videos", "Maps", "Levels", "__MACOSX", ".git" };
        static IEnumerable<string> SafeDirs(string d) { try { return Directory.GetDirectories(d); } catch { return Array.Empty<string>(); } }
        /// <summary>steam_api64.dll files in a game's folder, a few levels deep (where games keep it).</summary>
        static IEnumerable<string> FindDll(string dir, int depth)
        {
            string[] files; try { files = Directory.GetFiles(dir, "steam_api64.dll"); } catch { files = Array.Empty<string>(); }
            foreach (var f in files) yield return f;
            // Unreal games keep it deep: Engine/Binaries/ThirdParty/Steamworks/Steamv161/Win64
            if (depth >= 8) yield break;
            foreach (var sub in SafeDirs(dir))
            {
                var n = Path.GetFileName(sub);
                if (Skip.Contains(n)) continue;   // big asset folders: no DLLs there
                foreach (var f in FindDll(sub, depth + 1)) yield return f;
            }
        }

        // ------------------------------------------------------------------------------------------- the DLL itself
        /// <summary>null if the file is a 64-bit DLL exporting every function we use; else why not.</summary>
        static string Check(string path, out bool newest)
        {
            newest = false;
            try
            {
                var fi = new FileInfo(path); if (fi.Length < 4096 || fi.Length > 32 << 20) return "odd size";
                return Check(File.ReadAllBytes(path), out newest);
            }
            catch (Exception e) { return e.Message; }
        }
        static string Check(byte[] bytes, out bool newest)
        {
            newest = false;
            try
            {
                if (bytes.Length < 4096 || bytes.Length > 32 << 20) return "odd size";
                var exports = Exports(bytes);
                if (exports == null) return "not a 64-bit DLL";
                // the previous friends interface (Steamworks 1.61 and older) is fine too: SteamApi falls back to it
                var missing = Required.Where(r => !exports.Contains(r) && !(r == "SteamAPI_SteamFriends_v018" && exports.Contains("SteamAPI_SteamFriends_v017"))).ToList();
                newest = exports.Contains("SteamAPI_SteamFriends_v018");
                return missing.Count == 0 ? null : $"missing {missing.Count} functions (e.g. {missing[0]})";
            }
            catch (Exception e) { return e.Message; }
        }
        static string Plain(string s) => (s ?? "").Replace("<", "(").Replace(">", ")");

        /// <summary>null if the file carries a valid Authenticode signature (unchanged since signing, trusted chain) from
        /// Valve Corp.; else why not. Windows' / Wine's own WinVerifyTrust, offline (no revocation lookups).</summary>
        static string VerifyValve(string file)
        {
            try
            {
                var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = Marshal.StringToCoTaskMemUni(file) };
                IntPtr pFile = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_FILE_INFO>());
                try
                {
                    Marshal.StructureToPtr(fileInfo, pFile, false);
                    var data = new WINTRUST_DATA
                    {
                        cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(), dwUIChoice = 2 /* WTD_UI_NONE */, fdwRevocationChecks = 0 /* WTD_REVOKE_NONE */,
                        dwUnionChoice = 1 /* WTD_CHOICE_FILE */, pFile = pFile, dwStateAction = 0,
                        dwProvFlags = 0x10 /* WTD_REVOCATION_CHECK_NONE */ | 0x1000 /* WTD_CACHE_ONLY_URL_RETRIEVAL: no network */
                    };
                    var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");   // WINTRUST_ACTION_GENERIC_VERIFY_V2
                    int r = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
                    if (r != 0) return $"signature not valid (WinVerifyTrust 0x{r:X8})";
                }
                finally { Marshal.FreeCoTaskMem(fileInfo.pcwszFilePath); Marshal.FreeCoTaskMem(pFile); }
                // valid and trusted: now whose signature is it
                var signer = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(file);
                string subject = signer?.Subject ?? "";
                if (!subject.Contains("O=Valve Corp.", StringComparison.Ordinal)) return "signed, but not by Valve (" + subject + ")";
                return null;
            }
            catch (Exception e) { return "signature can't be checked: " + e.Message; }
        }
        [StructLayout(LayoutKind.Sequential)] struct WINTRUST_FILE_INFO { public uint cbStruct; public IntPtr pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }
        [StructLayout(LayoutKind.Sequential)]
        struct WINTRUST_DATA
        {
            public uint cbStruct; public IntPtr pPolicyCallbackData, pSIPClientData; public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
            public IntPtr pFile; public uint dwStateAction; public IntPtr hWVTStateData, pwszURLReference; public uint dwProvFlags, dwUIContext; public IntPtr pSignatureSettings;
        }
        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

        /// <summary>Names a PE32+ (64-bit) DLL exports, from its export table; null if it isn't one.</summary>
        static HashSet<string> Exports(byte[] b)
        {
            int I32(int o) => BitConverter.ToInt32(b, o); int U16(int o) => BitConverter.ToUInt16(b, o);
            if (b.Length < 0x200 || b[0] != 'M' || b[1] != 'Z') return null;
            int pe = I32(0x3C); if (pe <= 0 || pe + 24 > b.Length || I32(pe) != 0x4550) return null;
            int sections = U16(pe + 6), optSize = U16(pe + 20), opt = pe + 24;
            if (U16(opt) != 0x20B) return null;                         // PE32+ only (a 32-bit steam_api.dll won't load)
            int expRva = I32(opt + 112);                                // data directory 0 (PE32+)
            if (expRva == 0) return new HashSet<string>();
            int sec = opt + optSize;
            int Off(int rva)
            {
                for (int i = 0; i < sections; i++)
                {
                    int s = sec + i * 40, va = I32(s + 12), size = Math.Max(I32(s + 8), I32(s + 16)), raw = I32(s + 20);
                    if (rva >= va && rva < va + size) return rva - va + raw;
                }
                return -1;
            }
            int ed = Off(expRva); if (ed < 0 || ed + 40 > b.Length) return null;
            int n = I32(ed + 24), names = Off(I32(ed + 32));
            if (n < 0 || n > 100000 || names < 0) return null;
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < n; i++)
            {
                if (names + i * 4 + 4 > b.Length) break;
                int o = Off(I32(names + i * 4)); if (o < 0 || o >= b.Length) continue;
                int e = o; while (e < b.Length && b[e] != 0 && e - o < 256) e++;
                set.Add(Encoding.ASCII.GetString(b, o, e - o));
            }
            return set;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace HowToFish.RoomInfo
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class RoomInfoPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "takesei.howtofish.roominfo";
        public const string PluginName = "How to Fish - Room Info";
        public const string PluginVersion = "1.3.0";

        private const uint CF_UNICODETEXT = 13;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool IsClipboardFormatAvailable(uint format);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetClipboardData(uint uFormat);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalUnlock(IntPtr hMem);

        private ConfigEntry<float> _rightMargin;
        private ConfigEntry<float> _topMargin;
        private ConfigEntry<float> _panelWidth;
        private ConfigEntry<bool> _debug;

        private string _roomCode;
        private string _status = "等待刷新";
        private string _copySource;

        private bool _pendingClipboardRead;
        private float _clipboardReadAt;
        private float _nextPassiveRead;

        private GUIStyle _titleStyle;
        private GUIStyle _textStyle;
        private GUIStyle _smallStyle;
        private GUIStyle _boxStyle;

        private void Awake()
        {
            _rightMargin = Config.Bind("Display", "RightMargin", 28f, "Distance from the right side.");
            _topMargin = Config.Bind("Display", "TopMargin", 82f, "Distance from the top.");
            _panelWidth = Config.Bind("Display", "PanelWidth", 300f, "Room-info panel width.");
            _debug = Config.Bind("Debug", "VerboseLog", false, "Verbose room-code detection logging.");

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private void Update()
        {
            float now = Time.unscaledTime;

            if (_pendingClipboardRead && now >= _clipboardReadAt)
            {
                _pendingClipboardRead = false;
                ReadClipboardIntoRoomCode("刷新后读取");
            }

            // While paused, passively read the Wine clipboard occasionally.
            // This means the game's original "复制房号" button also works:
            // click it, and this panel will pick up the value without needing
            // Android clipboard synchronization.
            if (IsPauseLikeState() && now >= _nextPassiveRead)
            {
                _nextPassiveRead = now + 0.75f;
                ReadClipboardIntoRoomCode(null);
            }
        }

        private void OnGUI()
        {
            if (!IsPauseLikeState())
                return;

            EnsureStyles();

            float width = Mathf.Clamp(_panelWidth.Value, 230f, 460f);
            float x = Screen.width - width - Mathf.Max(0f, _rightMargin.Value);
            float y = Mathf.Max(0f, _topMargin.Value);
            Rect panel = new Rect(x, y, width, 142f);

            GUI.Box(panel, GUIContent.none, _boxStyle);

            GUI.Label(new Rect(x + 14f, y + 10f, width - 28f, 28f), "房间信息", _titleStyle);

            string roomText = string.IsNullOrWhiteSpace(_roomCode)
                ? "房间号: --"
                : "房间号: " + _roomCode;

            GUI.Label(new Rect(x + 14f, y + 42f, width - 28f, 26f), roomText, _textStyle);

            if (GUI.Button(new Rect(x + 14f, y + 72f, width - 28f, 30f), "刷新房号"))
            {
                RefreshRoomCode();
            }

            GUI.Label(
                new Rect(x + 14f, y + 108f, width - 28f, 24f),
                _status ?? string.Empty,
                _smallStyle);
        }

        private bool IsPauseLikeState()
        {
            // The game's pause menu freezes simulation. Using unscaled IMGUI keeps
            // this independent from the exact PauseMenu class/layout and from 1.0.x
            // UI renames.
            return Application.isFocused && Time.timeScale <= 0.001f;
        }

        private void RefreshRoomCode()
        {
            _status = "正在刷新...";

            bool invoked = TryInvokeNativeRoomCopy(out string source);
            _copySource = source;

            // Read immediately too; some handlers are synchronous.
            bool gotNow = ReadClipboardIntoRoomCode(invoked ? "已调用原生复制" : "读取现有剪贴板");

            // Then read again shortly afterward in case the game's handler updates
            // the clipboard on a delayed frame/callback.
            _pendingClipboardRead = true;
            _clipboardReadAt = Time.unscaledTime + 0.20f;

            if (!invoked && !gotNow)
                _status = "未找到复制方法；先点原生“复制房号”";
            else if (!invoked && gotNow)
                _status = "已从 Wine 剪贴板读取";
            else if (invoked && !gotNow)
                _status = "已调用复制，等待剪贴板";
        }

        private bool TryInvokeNativeRoomCopy(out string source)
        {
            source = null;
            MonoBehaviour bestInstance = null;
            MethodInfo bestMethod = null;
            int bestScore = int.MinValue;

            try
            {
                MonoBehaviour[] behaviours = Resources.FindObjectsOfTypeAll<MonoBehaviour>();

                foreach (MonoBehaviour behaviour in behaviours)
                {
                    if (!behaviour || !behaviour.gameObject)
                        continue;

                    Type type = behaviour.GetType();
                    string typeName = (type.FullName ?? type.Name ?? string.Empty).ToLowerInvariant();
                    string objectPath = BuildObjectPath(behaviour.transform).ToLowerInvariant();

                    foreach (MethodInfo method in SafeGetMethods(type))
                    {
                        if (method.IsStatic || method.ContainsGenericParameters)
                            continue;
                        if (method.GetParameters().Length != 0)
                            continue;
                        if (method.ReturnType != typeof(void))
                            continue;

                        int score = ScoreCopyMethod(typeName, objectPath, method.Name);
                        if (score <= bestScore)
                            continue;

                        bestScore = score;
                        bestInstance = behaviour;
                        bestMethod = method;
                    }
                }

                // Require a genuinely room/lobby/code-related candidate.
                if (bestMethod == null || bestInstance == null || bestScore < 90)
                {
                    if (_debug.Value)
                        Logger.LogInfo($"No safe room-copy method found. bestScore={bestScore}");
                    return false;
                }

                source = $"{bestInstance.GetType().FullName}.{bestMethod.Name}";

                if (_debug.Value)
                    Logger.LogInfo($"Invoking room-copy candidate score={bestScore}: {source}");

                bestMethod.Invoke(bestInstance, null);
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Room copy invocation failed: {ex.GetBaseException().Message}");
                return false;
            }
        }

        private static int ScoreCopyMethod(string typeName, string objectPath, string methodName)
        {
            string name = NormalizeName(methodName);
            int score = 0;

            bool copy = name.Contains("copy");
            bool room = name.Contains("room");
            bool lobby = name.Contains("lobby");
            bool code = name.Contains("code");
            bool invite = name.Contains("invite");
            bool clipboard = name.Contains("clipboard");

            if (!copy)
                return 0;

            score += 35;
            if (room) score += 70;
            if (lobby) score += 70;
            if (code) score += 55;
            if (invite) score += 35;
            if (clipboard) score += 25;

            string context = typeName + "/" + objectPath;
            if (context.Contains("pause")) score += 30;
            if (context.Contains("menu")) score += 20;
            if (context.Contains("server")) score += 15;
            if (context.Contains("online")) score += 15;
            if (context.Contains("room")) score += 25;
            if (context.Contains("lobby")) score += 25;

            // Generic CopyToClipboard without room/lobby/code context is intentionally
            // too weak to invoke automatically.
            return score;
        }

        private bool ReadClipboardIntoRoomCode(string successStatus)
        {
            string raw = ReadWineClipboard();

            if (_debug.Value && !string.IsNullOrWhiteSpace(raw))
                Logger.LogInfo($"Clipboard raw: '{raw}'");

            string code = ExtractLikelyRoomCode(raw);
            if (string.IsNullOrWhiteSpace(code))
                return false;

            if (_roomCode == code)
                return true;

            _roomCode = code;

            if (!string.IsNullOrEmpty(successStatus))
                _status = successStatus;
            else
                _status = "已捕获原生复制";

            if (_debug.Value)
                Logger.LogInfo($"Room code updated: {_roomCode}; copySource={_copySource ?? "<manual>"}");

            return true;
        }

        private static string ReadWineClipboard()
        {
            // First ask Unity. On Wine this often maps directly to the Win32 clipboard.
            try
            {
                string unityText = GUIUtility.systemCopyBuffer;
                if (!string.IsNullOrWhiteSpace(unityText))
                    return unityText.Trim();
            }
            catch { }

            // Fall back to native CF_UNICODETEXT. Wine implements these user32/kernel32
            // calls even when the Android host does not synchronize its own clipboard.
            bool opened = false;
            try
            {
                if (!IsClipboardFormatAvailable(CF_UNICODETEXT))
                    return null;

                opened = OpenClipboard(IntPtr.Zero);
                if (!opened)
                    return null;

                IntPtr handle = GetClipboardData(CF_UNICODETEXT);
                if (handle == IntPtr.Zero)
                    return null;

                IntPtr ptr = GlobalLock(handle);
                if (ptr == IntPtr.Zero)
                    return null;

                try
                {
                    string text = Marshal.PtrToStringUni(ptr);
                    return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                if (opened)
                {
                    try { CloseClipboard(); } catch { }
                }
            }
        }

        private static string ExtractLikelyRoomCode(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            string text = raw.Trim();

            // If the game copied only the room code, preserve it exactly.
            if (Regex.IsMatch(text, @"^[A-Za-z0-9_-]{3,32}$"))
                return text;

            // Common copied messages/URLs: prefer a short alphanumeric token.
            MatchCollection matches = Regex.Matches(text, @"[A-Za-z0-9]{4,16}");
            string best = null;
            int bestScore = int.MinValue;

            foreach (Match match in matches)
            {
                string token = match.Value;

                string lower = token.ToLowerInvariant();
                if (lower == "http" || lower == "https" ||
                    lower == "steam" || lower == "invite" ||
                    lower == "room" || lower == "lobby" ||
                    lower == "code" || lower == "fish")
                    continue;

                int score = 0;
                if (token.Length >= 4 && token.Length <= 10) score += 40;
                if (token.Any(char.IsDigit)) score += 20;
                if (token.Any(char.IsLetter) && token.Any(char.IsDigit)) score += 20;
                if (token.Length > 12) score -= 20;

                if (score > bestScore)
                {
                    best = token;
                    bestScore = score;
                }
            }

            return best;
        }

        private void EnsureStyles()
        {
            if (_titleStyle != null)
                return;

            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 20,
                richText = false
            };
            _titleStyle.normal.textColor = Color.white;

            _textStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                richText = false
            };
            _textStyle.normal.textColor = Color.white;

            _smallStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                richText = false
            };
            _smallStyle.normal.textColor = new Color(1f, 1f, 1f, 0.78f);

            _boxStyle = new GUIStyle(GUI.skin.box);
        }

        private static string BuildObjectPath(Transform transform)
        {
            if (!transform)
                return string.Empty;

            var parts = new List<string>(10);
            Transform current = transform;
            int guard = 0;

            while (current && guard++ < 16)
            {
                parts.Add(current.name ?? string.Empty);
                current = current.parent;
            }

            parts.Reverse();
            return string.Join("/", parts);
        }

        private static MethodInfo[] SafeGetMethods(Type type)
        {
            try
            {
                return type.GetMethods(
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Instance);
            }
            catch
            {
                return Array.Empty<MethodInfo>();
            }
        }

        private static string NormalizeName(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }
    }
}

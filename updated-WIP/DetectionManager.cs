using System;
using System.Collections.Generic;
using Photon.Realtime;
using Steamworks;

namespace AntiCheatMod
{
    public enum DetectionType
    {
        // Cheat Mod Detection
        CherryMod,
        AtlasMod,
        
        // Name Spoofing
        SteamNameMismatch,
        NameImpersonation,
        MidGameNameChange,
        
        // RPC Abuse
        OwnershipTheft,

        UnauthorizedDestroy,
        RateLimitExceeded,
        
        // Character Control
        UnauthorizedKill,
        UnauthorizedRevive,
        UnauthorizedWarp,
        UnauthorizedStatusEffect,
        UnauthorizedMovement,
        UnauthorizedEmote,
        
        // Item Manipulation
        UnauthorizedItemDrop,
        UnauthorizedCampfireModification,
        UnauthorizedFlareLighting,
        UnauthorizedBananaSlip,
        
        // Master Client Protection
        MasterClientTheft,
        
        // Infinity/Black Screen
        InfinityWarp,
        BlackScreenAttempt
    }

    public class DetectionSettings
    {
        public bool IsEnabled { get; set; } = true;
        public bool AutoBlock { get; set; } = true;
        public bool ShowVisualWarning { get; set; } = true;
        public bool LogToConsole { get; set; } = true;
        public string CustomReason { get; set; } = "";
        
        public DetectionSettings() { }
        
        public DetectionSettings(bool isEnabled, bool autoBlock = true, bool showVisual = true, bool logToConsole = true)
        {
            IsEnabled = isEnabled;
            AutoBlock = autoBlock;
            ShowVisualWarning = showVisual;
            LogToConsole = logToConsole;
        }
    }

    public class DetectionResult
    {
        public DetectionType Type { get; set; }
        public Photon.Realtime.Player Target { get; set; }
        public string Reason { get; set; }
        public DateTime Timestamp { get; set; }
        public bool WasBlocked { get; set; }
        public CSteamID SteamID { get; set; }
        
        public DetectionResult(DetectionType type, Photon.Realtime.Player target, string reason, CSteamID steamID = default)
        {
            Type = type;
            Target = target;
            Reason = reason;
            Timestamp = DateTime.Now;
            SteamID = steamID;
        }
    }

    public static class DetectionManager
    {
        private static readonly Dictionary<DetectionType, DetectionSettings> _detectionSettings = new Dictionary<DetectionType, DetectionSettings>();
        private static readonly List<DetectionResult> _detectionHistory = new List<DetectionResult>();
        private static readonly Dictionary<int, List<DetectionResult>> _playerDetectionHistory = new Dictionary<int, List<DetectionResult>>();
        
        // Events for UI updates
        public static event Action<DetectionResult> OnDetectionOccurred;
        public static event Action<DetectionType, DetectionSettings> OnDetectionSettingsChanged;
        public static event Action<int, bool> OnPlayerBlockStatusChanged;

        static DetectionManager()
        {
            // Initialize default settings for all detection types
            foreach (DetectionType type in Enum.GetValues(typeof(DetectionType)))
            {
                _detectionSettings[type] = new DetectionSettings();
            }
        }

        // Serialization helpers for Photon networking
        public static object[] SerializeSettings()
        {
            var settings = new object[Enum.GetValues(typeof(DetectionType)).Length * 4]; // 4 properties per setting
            int index = 0;
            
            foreach (DetectionType type in Enum.GetValues(typeof(DetectionType)))
            {
                var setting = _detectionSettings[type];
                settings[index++] = setting.IsEnabled;
                settings[index++] = setting.AutoBlock;
                settings[index++] = setting.ShowVisualWarning;
                settings[index++] = setting.LogToConsole;
            }
            
            return settings;
        }

        public static void DeserializeSettings(object[] data)
        {
            int index = 0;
            foreach (DetectionType type in Enum.GetValues(typeof(DetectionType)))
            {
                var setting = new DetectionSettings();
                setting.IsEnabled = (bool)data[index++];
                setting.AutoBlock = (bool)data[index++];
                setting.ShowVisualWarning = (bool)data[index++];
                setting.LogToConsole = (bool)data[index++];
                _detectionSettings[type] = setting;
            }
        }

        public static DetectionSettings GetDetectionSettings(DetectionType type)
        {
            return _detectionSettings.TryGetValue(type, out var settings) ? settings : new DetectionSettings();
        }

        public static void SetDetectionSettings(DetectionType type, DetectionSettings settings)
        {
            _detectionSettings[type] = settings;
            OnDetectionSettingsChanged?.Invoke(type, settings);
        }

        public static void SetDetectionEnabled(DetectionType type, bool enabled)
        {
            if (_detectionSettings.TryGetValue(type, out var settings))
            {
                settings.IsEnabled = enabled;
                OnDetectionSettingsChanged?.Invoke(type, settings);
            }
        }

        public static bool IsDetectionEnabled(DetectionType type)
        {
            return _detectionSettings.TryGetValue(type, out var settings) && settings.IsEnabled;
        }

        public static bool ShouldAutoBlock(DetectionType type)
        {
            return _detectionSettings.TryGetValue(type, out var settings) && settings.AutoBlock;
        }

        public static bool ShouldShowVisualWarning(DetectionType type)
        {
            return _detectionSettings.TryGetValue(type, out var settings) && settings.ShowVisualWarning;
        }

        public static bool ShouldLogToConsole(DetectionType type)
        {
            return _detectionSettings.TryGetValue(type, out var settings) && settings.LogToConsole;
        }

        public static void RecordDetection(DetectionType type, Photon.Realtime.Player target, string reason, CSteamID steamID = default)
        {
            var result = new DetectionResult(type, target, reason, steamID);
            _detectionHistory.Add(result);
            
            if (!_playerDetectionHistory.ContainsKey(target.ActorNumber))
                _playerDetectionHistory[target.ActorNumber] = new List<DetectionResult>();
            
            _playerDetectionHistory[target.ActorNumber].Add(result);
            
            OnDetectionOccurred?.Invoke(result);
        }

        public static List<DetectionResult> GetDetectionHistory()
        {
            return new List<DetectionResult>(_detectionHistory);
        }

        public static List<DetectionResult> GetPlayerDetectionHistory(int actorNumber)
        {
            return _playerDetectionHistory.TryGetValue(actorNumber, out var history) 
                ? new List<DetectionResult>(history) 
                : new List<DetectionResult>();
        }

        public static void ClearDetectionHistory()
        {
            _detectionHistory.Clear();
            _playerDetectionHistory.Clear();
        }

        public static void ClearPlayerDetectionHistory(int actorNumber)
        {
            _playerDetectionHistory.Remove(actorNumber);
        }

        public static DetectionSettings[] GetAllSettings()
        {
            var arr = new DetectionSettings[Enum.GetValues(typeof(DetectionType)).Length];
            foreach (DetectionType type in Enum.GetValues(typeof(DetectionType)))
                arr[(int)type] = _detectionSettings[type];
            return arr;
        }
        public static void SetAllSettings(DetectionSettings[] arr)
        {
            foreach (DetectionType type in Enum.GetValues(typeof(DetectionType)))
                _detectionSettings[type] = arr[(int)type];
        }
    }
} 
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using BepInEx.Configuration;

namespace AntiCheatMod
{
    public static class TranslationManager
    {
        private static Dictionary<string, string> _translations = new Dictionary<string, string>();
        private static string _currentLanguage = "en";
        private static bool _isInitialized = false;
        private static ConfigEntry<string> _languageConfig;

        // Default English translations
        private static readonly Dictionary<string, string> _defaultTranslations = new Dictionary<string, string>
        {
            // Window and sections
            {"UI_WINDOW_TITLE", "PEAK AntiCheat"},
            {"UI_DETECTIONS", "Detections"},
            {"UI_PLAYERS", "Players"},
            {"UI_BLOCKED_PLAYERS", "Blocked Players"},
            
            // Detection types
            {"DETECTION_CHEAT_DETECTION", "Cheat Detection"},
            {"DETECTION_SPOOFED_NAMES", "Spoofed Names"},
            {"DETECTION_OWNERSHIP_THEFT", "Ownership Theft"},
            {"DETECTION_UNAUTHORIZED_DESTROY", "Unauthorized Destroy"},
            {"DETECTION_RATE_LIMIT_EXCEEDED", "Rate Limit Exceeded"},
            {"DETECTION_UNAUTHORIZED_KILL", "Unauthorized Kill"},
            {"DETECTION_UNAUTHORIZED_REVIVE", "Unauthorized Revive"},
            {"DETECTION_UNAUTHORIZED_WARP", "Unauthorized Warp"},
            {"DETECTION_UNAUTHORIZED_STATUS_EFFECT", "Unauthorized Status Effect"},
            {"DETECTION_UNAUTHORIZED_MOVEMENT", "Unauthorized Movement"},
            {"DETECTION_UNAUTHORIZED_EMOTE", "Unauthorized Emote"},
            {"DETECTION_UNAUTHORIZED_ITEM_DROP", "Unauthorized Item Drop"},
            {"DETECTION_UNAUTHORIZED_CAMPFIRE", "Unauthorized Campfire"},
            {"DETECTION_UNAUTHORIZED_FLARE", "Unauthorized Flare"},
            {"DETECTION_UNAUTHORIZED_BANANA", "Unauthorized Banana"},
            {"DETECTION_MASTER_CLIENT_THEFT", "Master Client Theft"},
            {"DETECTION_STEAM_ID_SPOOFING", "Steam ID Spoofing"},
            {"DETECTION_INFINITY_WARP", "Infinity Warp"},
            {"DETECTION_PREFAB_DETECTION", "Prefab Detection"},
            {"DETECTION_ITEM_SPAWNING", "Item Spawning"},
            
            // Settings
            {"SETTING_OFF", "Off"},
            {"SETTING_WARN", "Warn"},
            {"SETTING_BLOCK", "Block"},
            {"SETTING_AUTOKICK_BLOCKED_PLAYERS", "Autokick Blocked Players"},
            {"SETTING_AUTOBLOCK_NO_ANTICHEAT", "Autoblock No Anticheat"},
            {"SETTING_ADVANCED_MOD_DETECTION", "Advanced Mod Detection"},
            
            // Buttons
            {"BUTTON_ALL_BLOCK", "All Block"},
            {"BUTTON_ALL_WARN", "All Warn"},
            {"BUTTON_ALL_OFF", "All Off"},
            {"BUTTON_BLOCK", "Block"},
            {"BUTTON_UNBLOCK", "Unblock"},
            {"BUTTON_KICK", "Kick"},
            
            // Player status
            {"PLAYER_MASTER_CLIENT", "MasterClient"},
            {"PLAYER_ACTOR_NUMBER", "#{0}"},
            
            // Messages
            {"MESSAGE_MANUAL_BLOCK", "Manual block from UI"},
            {"MESSAGE_NO_ANTICHEAT", "No Anticheat installed - can't fetch mods"},
            {"MESSAGE_MODS_TITLE", "Installed Mods:"},
            {"MESSAGE_NO_MODS", "No mods detected"},
            {"MESSAGE_OPTED_OUT", "{0} opted out of advanced mod sharing"}
        };

        public static void Initialize(ConfigEntry<string> languageConfig = null)
        {
            if (_isInitialized) return;

            // Store the config entry
            _languageConfig = languageConfig;

            // Load default translations first
            _translations = new Dictionary<string, string>(_defaultTranslations);
            
            // Try to load language-specific translations
            LoadLanguageTranslations();
            
            _isInitialized = true;
        }

        private static void LoadLanguageTranslations()
        {
            try
            {
                // Get the plugin directory path
                string pluginPath = GetPluginPath();
                if (string.IsNullOrEmpty(pluginPath)) return;

                // Look for translation files in the Assets subdirectory
                string assetsPath = Path.Combine(pluginPath, "Assets");
                if (!Directory.Exists(assetsPath))
                {
                    Debug.LogWarning($"[TranslationManager] Assets directory not found at: {assetsPath}");
                    return;
                }

                string[] translationFiles = Directory.GetFiles(assetsPath, "translations_*.txt");
                
                if (translationFiles.Length == 0)
                {
                    Debug.LogWarning($"[TranslationManager] No translation files found in: {assetsPath}");
                    return;
                }

                // Try to detect language from system or use first available
                string selectedLanguage = DetectLanguage(translationFiles);
                
                // If "en" is selected, don't load any file (use defaults)
                if (selectedLanguage == "en")
                {
                    _currentLanguage = "en";
                    Debug.Log("[TranslationManager] Using English defaults (no translation file loaded)");
                    return;
                }
                
                foreach (string file in translationFiles)
                {
                    string fileName = Path.GetFileNameWithoutExtension(file);
                    if (fileName.StartsWith("translations_"))
                    {
                        string language = fileName.Substring("translations_".Length);
                        if (language.Equals(selectedLanguage, StringComparison.OrdinalIgnoreCase))
                        {
                            LoadTranslationFile(file);
                            _currentLanguage = language;
                            Debug.Log($"[TranslationManager] Loaded translations for language: {language} from {file}");
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TranslationManager] Error loading translations: {ex.Message}");
            }
        }

        private static string GetPluginPath()
        {
            try
            {
                // Try to get the path from the loaded assembly
                string assemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(assemblyPath))
                {
                    return Path.GetDirectoryName(assemblyPath);
                }
                
                // Fallback: try to construct the expected path
                string steamPath = @"C:\Program Files (x86)\Steam\steamapps\common\PEAK\BepInEx\Plugins\hiccup-PEAKAntiCheat";
                if (Directory.Exists(steamPath))
                {
                    return steamPath;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TranslationManager] Error getting plugin path: {ex.Message}");
            }
            
            return null;
        }

        private static string DetectLanguage(string[] translationFiles)
        {
            // Extract language codes from available files
            var availableLanguages = new List<string>();
            foreach (string file in translationFiles)
            {
                string fileName = Path.GetFileNameWithoutExtension(file);
                if (fileName.StartsWith("translations_"))
                {
                    string language = fileName.Substring("translations_".Length);
                    availableLanguages.Add(language);
                }
            }

            // Check if a specific language is configured
            if (_languageConfig != null && !string.IsNullOrEmpty(_languageConfig.Value))
            {
                string configuredLanguage = _languageConfig.Value.ToLower().Trim();
                
                // If "en" is configured, use English defaults (no file loading)
                if (configuredLanguage == "en")
                {
                    return "en";
                }
                
                // Check if the configured language has a translation file
                if (availableLanguages.Contains(configuredLanguage))
                {
                    return configuredLanguage;
                }
                else
                {
                    Debug.LogWarning($"[TranslationManager] Configured language '{configuredLanguage}' not found. Available languages: {string.Join(", ", availableLanguages)}. Falling back to system detection.");
                }
            }

            // Try to detect system language
            string systemLanguage = Application.systemLanguage.ToString().ToLower();
            
            // Map Unity system languages to our language codes
            if (systemLanguage.Contains("spanish") && availableLanguages.Contains("es"))
                return "es";
            if (systemLanguage.Contains("french") && availableLanguages.Contains("fr"))
                return "fr";
            if (systemLanguage.Contains("german") && availableLanguages.Contains("de"))
                return "de";
            if (systemLanguage.Contains("italian") && availableLanguages.Contains("it"))
                return "it";
            if (systemLanguage.Contains("portuguese") && availableLanguages.Contains("pt"))
                return "pt";
            if (systemLanguage.Contains("russian") && availableLanguages.Contains("ru"))
                return "ru";
            if (systemLanguage.Contains("japanese") && availableLanguages.Contains("ja"))
                return "ja";
            if (systemLanguage.Contains("korean") && availableLanguages.Contains("ko"))
                return "ko";
            if (systemLanguage.Contains("chinese") && availableLanguages.Contains("zh"))
                return "zh";

            // Default to English if no translation files available, otherwise first available language
            return availableLanguages.Count > 0 ? availableLanguages.FirstOrDefault() : "en";
        }

        private static void LoadTranslationFile(string filePath)
        {
            try
            {
                string[] lines = File.ReadAllLines(filePath);
                foreach (string line in lines)
                {
                    string trimmedLine = line.Trim();
                    if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("#"))
                        continue;

                    int equalIndex = trimmedLine.IndexOf('=');
                    if (equalIndex > 0)
                    {
                        string key = trimmedLine.Substring(0, equalIndex).Trim();
                        string value = trimmedLine.Substring(equalIndex + 1).Trim();
                        
                        // Remove quotes if present
                        if (value.StartsWith("\"") && value.EndsWith("\""))
                            value = value.Substring(1, value.Length - 2);
                        
                        _translations[key] = value;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[TranslationManager] Error loading translation file {filePath}: {ex.Message}");
            }
        }

        public static string GetTranslation(string key, params object[] args)
        {
            if (!_isInitialized)
                Initialize();

            if (_translations.TryGetValue(key, out string translation))
            {
                if (args.Length > 0)
                {
                    try
                    {
                        return string.Format(translation, args);
                    }
                    catch
                    {
                        return translation;
                    }
                }
                return translation;
            }

            // Fallback to default translations
            if (_defaultTranslations.TryGetValue(key, out string defaultTranslation))
            {
                if (args.Length > 0)
                {
                    try
                    {
                        return string.Format(defaultTranslation, args);
                    }
                    catch
                    {
                        return defaultTranslation;
                    }
                }
                return defaultTranslation;
            }

            // If no translation found, return the key
            return key;
        }

        public static string GetCurrentLanguage()
        {
            return _currentLanguage;
        }

        public static void ReloadTranslations()
        {
            _isInitialized = false;
            Initialize(_languageConfig);
        }

        public static void UpdateLanguageConfig(ConfigEntry<string> newLanguageConfig)
        {
            _languageConfig = newLanguageConfig;
            ReloadTranslations();
        }
    }
} 
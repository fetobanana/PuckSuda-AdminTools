using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;

namespace PuckSuda_AdminTools {
    public class AdminTools : IPuckMod
    {
        private static Harmony _harmony;

        public bool OnEnable()
        {
            // Don't return false here based on Server status, 
            // because the Server might not be started YET when the mod loads.
            try {
                _harmony = new Harmony("com.pucksuda.admintools");
                _harmony.PatchAll(Assembly.GetExecutingAssembly());
                Debug.Log("[PuckSuda] Mod Loaded and Patched successfully.");
                return true;
            }
            catch (Exception e) {
                Debug.LogError($"[PuckSuda] Patching failed: {e.Message}");
                return false;
            }
        }

        public bool OnDisable()
        {
            _harmony?.UnpatchSelf();
            return true;
        }

        // --- Helper Method Moved Inside AdminTools for visibility ---
        public static bool IsGameRunning()
        {
            if (GameManager.Instance == null) return true;
            
            // Block if Phase is not Playing OR if our new pause switch is ON
            if (GameManager.Instance.Phase != GamePhase.Playing || IsPausedByAdmin)
            {
                return false;
            }

            return true;
        }

        public static bool IsPausedByAdmin = false;

    }
    
    // Patch the Pause command
    [HarmonyPatch(typeof(GameManager), "Server_StopGameStateTickCoroutine")]
    public static class PauseWatcher
    {
        [HarmonyPostfix]
        public static void Postfix() => AdminTools.IsPausedByAdmin = true;
    }

    // Patch the Resume command
    [HarmonyPatch(typeof(GameManager), "Server_StartGameStateTickCoroutine")]
    public static class ResumeWatcher
    {
        [HarmonyPostfix]
        public static void Postfix() => AdminTools.IsPausedByAdmin = false;
    }

    [HarmonyPatch(typeof(Goal), "Server_OnPuckEnterGoal")]
    public static class GoalBlockerPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Puck puck) // Ensure 'Puck puck' is here to match Goal.cs
        {
            if (AdminTools.IsGameRunning())
            {
                return true; // Allow original logic
            }

            Debug.Log("[PuckSuda] Goal BLOCKED.");
            return false; // Stop the goal from being processed
        }
    }
    
    [HarmonyPatch(typeof(GameManagerController), "Event_Server_OnChatCommand")]
    public static class ChatCommandPatch
    {
        [HarmonyPrefix]
        public static void Prefix(Dictionary<string, object> message)
        {
            // Perform the Server check HERE, at the moment the command is typed.
            if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;

            if (!message.ContainsKey("clientId") || !message.ContainsKey("command")) return;

            ulong clientId = (ulong)message["clientId"];
            string command = ((string)message["command"]).ToLower();
            string[] args = message.ContainsKey("args") ? (string[])message["args"] : new string[0];

            // 1. Get Player
            var player = NetworkBehaviourSingleton<PlayerManager>.Instance.GetPlayerByClientId(clientId);
            if (player == null) return;

            // 2. Admin Check (Using the exact logic from GameManagerController.cs)
            // Note: We use .ToString() on SteamId.Value to match the List<string> AdminSteamIds
            bool isHost = clientId == NetworkManager.ServerClientId;
            bool isListAdmin = NetworkBehaviourSingleton<ServerManager>.Instance.AdminSteamIds.Contains(player.SteamId.Value.ToString());
            
            if (!isHost && !isListAdmin) return;

            // 3. Process Custom Commands
            ProcessAdminCommands(command, args);
        }

        private static void ProcessAdminCommands(string cmd, string[] args)
        {
            // /faceoff
            if (cmd == "/faceoff")
            {
                var phase = GameManager.Instance.Phase;
                if (phase == GamePhase.Playing || phase == GamePhase.Warmup)
                {
                    // 1. Capture the current timer value from the GameState
                    int currentTime = GameManager.Instance.GameState.Value.Time;

                    // if current time bigger than phase
                    if (currentTime > GameManager.Instance.PhaseDurationMap[GamePhase.Playing])
                    {
                        currentTime = GameManager.Instance.PhaseDurationMap[GamePhase.Playing];
                    }

                    // 2. Access the private 'remainingPlayTime' field using Reflection
                    // This ensures that when the Faceoff ends, the clock resumes at 'currentTime'
                    var field = typeof(GameManager).GetField("remainingPlayTime", 
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        
                    if (field != null)
                    {
                        field.SetValue(GameManager.Instance, currentTime);
                    }

                    // 3. Set the phase to FaceOff
                    // Passing -1 allows the game to use its PhaseDurationMap for the faceoff length
                    GameManager.Instance.Server_SetPhase(GamePhase.FaceOff, -1);

                    string msg = "triggered a faceoff";
                    if ( AdminTools.IsPausedByAdmin ) 
                    {
                        GameManager.Instance.Server_StartGameStateTickCoroutine(); //resumes the clock, unpauses, i think
                        msg += " and resumed the timer";
                    }
                    LogFeedback (msg);

                }
                else LogFeedback("can't trigger a faceoff during phase '" + phase.ToString() + "'");
            }
            // /set [rs/bs/p/t] [value]
            else if (cmd == "/set" || cmd == "/s")
            {
                if (args.Length < 2) return;
                string sub = args[0].ToLower();
                string valStr = args[1];

                if (sub == "rs" || sub == "redscore")
                {
                    if (int.TryParse(valStr, out int val)) {
                        GameManager.Instance.Server_UpdateGameState(redScore: val);
                        LogFeedback($"set Red Score to {val}");
                    }
                }
                else if (sub == "bs" || sub == "bluescore")
                {
                    if (int.TryParse(valStr, out int val)) {
                        GameManager.Instance.Server_UpdateGameState(blueScore: val);
                        LogFeedback($"set Blue Score to {val}");
                    }
                }
                else if (sub == "p" || sub == "period")
                {
                    if (int.TryParse(valStr, out int val)) {
                        GameManager.Instance.Server_UpdateGameState(period: val);
                        LogFeedback($"set Period to {val}");
                    }
                }
                else if (sub == "t" || sub == "timer")
                {
                    int totalSeconds = ParseTime(valStr);
                    if (totalSeconds >= 0) {
                        GameManager.Instance.Server_UpdateGameState(time: totalSeconds);
                        LogFeedback($"set Timer to {valStr}");
                    }
                }
            }
            else if (cmd == "/endgame")
            {
                GameManager.Instance.Server_GameOver();
                LogFeedback("triggered a game end");
            }
        }

        private static void LogFeedback(string action)
        {
            string msg = $"<b><color=orange>ADMIN</color></b> {action}.";
            NetworkBehaviourSingleton<UIChat>.Instance.Server_SendSystemChatMessage(msg);
        }

        private static int ParseTime(string time)
        {
            if (time.Contains(":"))
            {
                var parts = time.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[0], out int m) && int.TryParse(parts[1], out int s))
                    return (m * 60) + s;
            }
            return int.TryParse(time, out int res) ? res : -1;
        }
        
    }
}
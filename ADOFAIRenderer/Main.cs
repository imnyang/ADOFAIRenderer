using System;
using System.Globalization;
using HarmonyLib;
using UnityModManagerNet;
using UnityEngine;
using UnityEngine.SceneManagement;
using ADOFAIRenderer.Renderer;
using ADOFAIRenderer.UI;
namespace ADOFAIRenderer
{
    public static class Main
    {
        internal static UnityModManager.ModEntry Entry;
        internal static bool Enabled;
        internal static bool RpcEnabled { get; private set; }
        internal static int RpcPort { get; private set; } = 1108;
        internal static RendererRpcServer RpcServer { get; private set; }
        internal static RendererSettings Settings;
        private static Harmony harmony;
        private static GameObject host;

        public static bool Load(UnityModManager.ModEntry entry)
        {
            Entry = entry;
            try
            {
                ReadCommandLineOptions();
                Settings = RendererSettings.Load(entry);
                Settings.OnChange();
                harmony = new Harmony(entry.Info.Id);
                harmony.PatchAll(typeof(Main).Assembly);
                host = new GameObject("ADOFAI Renderer");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.AddComponent<RendererController>();
                SceneManager.sceneLoaded += OnSceneLoaded;
                Enabled = true;
                StartRpcServer();
                FfmpegInstaller.Start(entry, Settings);
                UpdateManager.Start(entry);
                entry.OnToggle = (mod, enabled) =>
                {
                    if (!enabled) RendererController.Instance?.StopAndClean();
                    Enabled = enabled;
                    if (!enabled) StopRpcServer();
                    else StartRpcServer();
                    return true;
                };
                entry.OnGUI = mod =>
                {
                    if (Settings == null) return;
                    UnityModManager.UI.DrawFields(ref Settings, mod, DrawFieldMask.Any, Settings.OnChange);
                };
                entry.OnUpdate = (mod, deltaTime) => UpdateManager.PumpMainThread();
                entry.OnSaveGUI = mod => Settings?.Save(mod);
                entry.OnUnload = mod =>
                {
                    RendererController.Instance?.StopAndClean();
                    StopRpcServer();
                    SceneManager.sceneLoaded -= OnSceneLoaded;
                    UnityEngine.Object.Destroy(host);
                    harmony.UnpatchAll(mod.Info.Id);
                    Settings = null;
                    return true;
                };
                entry.Logger.Log("Renderer loaded. Unity " + Application.unityVersion);
                return true;
            }
            catch (Exception ex)
            {
                if (host != null) UnityEngine.Object.Destroy(host);
                harmony?.UnpatchAll(entry.Info.Id);
                entry.Logger.Error(ex.ToString());
                return false;
            }
        }

        private static void ReadCommandLineOptions()
        {
            RpcEnabled = false;
            RpcPort = 1108;
            foreach (var argument in Environment.GetCommandLineArgs())
            {
                if (string.Equals(argument, "--renderer-rpc", StringComparison.OrdinalIgnoreCase))
                {
                    RpcEnabled = true;
                    continue;
                }
                const string prefix = "--renderer-rpc-port=";
                if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(argument.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                    && port >= 1 && port <= 65535)
                {
                    RpcPort = port;
                    RpcEnabled = true;
                }
            }
        }

        private static void StartRpcServer()
        {
            if (!RpcEnabled || RpcServer != null || RendererController.Instance == null) return;
            try
            {
                RpcServer = new RendererRpcServer(RendererController.Instance, RpcPort);
                RpcServer.Start();
            }
            catch (Exception ex)
            {
                RpcServer = null;
                Entry.Logger.Error("Renderer RPC could not start on port " + RpcPort + ": " + ex);
            }
        }

        private static void StopRpcServer()
        {
            var server = RpcServer;
            RpcServer = null;
            try { server?.Dispose(); } catch (Exception ex) { Entry.Logger.Error("Renderer RPC shutdown: " + ex); }
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!Enabled) return;
            // ADOFAI's KillAll cleanup can remove DontDestroyOnLoad objects
            // while switching between menu, editor, and gameplay scenes.
            // Recreate our tiny host so RPC jobs survive that transition.
            if (RendererController.Instance == null)
            {
                host = new GameObject("ADOFAI Renderer");
                UnityEngine.Object.DontDestroyOnLoad(host);
                host.AddComponent<RendererController>();
                RpcServer?.Rebind(RendererController.Instance);
                Entry.Logger.Log("Renderer host recreated after scene load: " + scene.name);
            }
        }
    }
}

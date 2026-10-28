using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
#if FIGCRAFT_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif
using Debug = UnityEngine.Debug;

namespace FigCraftToUnity.Editor
{
    /// <summary>
    /// Shortcuts for working (and demoing) the round trip with FigCraft:
    /// - Ctrl+Shift+Alt+F also works in Play Mode with the Game view focused. Unity does not pass menu shortcuts
    ///   through there, so the capture never started. Needs the Input System package.
    /// - Open the master document in FigCraft from Unity.
    /// </summary>
    [InitializeOnLoad]
    static class FigCraftShortcuts
    {
#if FIGCRAFT_INPUT_SYSTEM
        static double _lastCapture;

        static FigCraftShortcuts()
        {
            EditorApplication.update += WatchShortcutInPlayMode;
        }

        static void WatchShortcutInPlayMode()
        {
            if (!EditorApplication.isPlaying) return;
            var kb = Keyboard.current;
            if (kb == null || !kb.fKey.wasPressedThisFrame) return;
            if (!kb.ctrlKey.isPressed || !kb.shiftKey.isPressed || !kb.altKey.isPressed) return;

            // The Editor update runs more often than game frames (and if Unity does pass the menu shortcut,
            // both would fire): one capture per key press.
            if (EditorApplication.timeSinceStartup - _lastCapture < 1.0) return;
            _lastCapture = EditorApplication.timeSinceStartup;

            FigCraftCapture.Capture(null);
        }
#endif

        [MenuItem("Tools/FigCraft/Open Master Document in FigCraft", priority = 3)]
        static void OpenMaster()
        {
            var master = EditorPrefs.GetString(FigCraftCapture.PrefTarget, "");
            if (string.IsNullOrEmpty(master) || !File.Exists(master))
            {
                EditorUtility.DisplayDialog("FigCraft",
                    "There is no master document set up (or it does not exist).\n\n" +
                    "Choose it in Tools > FigCraft > Set Up Automatic Import...", "OK");
                return;
            }

            try
            {
                // Opened by the program associated with .figcraft (FigCraft).
                Process.Start(new ProcessStartInfo(master) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FigCraft] Could not open {master} ({ex.Message}); showing the file instead.");
                EditorUtility.RevealInFinder(master);
            }
        }
    }
}

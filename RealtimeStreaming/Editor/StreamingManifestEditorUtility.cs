using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using ZGConnect;

namespace ZGConnect.RealtimeStreaming.Editor
{
    public static class StreamingManifestEditorUtility
    {
        public readonly struct BasemapOption
        {
            public readonly string Id;
            public readonly string Label;
            public readonly BasemapType Type;

            public BasemapOption(string id, string label, BasemapType type)
            {
                Id = id;
                Label = label;
                Type = type;
            }
        }

        public static bool TryGetBasemapOptions(
            string manifestRelativePath,
            bool orthoOnly,
            out BasemapOption[] options)
        {
            options = Array.Empty<BasemapOption>();
            if (!TryLoadManifest(manifestRelativePath, out StreamingDatasetManifest manifest))
                return false;

            return TryGetBasemapOptions(manifest, orthoOnly, out options);
        }

        public static bool TryGetBasemapOptions(
            StreamingDatasetManifest manifest,
            bool orthoOnly,
            out BasemapOption[] options)
        {
            options = Array.Empty<BasemapOption>();
            if (manifest?.AvailableBasemaps == null || manifest.AvailableBasemaps.Count == 0)
                return false;

            var list = new List<BasemapOption>();
            foreach (StreamingBasemapEntry entry in manifest.AvailableBasemaps)
            {
                if (entry == null || string.IsNullOrEmpty(entry.Id))
                    continue;

                BasemapType type = entry.GetBasemapType();
                if (orthoOnly && type != BasemapType.Ortho)
                    continue;

                list.Add(new BasemapOption(entry.Id, BuildLabel(entry), type));
            }

            if (list.Count == 0)
                return false;

            options = list.ToArray();
            return true;
        }

        public static void DrawBasemapIdPopup(
            SerializedProperty property,
            string manifestRelativePath,
            StreamingDatasetManifest playModeManifest,
            bool orthoOnly)
        {
            if (property == null || property.propertyType != SerializedPropertyType.String)
            {
                EditorGUILayout.PropertyField(property);
                return;
            }

            BasemapOption[] options = Array.Empty<BasemapOption>();
            bool hasOptions = playModeManifest != null
                ? TryGetBasemapOptions(playModeManifest, orthoOnly, out options)
                : TryGetBasemapOptions(manifestRelativePath, orthoOnly, out options);

            bool usedOrthoFallback = false;
            if (!hasOptions && orthoOnly)
            {
                hasOptions = playModeManifest != null
                    ? TryGetBasemapOptions(playModeManifest, orthoOnly: false, out options)
                    : TryGetBasemapOptions(manifestRelativePath, orthoOnly: false, out options);
                usedOrthoFallback = hasOptions;
            }

            if (!hasOptions)
            {
                EditorGUILayout.PropertyField(property);
                EditorGUILayout.HelpBox(
                    orthoOnly
                        ? "No ortho basemaps found in manifest. Check Manifest Relative Path under StreamingAssets."
                        : "No basemaps found in manifest. Check Manifest Relative Path under StreamingAssets.",
                    MessageType.Info);
                return;
            }

            if (usedOrthoFallback)
            {
                EditorGUILayout.HelpBox(
                    "No ortho basemaps in manifest â€” showing all basemap types. Roof orthophoto works best with an ortho layer.",
                    MessageType.Info);
            }

            string current = property.stringValue ?? string.Empty;
            int selectedIndex = IndexOfOption(options, current);
            bool currentMissing = !string.IsNullOrEmpty(current) && selectedIndex < 0;

            string[] labels = BuildPopupLabels(options, currentMissing ? current : null);
            int popupIndex = selectedIndex;
            if (currentMissing)
                popupIndex = 0;
            else if (popupIndex < 0)
                popupIndex = 0;

            EditorGUI.BeginChangeCheck();
            int newPopupIndex = EditorGUILayout.Popup(property.displayName, popupIndex, labels);
            if (EditorGUI.EndChangeCheck())
            {
                int optionIndex = currentMissing ? newPopupIndex - 1 : newPopupIndex;
                optionIndex = Mathf.Clamp(optionIndex, 0, options.Length - 1);
                property.stringValue = options[optionIndex].Id;
            }

            if (currentMissing)
            {
                EditorGUILayout.HelpBox(
                    $"Current value '{current}' is not listed in the dataset manifest.",
                    MessageType.Warning);
            }
        }

        static bool TryLoadManifest(string manifestRelativePath, out StreamingDatasetManifest manifest)
        {
            manifest = null;
            string rel = string.IsNullOrEmpty(manifestRelativePath)
                ? RuntimeStreamingPaths.DefaultManifestRelativePath
                : manifestRelativePath;

            string path = RuntimeStreamingPaths.ManifestPath(rel);
            if (!File.Exists(path))
                return false;

            try
            {
                manifest = StreamingDatasetManifest.LoadFromFile(path);
                return manifest != null;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ZGConnect.Realtime] Failed to read manifest for inspector: {ex.Message}");
                return false;
            }
        }

        static string BuildLabel(StreamingBasemapEntry entry)
        {
            string typeLabel = entry.GetBasemapType() == BasemapType.Tiled ? "Tiled" : "Ortho";
            if (!string.IsNullOrEmpty(entry.DisplayName))
                return $"{entry.DisplayName} ({entry.Id}, {typeLabel})";
            return $"{entry.Id} ({typeLabel})";
        }

        static string[] BuildPopupLabels(BasemapOption[] options, string missingCurrentValue)
        {
            if (string.IsNullOrEmpty(missingCurrentValue))
            {
                var labels = new string[options.Length];
                for (int i = 0; i < options.Length; i++)
                    labels[i] = options[i].Label;
                return labels;
            }

            var withMissing = new string[options.Length + 1];
            withMissing[0] = $"(missing) {missingCurrentValue}";
            for (int i = 0; i < options.Length; i++)
                withMissing[i + 1] = options[i].Label;
            return withMissing;
        }

        static int IndexOfOption(BasemapOption[] options, string id)
        {
            if (string.IsNullOrEmpty(id))
                return -1;

            for (int i = 0; i < options.Length; i++)
            {
                if (options[i].Id == id)
                    return i;
            }

            return -1;
        }
    }
}


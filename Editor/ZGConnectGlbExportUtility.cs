using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace ZGConnect.Editor
{
    /// <summary>
    /// Optional UnityGLTF export (com.khronos.unitygltf). Used by building exporter and bake tools.
    /// </summary>
    public static class ZGConnectGlbExportUtility
    {
        private static bool            s_checked;
        private static ConstructorInfo s_exporterCtor;
        private static MethodInfo      s_saveGlbMethod;
        private static System.Type     s_exportContextType;
        private static FieldInfo       s_beforeMaterialExportField;
        private static FieldInfo       s_beforeTextureExportField;

        public static bool IsAvailable
        {
            get
            {
                EnsureChecked();
                return s_saveGlbMethod != null;
            }
        }

        /// <summary>Full export including materials and embedded textures.</summary>
        public static void ExportRoot(GameObject root, string glbPath) =>
            ExportRootInternal(root, glbPath, geometryOnly: false);

        /// <summary>Mesh + UVs only; materials/textures stripped before export (slot keys go to JSON).</summary>
        public static void ExportRootGeometryOnly(GameObject root, string glbPath) =>
            ExportRootInternal(root, glbPath, geometryOnly: true);

        /// <summary>Counts mesh filters with non-empty geometry under <paramref name="root"/>.</summary>
        public static int CountExportableMeshes(GameObject root)
        {
            if (root == null)
                return 0;

            int count = 0;
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = mf.sharedMesh;
                if (mesh != null && mesh.vertexCount > 0)
                    count++;
            }

            return count;
        }

        private static void ExportRootInternal(GameObject root, string glbPath, bool geometryOnly)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));

            EnsureChecked();
            if (s_saveGlbMethod == null)
                throw new InvalidOperationException(
                    "UnityGLTF is not installed. Add package com.khronos.unitygltf via Package Manager.");

            if (CountExportableMeshes(root) == 0)
                throw new InvalidOperationException(
                    $"No exportable mesh geometry under '{root.name}'. GLB export aborted.");

            string dir = Path.GetDirectoryName(glbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            string fileName = Path.GetFileNameWithoutExtension(glbPath);

            bool wasActive = root.activeSelf;
            if (!wasActive) root.SetActive(true);

            using var hideScope = new HideFlagsRestoreScope(root);
            using var materialScope = geometryOnly ? new MaterialStripScope(root) : null;

            try
            {
                object context = geometryOnly ? CreateGeometryOnlyExportContext() : CreateDefaultExportContext();
                object[] ctorArgs = context != null
                    ? new object[] { new Transform[] { root.transform }, context }
                    : new object[] { new Transform[] { root.transform } };

                object exporter = s_exporterCtor.Invoke(ctorArgs);
                s_saveGlbMethod.Invoke(exporter, new object[] { dir, fileName });
            }
            finally
            {
                if (!wasActive) root.SetActive(false);
            }
        }

        private static object CreateDefaultExportContext()
        {
            if (s_exportContextType == null || s_exporterCtor.GetParameters().Length < 2)
                return null;

            try { return Activator.CreateInstance(s_exportContextType); }
            catch { return null; }
        }

        private static object CreateGeometryOnlyExportContext()
        {
            if (s_exportContextType == null)
                return null;

            object context;
            try { context = Activator.CreateInstance(s_exportContextType); }
            catch { return null; }

            if (context == null)
                return null;

            // Skip texture embedding only — do not skip materials/meshes (null mats drop geometry).
            TryAssignTextureSkipDelegate(context);
            return context;
        }

        private static void TryAssignTextureSkipDelegate(object context)
        {
            if (context == null)
                return;

            System.Type exporterType = s_exporterCtor.DeclaringType;
            if (exporterType == null)
                return;

            if (s_beforeTextureExportField != null)
            {
                Delegate texDel = CreateTextureSkipDelegate(exporterType);
                if (texDel != null)
                    s_beforeTextureExportField.SetValue(context, texDel);
            }
        }

        private static Delegate CreateTextureSkipDelegate(System.Type exporterType)
        {
            System.Type delegateType = exporterType.GetNestedType(
                "BeforeTextureExportDelegate", BindingFlags.Public | BindingFlags.NonPublic);
            if (delegateType == null)
                return null;

            MethodInfo stub = typeof(ZGConnectGlbExportUtility).GetMethod(
                nameof(SkipTextureExportStub), BindingFlags.Static | BindingFlags.NonPublic);
            if (stub == null)
                return null;

            try { return Delegate.CreateDelegate(delegateType, stub); }
            catch { return null; }
        }

        // UnityGLTF: return true = export handled (skip default texture embed).
        private static bool SkipTextureExportStub(object exporter, ref object texture, string textureSlot)
        {
            texture = null;
            return true;
        }

        private static Material s_exportPlaceholder;

        private static Material ExportPlaceholderMaterial()
        {
            if (s_exportPlaceholder != null)
                return s_exportPlaceholder;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
                shader = Shader.Find("Standard");
            if (shader == null)
                shader = Shader.Find("Hidden/InternalErrorShader");

            s_exportPlaceholder = new Material(shader)
            {
                name = "ZGConnect_ExportPlaceholder",
                hideFlags = HideFlags.HideAndDontSave,
            };
            return s_exportPlaceholder;
        }

        private sealed class MaterialStripScope : IDisposable
        {
            readonly List<(MeshRenderer renderer, Material[] materials)> _backup =
                new List<(MeshRenderer, Material[])>();

            public MaterialStripScope(GameObject root)
            {
                Material placeholder = ExportPlaceholderMaterial();
                foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    Material[] mats = renderer.sharedMaterials;
                    _backup.Add((renderer, mats));

                    int slotCount = Mathf.Max(1, mats.Length);
                    var stripped = new Material[slotCount];
                    for (int i = 0; i < slotCount; i++)
                        stripped[i] = placeholder;
                    renderer.sharedMaterials = stripped;
                }
            }

            public void Dispose()
            {
                foreach ((MeshRenderer renderer, Material[] materials) in _backup)
                {
                    if (renderer != null)
                        renderer.sharedMaterials = materials;
                }
            }
        }

        /// <summary>UnityGLTF skips hidden hierarchies — clear HideInHierarchy for export.</summary>
        private sealed class HideFlagsRestoreScope : IDisposable
        {
            readonly List<(GameObject go, HideFlags flags)> _backup =
                new List<(GameObject, HideFlags)>();

            public HideFlagsRestoreScope(GameObject root)
            {
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    GameObject go = t.gameObject;
                    _backup.Add((go, go.hideFlags));
                    go.hideFlags &= ~HideFlags.HideInHierarchy;
                }
            }

            public void Dispose()
            {
                foreach ((GameObject go, HideFlags flags) in _backup)
                {
                    if (go != null)
                        go.hideFlags = flags;
                }
            }
        }

        private static void EnsureChecked()
        {
            if (s_checked) return;
            s_checked = true;

            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!asm.GetName().Name.StartsWith("UnityGLTF"))
                    continue;

                System.Type exporterType = asm.GetType("UnityGLTF.GLTFSceneExporter");
                if (exporterType == null)
                    continue;

                MethodInfo saveMethod = exporterType.GetMethod(
                    "SaveGLB",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(string), typeof(string) },
                    null);
                if (saveMethod == null)
                    continue;

                System.Type contextType = asm.GetType("UnityGLTF.ExportContext");
                ConstructorInfo ctor = contextType != null
                    ? exporterType.GetConstructor(new[] { typeof(Transform[]), contextType })
                    : null;

                if (ctor == null)
                    ctor = exporterType.GetConstructor(new[] { typeof(Transform[]) });

                if (ctor == null)
                    continue;

                s_exporterCtor              = ctor;
                s_saveGlbMethod             = saveMethod;
                s_exportContextType         = contextType;
                s_beforeMaterialExportField = contextType?.GetField(
                    "BeforeMaterialExport", BindingFlags.Public | BindingFlags.Instance);
                s_beforeTextureExportField = contextType?.GetField(
                    "BeforeTextureExport", BindingFlags.Public | BindingFlags.Instance);
                return;
            }
        }
    }
}

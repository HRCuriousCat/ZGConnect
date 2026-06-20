using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace ZGConnect.SpatialStreaming
{
    public readonly struct SpatialRendererCompatibilityIssue
    {
        public readonly string RendererName;
        public readonly RenderingMode RenderingMode;
        public readonly string PipelineAssetName;

        public SpatialRendererCompatibilityIssue(
            string rendererName,
            RenderingMode renderingMode,
            string pipelineAssetName)
        {
            RendererName = rendererName;
            RenderingMode = renderingMode;
            PipelineAssetName = pipelineAssetName;
        }

        public bool IsCompatible => RenderingMode is RenderingMode.ForwardPlus or RenderingMode.DeferredPlus;

        public string DescribeRequiredMode() =>
            RenderingMode switch
            {
                RenderingMode.Forward => "Forward → Forward+ or Deferred+",
                RenderingMode.Deferred => "Deferred → Deferred+ (or use Forward+)",
                _ => $"{RenderingMode} → Forward+ or Deferred+",
            };
    }

    public static class SpatialGpuResidentRendererCompatibility
    {
        public static List<SpatialRendererCompatibilityIssue> FindIncompatibleRenderers(
            UniversalRenderPipelineAsset pipeline)
        {
            var issues = new List<SpatialRendererCompatibilityIssue>();
            if (pipeline == null)
                return issues;

            foreach (ScriptableRendererData rendererData in pipeline.rendererDataList)
            {
                if (rendererData is not UniversalRendererData universalRenderer)
                    continue;

                if (universalRenderer.usesClusterLightLoop)
                    continue;

                issues.Add(new SpatialRendererCompatibilityIssue(
                    universalRenderer.name,
                    universalRenderer.renderingMode,
                    pipeline.name));
            }

            return issues;
        }

        public static string FormatIssueList(IReadOnlyList<SpatialRendererCompatibilityIssue> issues)
        {
            if (issues == null || issues.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            for (int i = 0; i < issues.Count; i++)
            {
                SpatialRendererCompatibilityIssue issue = issues[i];
                if (i > 0)
                    sb.Append("; ");

                sb.Append(issue.RendererName);
                sb.Append(" (");
                sb.Append(issue.RenderingMode);
                sb.Append(" in ");
                sb.Append(issue.PipelineAssetName);
                sb.Append(')');
            }

            return sb.ToString();
        }
    }
}

using UnityEngine;

namespace ZGConnect.RealtimeStreaming
{
    internal static class RuntimeObjectUtility
    {
        public static void Destroy(Object obj)
        {
            if (obj == null)
                return;

#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                Object.DestroyImmediate(obj);
                return;
            }
#endif
            Object.Destroy(obj);
        }
    }
}


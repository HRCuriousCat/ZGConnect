using System.Collections;

using TMPro;

using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using ZGConnect.RealtimeStreaming;



namespace ZGConnect
{
    /// <summary>
    /// Displays a title and progress bar while loading the runtime streaming scene.
    /// Additively loads the target scene, waits for initial terrain tiles and buildings, then switches to it.
    /// </summary>
    public class SceneLoadingController : MonoBehaviour
    {
        /// <summary>True while Intro is additively loading and waiting to hand off to Runtime_Streaming.</summary>
        public static bool IntroLoadHandoffActive { get; private set; }



        [Header("Next scene")]
        [Tooltip("Scene name in Build Settings (used when Load By Build Index is off).")]
        [SerializeField] private string nextSceneName = "Runtime_Streaming";
        [SerializeField] private bool loadByBuildIndex;
        [SerializeField] private int nextSceneBuildIndex = 1;



        [Header("Load mode")]
        [Tooltip("Load the next scene additively, wait for terrain tiles and buildings, then activate and unload this scene.")]
        [SerializeField] private bool additiveLoadWithStreamingProgress = true;
        [Tooltip("Share of the progress bar used while the scene file loads (rest = terrain + building tiles).")]
        [Range(0.05f, 0.5f)]
        [SerializeField] private float sceneLoadProgressWeight = 0.15f;



        [Header("Presentation")]
        [SerializeField] private string projectTitle = "ZG Connect";
        [Tooltip("Minimum time the loading screen stays visible before switching scenes.")]
        [SerializeField] private float minDisplaySeconds = 0.75f;



        [Header("UI")]
        [SerializeField] private Text titleText;
        [SerializeField] private Slider progressSlider;
        [SerializeField] private Text progressPercentText;

        [Tooltip("Optional status line: Stage label \"detail\" e.g. Loading terrains \"25/50 - 50%\".")]

        [SerializeField] private TMP_Text loadingStatusText;



        GameObject _persistedUiRoot;
        Coroutine _loadRoutine;
        Scene _introScene;



        private void Start()
        {
            if (titleText != null)
                titleText.text = projectTitle;



            SetProgress(0f);

            SetLoadingStatus("Loading scene", "0%");

            if (progressSlider != null)
            {
                progressSlider.minValue = 0f;
                progressSlider.maxValue = 1f;
                progressSlider.interactable = false;
            }



            _loadRoutine = StartCoroutine(LoadNextSceneRoutine());
        }



        private IEnumerator LoadNextSceneRoutine()
        {
            if (additiveLoadWithStreamingProgress)
            {
                yield return LoadStreamingSceneAdditiveRoutine();
                yield break;
            }



            yield return LoadSingleSceneRoutine();
        }



        private IEnumerator LoadStreamingSceneAdditiveRoutine()
        {
            IntroLoadHandoffActive = true;
            try
            {
                yield return LoadStreamingSceneAdditiveRoutineCore();
            }
            finally
            {
                IntroLoadHandoffActive = false;
            }
        }



        private IEnumerator LoadStreamingSceneAdditiveRoutineCore()
        {
            string sceneName = ResolveTargetSceneName();
            if (string.IsNullOrEmpty(sceneName))
            {
                Debug.LogError("[ZGConnect] SceneLoadingController: no target scene configured.");
                yield break;
            }



            // Capture before PersistLoadingUi moves this controller into DontDestroyOnLoad.
            _introScene = gameObject.scene;



            PersistLoadingUi();
            float shownSince = Time.unscaledTime;



            Debug.Log($"[ZGConnect] Additive load of '{sceneName}' with streaming progress...");



            AsyncOperation loadOp = loadByBuildIndex
                ? SceneManager.LoadSceneAsync(nextSceneBuildIndex, LoadSceneMode.Additive)
                : SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);



            if (loadOp == null)
            {
                Debug.LogError(
                    $"[ZGConnect] SceneLoadingController: could not start additive load for '{sceneName}'. " +
                    "Add the scene to File → Build Profiles / Build Settings.");
                yield break;
            }



            loadOp.allowSceneActivation = true;



            while (!loadOp.isDone)
            {
                float sceneProgress = Mathf.Clamp01(loadOp.progress / 0.9f);
                SetProgress(sceneProgress * sceneLoadProgressWeight);

                SetLoadingStatus("Loading scene", $"{Mathf.RoundToInt(sceneProgress * 100f)}%");

                yield return null;
            }



            Scene streamingScene = loadByBuildIndex
                ? SceneManager.GetSceneByBuildIndex(nextSceneBuildIndex)
                : SceneManager.GetSceneByName(sceneName);



            if (!streamingScene.IsValid() || !streamingScene.isLoaded)
            {
                Debug.LogError($"[ZGConnect] SceneLoadingController: '{sceneName}' did not load.");
                yield break;

            }



            SetLoadingStatus("Starting streaming");



            RealtimeStreamingController streamer = null;
            const float findTimeoutSeconds = 10f;
            float findStarted = Time.unscaledTime;
            while (streamer == null && Time.unscaledTime - findStarted < findTimeoutSeconds)
            {

                SetLoadingStatus("Starting streaming", "waiting for streamer");

                streamer = FindStreamerInScene(streamingScene);
                yield return null;
            }



            if (streamer == null)
            {
                Debug.LogError(
                    $"[ZGConnect] SceneLoadingController: RealtimeStreamingController not found in '{sceneName}'.");
                yield break;
            }



            streamer.ApplyInitialCameraFocus();
            streamer.SetRuntimeRenderingSuppressed(true);



            while (true)
            {
                StreamingLoadProgress loadProgress = streamer.GetInitialLoadProgress();

                StreamingLoadStatus loadStatus = streamer.GetInitialLoadStatus();

                float normalized = sceneLoadProgressWeight +
                                   loadProgress.NormalizedProgress * (1f - sceneLoadProgressWeight);
                SetProgress(normalized);

                SetLoadingStatus(loadStatus.StageLabel, loadStatus.StageDetail);



                bool minTimeElapsed = Time.unscaledTime - shownSince >= minDisplaySeconds;
                if (streamer.IsIntroInitialLoadComplete() && minTimeElapsed)
                    break;



                yield return null;
            }



            SetProgress(1f);

            SetLoadingStatus("Ready", "100%");

            yield return null;



            DisableIntroSceneCameras();



            streamer.ApplyInitialCameraFocus();
            streamer.ReleaseStreamingPresentation();



            SceneManager.SetActiveScene(streamingScene);



            if (_introScene.IsValid() && _introScene.isLoaded && _introScene != streamingScene)
                yield return SceneManager.UnloadSceneAsync(_introScene);



            if (_persistedUiRoot != null)
                Destroy(_persistedUiRoot);



            Destroy(gameObject);
        }



        void DisableIntroSceneCameras()
        {
            SetSceneCamerasActive(_introScene, false);
        }



        static void SetSceneCamerasActive(Scene scene, bool active)
        {
            if (!scene.IsValid() || !scene.isLoaded)
                return;



            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Camera camera in root.GetComponentsInChildren<Camera>(true))
                    camera.gameObject.SetActive(active);
            }
        }



        private IEnumerator LoadSingleSceneRoutine()
        {
            string sceneLabel = loadByBuildIndex
                ? $"build index {nextSceneBuildIndex}"
                : $"'{ResolveTargetSceneName()}'";



            Debug.Log($"[ZGConnect] Loading scene {sceneLabel}...");



            AsyncOperation loadOp = null;
            if (loadByBuildIndex)
                loadOp = SceneManager.LoadSceneAsync(nextSceneBuildIndex);



            if (loadOp == null && !string.IsNullOrEmpty(nextSceneName))
                loadOp = SceneManager.LoadSceneAsync(nextSceneName);



            if (loadOp == null)
            {
                Debug.LogError(
                    $"[ZGConnect] SceneLoadingController: could not start load for {sceneLabel}. " +
                    "Add the scene to File → Build Profiles / Build Settings.");
                yield break;
            }



            loadOp.allowSceneActivation = false;
            float shownSince = Time.unscaledTime;



            while (!loadOp.isDone)
            {
                float progress = Mathf.Clamp01(loadOp.progress / 0.9f);
                SetProgress(progress);

                SetLoadingStatus("Loading scene", $"{Mathf.RoundToInt(progress * 100f)}%");



                bool loadReady = loadOp.progress >= 0.9f;
                bool minTimeElapsed = Time.unscaledTime - shownSince >= minDisplaySeconds;



                if (loadReady && minTimeElapsed)
                    loadOp.allowSceneActivation = true;



                yield return null;
            }
        }



        private string ResolveTargetSceneName()
        {
            if (!string.IsNullOrEmpty(nextSceneName))
                return nextSceneName;



            if (!loadByBuildIndex)
                return null;



            string path = SceneUtility.GetScenePathByBuildIndex(nextSceneBuildIndex);
            return string.IsNullOrEmpty(path) ? null : System.IO.Path.GetFileNameWithoutExtension(path);
        }



        private void PersistLoadingUi()
        {
            if (_persistedUiRoot != null)
                return;



            if (progressSlider != null)
            {
                Canvas canvas = progressSlider.GetComponentInParent<Canvas>();

                if (canvas != null)

                    _persistedUiRoot = canvas.rootCanvas.gameObject;

            }



            if (_persistedUiRoot == null && loadingStatusText != null)

            {

                Canvas canvas = loadingStatusText.GetComponentInParent<Canvas>();

                if (canvas != null)
                    _persistedUiRoot = canvas.rootCanvas.gameObject;
            }



            if (_persistedUiRoot == null)
                _persistedUiRoot = gameObject;



            DontDestroyOnLoad(_persistedUiRoot);



            if (gameObject != _persistedUiRoot && gameObject.scene.isLoaded)
                DontDestroyOnLoad(gameObject);
        }



        private static RealtimeStreamingController FindStreamerInScene(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
                return null;



            foreach (GameObject root in scene.GetRootGameObjects())
            {
                RealtimeStreamingController streamer =
                    root.GetComponentInChildren<RealtimeStreamingController>(true);
                if (streamer != null)
                    return streamer;
            }



            return null;
        }



        private void SetProgress(float normalized)
        {
            normalized = Mathf.Clamp01(normalized);



            if (progressSlider != null)
                progressSlider.value = normalized;



            if (progressPercentText != null)
                progressPercentText.text = $"{Mathf.RoundToInt(normalized * 100f)}%";

        }



        void SetLoadingStatus(string stageLabel, string stageDetail = null)

        {

            if (loadingStatusText == null)

                return;



            loadingStatusText.text = string.IsNullOrEmpty(stageDetail)

                ? stageLabel

                : $"{stageLabel} \"{stageDetail}\"";

        }



        private void OnDestroy()
        {
            if (_loadRoutine != null)
                StopCoroutine(_loadRoutine);
        }
    }
}



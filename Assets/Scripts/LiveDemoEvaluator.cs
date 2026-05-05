using System.Collections.Generic;
using System.IO;
using System.Collections;
using System.Reflection;
using System.Text;
using Unity.InferenceEngine;
using Unity.MLAgents;
using Unity.MLAgents.Policies;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class LiveDemoEvaluator : MonoBehaviour
{
    private sealed class ScenarioMetrics
    {
        public string Title;
        public string TrackId;
        public ModelAsset Model;
        public int EpisodesCompleted;
        public int Completions;
        public float TotalReward;
        public float TotalAverageSpeed;
        public float BestTime = float.PositiveInfinity;
        public float TotalCompletionTime;

        public float CompletionRate => EpisodesCompleted > 0 ? (float)Completions / EpisodesCompleted : 0f;
        public float AverageReward => EpisodesCompleted > 0 ? TotalReward / EpisodesCompleted : 0f;
        public float AverageSpeed => EpisodesCompleted > 0 ? TotalAverageSpeed / EpisodesCompleted : 0f;
        public float AverageCompletionTime => Completions > 0 ? TotalCompletionTime / Completions : float.NaN;

        public void Add(CarDrivingAgent.EpisodeResult result)
        {
            EpisodesCompleted++;
            TotalReward += result.totalReward;
            TotalAverageSpeed += result.averageSpeed;
            if (!result.completed)
            {
                return;
            }

            Completions++;
            TotalCompletionTime += result.elapsedSeconds;
            BestTime = Mathf.Min(BestTime, result.elapsedSeconds);
        }
    }

    [Header("References")]
    [SerializeField] private DrivingTrainingManager trainingManager;
    [SerializeField] private CarDrivingAgent carDrivingAgent;
    [SerializeField] private BehaviorParameters behaviorParameters;
    [SerializeField] private Camera evaluationCamera;
    [SerializeField] private Camera overheadCamera;
    [SerializeField] private ProceduralTrackGenerator trackGenerator;

    [Header("Inference Models")]
    [SerializeField] private ModelAsset standardModelTrackABefore;
    [SerializeField] private ModelAsset standardModelTrackBAfter;
    [SerializeField] private ModelAsset replayModelTrackBAfter;

    [Header("Evaluation Setup")]
    [SerializeField] private int episodesPerScenario = 100;
    [SerializeField] private string trackAId = "TrackA";
    [SerializeField] private string trackBId = "TrackB";
    [SerializeField] private string resultsFileName = "live_demo_results.txt";
    [SerializeField] private bool autoStartOnPlay = true;
    [SerializeField] private bool hideTrainingStatsOverlay = true;
    [SerializeField] private bool hideCheckpointGizmos = true;
    [SerializeField] private bool hideParticleSystemGizmoIcons = true;
    [SerializeField] private bool hideCanvasGizmoOutline = true;
    [SerializeField] private bool hideGameViewGizmosInEditor = true;

    [Header("UI")]
    [SerializeField] private float panelWidthFraction = 0.3333f;
    [SerializeField] private Vector2 panelPadding = new Vector2(14f, 14f);

    [Header("Overhead Camera")]
    [SerializeField] private float overheadPadding = 2f;
    [SerializeField] private float overheadHeight = 80f;
    [SerializeField] private float overheadNearClipPlane = 0.1f;
    [SerializeField] private float overheadFarClipPlane = 250f;

    [Header("Simulation Speed")]
    [SerializeField] private float simulationSpeed = 1f;
    [SerializeField] private float minSimulationSpeed = 1f;
    [SerializeField] private float maxSimulationSpeed = 100f;

    [Header("Build Performance Mode")]
    [SerializeField] private bool useFastBuildMode = false;
    [SerializeField] private int buildWidth = 1280;
    [SerializeField] private int buildHeight = 720;
    [SerializeField] private FullScreenMode fullscreenMode = FullScreenMode.FullScreenWindow;
    [SerializeField] private int qualityLevel = 0;
    [SerializeField] private bool disableVSync = true;
    [SerializeField] private int targetFrameRate = -1;

    private readonly List<ScenarioMetrics> scenarios = new List<ScenarioMetrics>(6);
    private int currentScenarioIndex;
    private bool evaluationStarted;
    private bool evaluationCompleted;
    private bool showFullscreenResults;
    private float baseFixedDeltaTime;
    private Vector2 statsScrollPosition;
    private Vector2 fullscreenTableScrollPosition;
    private Texture2D solidTexture;
    private GUIStyle headerStyle;
    private GUIStyle textStyle;
    private GUIStyle boxStyle;

    private void Awake()
    {
        if (trainingManager == null)
        {
            trainingManager = FindFirstObjectByType<DrivingTrainingManager>();
        }

        if (carDrivingAgent == null)
        {
            carDrivingAgent = FindFirstObjectByType<CarDrivingAgent>();
        }

        if (behaviorParameters == null && carDrivingAgent != null)
        {
            behaviorParameters = carDrivingAgent.GetComponent<BehaviorParameters>();
        }

        if (trackGenerator == null && trainingManager != null)
        {
            trackGenerator = trainingManager.TrackGenerator;
        }

        if (evaluationCamera == null)
        {
            evaluationCamera = Camera.main;
        }

        baseFixedDeltaTime = Time.fixedDeltaTime;
    }

    private void OnEnable()
    {
        if (trainingManager != null)
        {
            trainingManager.EpisodeResultRecorded += HandleEpisodeResult;
        }
    }

    private void OnDisable()
    {
        if (trainingManager != null)
        {
            trainingManager.EpisodeResultRecorded -= HandleEpisodeResult;
        }
    }

    private void Start()
    {
        ApplyBuildPerformanceMode();
        ApplyBuildFullscreen();

        if (trainingManager != null && hideTrainingStatsOverlay)
        {
            trainingManager.SetStatsOverlayVisible(false);
            trainingManager.SetTrackGenerationFallbackAllowed(false);
        }

        ApplyEvaluationVisualSettings();
        if (autoStartOnPlay)
        {
            StartCoroutine(BeginEvaluationDeferred());
        }

        ApplyCameraLayout();
        ApplySimulationSpeed();
    }

    private IEnumerator BeginEvaluationDeferred()
    {
        yield return null;
        yield return new WaitForEndOfFrame();
        BeginEvaluation();
    }

    private void Update()
    {
        ApplyCameraLayout();
        UpdateOverheadCameraFraming();

#if UNITY_EDITOR
        if (hideCanvasGizmoOutline)
        {
            AnnotationUtilityBridge.ClearCanvasSelectionOutline();
        }

        if (hideGameViewGizmosInEditor)
        {
            AnnotationUtilityBridge.DisableGameViewGizmos();
        }
#endif
    }

    [ContextMenu("Begin Live Demo Evaluation")]
    public void BeginEvaluation()
    {
        if (trainingManager == null || carDrivingAgent == null || behaviorParameters == null)
        {
            Debug.LogError("LiveDemoEvaluator is missing required references.", this);
            return;
        }

        if (standardModelTrackABefore == null || standardModelTrackBAfter == null || replayModelTrackBAfter == null)
        {
            Debug.LogError("Assign the standard before model, standard after model, and replay after model on LiveDemoEvaluator before starting the evaluation.", this);
            return;
        }

        scenarios.Clear();
        scenarios.Add(new ScenarioMetrics
        {
            Title = "Standard Model on Track A (Before)",
            TrackId = trackAId,
            Model = standardModelTrackABefore
        });
        scenarios.Add(new ScenarioMetrics
        {
            Title = "Standard Model on Track B (After)",
            TrackId = trackBId,
            Model = standardModelTrackBAfter
        });
        scenarios.Add(new ScenarioMetrics
        {
            Title = "Standard Model on Track A (After)",
            TrackId = trackAId,
            Model = standardModelTrackBAfter
        });
        scenarios.Add(new ScenarioMetrics
        {
            Title = "Replay Model on Track A (Before)",
            TrackId = trackAId,
            Model = standardModelTrackABefore
        });
        scenarios.Add(new ScenarioMetrics
        {
            Title = "Replay Model on Track B (After)",
            TrackId = trackBId,
            Model = replayModelTrackBAfter
        });
        scenarios.Add(new ScenarioMetrics
        {
            Title = "Replay Model on Track A (After)",
            TrackId = trackAId,
            Model = replayModelTrackBAfter
        });

        currentScenarioIndex = 0;
        evaluationStarted = true;
        evaluationCompleted = false;

        ApplyScenario(scenarios[currentScenarioIndex]);
        carDrivingAgent.EndEpisode();
    }

    private void HandleEpisodeResult(CarDrivingAgent.EpisodeResult result)
    {
        if (!evaluationStarted || evaluationCompleted || scenarios.Count == 0)
        {
            return;
        }

        ScenarioMetrics currentScenario = scenarios[currentScenarioIndex];
        currentScenario.Add(result);

        if (currentScenario.EpisodesCompleted < Mathf.Max(1, episodesPerScenario))
        {
            return;
        }

        currentScenarioIndex++;
        if (currentScenarioIndex >= scenarios.Count)
        {
            evaluationCompleted = true;
            WriteResultsReport();
            if (behaviorParameters != null)
            {
                behaviorParameters.BehaviorType = BehaviorType.InferenceOnly;
            }

            StopSimulationActivity();
            return;
        }

        ApplyScenario(scenarios[currentScenarioIndex]);
    }

    private void ApplyScenario(ScenarioMetrics scenario)
    {
        behaviorParameters.Model = scenario.Model;
        carDrivingAgent.SetModel(behaviorParameters.BehaviorName, scenario.Model, behaviorParameters.InferenceDevice);
        behaviorParameters.BehaviorType = BehaviorType.InferenceOnly;
        trainingManager.ConfigureStandardTrack(scenario.TrackId);
        if (trainingManager.CarAdapter != null)
        {
            trainingManager.CarAdapter.ClearAction();
        }

        Debug.Log(
            $"LiveDemoEvaluator applied scenario '{scenario.Title}' with model '{(scenario.Model != null ? scenario.Model.name : "missing")}' on track '{scenario.TrackId}'.",
            this);
        UpdateOverheadCameraFraming();
    }

    private void RestartEvaluation()
    {
        if (carDrivingAgent != null)
        {
            carDrivingAgent.enabled = true;
        }

        if (behaviorParameters != null)
        {
            behaviorParameters.BehaviorType = BehaviorType.InferenceOnly;
        }

        Academy.Instance.AutomaticSteppingEnabled = true;
        BeginEvaluation();
    }

    private void OnGUI()
    {
        if (!Application.isPlaying)
        {
            return;
        }

        EnsureGuiAssets();

        float panelWidth = Mathf.Clamp(Screen.width * panelWidthFraction, 360f, Screen.width * 0.45f);
        Rect panelRect = new Rect(Screen.width - panelWidth, 0f, panelWidth, Screen.height);
        GUI.Box(panelRect, GUIContent.none, boxStyle);

        GUILayout.BeginArea(new Rect(panelRect.x + panelPadding.x, panelRect.y + panelPadding.y, panelRect.width - panelPadding.x * 2f, panelRect.height - panelPadding.y * 2f));
        GUILayout.Label("Live Demo Evaluation", headerStyle);
        DrawSimulationSpeedControls();
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Show Fullscreen Table"))
        {
            showFullscreenResults = true;
        }
        if (GUILayout.Button("Restart Simulation"))
        {
            RestartEvaluation();
        }
        GUILayout.EndHorizontal();
        GUILayout.Label(BuildStatusSummaryText(), textStyle);
        GUILayout.Space(8f);
        DrawScenarioStatusBlocks();
        GUILayout.EndArea();

        if (showFullscreenResults)
        {
            DrawFullscreenResultsOverlay();
        }
    }

    private void DrawSimulationSpeedControls()
    {
        GUILayout.Space(6f);
        GUILayout.Label($"Simulation Speed: {simulationSpeed:F0}x", textStyle);
        float updatedSpeed = GUILayout.HorizontalSlider(simulationSpeed, minSimulationSpeed, maxSimulationSpeed);
        updatedSpeed = QuantizeSimulationSpeed(updatedSpeed);
        if (!Mathf.Approximately(updatedSpeed, simulationSpeed))
        {
            simulationSpeed = updatedSpeed;
            ApplySimulationSpeed();
        }

        GUILayout.Space(8f);
    }

    private string BuildStatusSummaryText()
    {
        var builder = new StringBuilder(256);
        builder.AppendLine($"Episodes Per Scenario: {episodesPerScenario}");

        if (!evaluationStarted)
        {
            builder.AppendLine("Status: Waiting to start.");
            return builder.ToString();
        }

        if (!evaluationCompleted && currentScenarioIndex < scenarios.Count)
        {
            ScenarioMetrics currentScenario = scenarios[currentScenarioIndex];
            builder.AppendLine($"Current Phase: {currentScenario.Title}");
            builder.AppendLine($"Progress: {currentScenario.EpisodesCompleted}/{episodesPerScenario} episodes");
        }
        else
        {
            builder.AppendLine("Status: Evaluation complete.");
        }

        return builder.ToString();
    }

    private string BuildDetailedTableText()
    {
        var builder = new StringBuilder(2048);
        builder.AppendLine("Track A Forgetting Summary");
        if (scenarios.Count >= 3)
        {
            ScenarioMetrics before = scenarios[0];
            ScenarioMetrics standardAfter = scenarios[2];
            builder.AppendLine($"Standard before Track B completion: {before.CompletionRate:P1}");
            builder.AppendLine($"Standard after Track B completion: {standardAfter.CompletionRate:P1}");
            builder.AppendLine($"Standard before Track B avg success time: {(float.IsNaN(before.AverageCompletionTime) ? "n/a" : $"{before.AverageCompletionTime:F2}s")}");
            builder.AppendLine($"Standard after Track B avg success time: {(float.IsNaN(standardAfter.AverageCompletionTime) ? "n/a" : $"{standardAfter.AverageCompletionTime:F2}s")}");
        }

        if (scenarios.Count >= 5)
        {
            ScenarioMetrics replayBefore = scenarios[3];
            ScenarioMetrics replayAfter = scenarios[5];
            builder.AppendLine();
            builder.AppendLine($"Replay before Track B completion: {replayBefore.CompletionRate:P1}");
            builder.AppendLine($"Replay after Track B completion: {replayAfter.CompletionRate:P1}");
            builder.AppendLine($"Replay before Track B avg success time: {(float.IsNaN(replayBefore.AverageCompletionTime) ? "n/a" : $"{replayBefore.AverageCompletionTime:F2}s")}");
            builder.AppendLine($"Replay after Track B avg success time: {(float.IsNaN(replayAfter.AverageCompletionTime) ? "n/a" : $"{replayAfter.AverageCompletionTime:F2}s")}");
            builder.AppendLine($"Replay completion change: {(replayAfter.CompletionRate - replayBefore.CompletionRate):P1}");
        }

        return builder.ToString();
    }

    private void WriteResultsReport()
    {
        string resultsPath = GetResultsOutputPath();
        string directory = Path.GetDirectoryName(resultsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(resultsPath, BuildResultsReportText());
        Debug.Log($"Live demo results written to '{resultsPath}'.", this);
    }

    private string BuildResultsReportText()
    {
        var builder = new StringBuilder(4096);
        builder.AppendLine("Continuous Learning Track Driving - Live Demo Results");
        builder.AppendLine($"Generated: {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Episodes Per Scenario: {episodesPerScenario}");
        builder.AppendLine();
        builder.AppendLine("Scenario Results");

        foreach (ScenarioMetrics scenario in scenarios)
        {
            string avgTimeText = float.IsNaN(scenario.AverageCompletionTime) ? "n/a" : $"{scenario.AverageCompletionTime:F2}s";
            string bestTimeText = float.IsPositiveInfinity(scenario.BestTime) ? "n/a" : $"{scenario.BestTime:F2}s";

            builder.AppendLine(scenario.Title);
            builder.AppendLine($"Track: {scenario.TrackId}");
            builder.AppendLine($"Episodes Completed: {scenario.EpisodesCompleted}/{episodesPerScenario}");
            builder.AppendLine($"Completion Rate: {scenario.CompletionRate:P1}");
            builder.AppendLine($"Average Successful Time: {avgTimeText}");
            builder.AppendLine($"Best Time: {bestTimeText}");
            builder.AppendLine($"Average Reward: {scenario.AverageReward:F2}");
            builder.AppendLine($"Average Speed: {scenario.AverageSpeed:F2}");
            builder.AppendLine();
        }

        builder.AppendLine(BuildDetailedTableText().TrimEnd());
        return builder.ToString();
    }

    private string GetResultsOutputPath()
    {
#if UNITY_EDITOR
        return Path.Combine(Application.dataPath, "TrainingData", "Results", resultsFileName);
#else
        string appRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        return Path.Combine(appRoot, resultsFileName);
#endif
    }

    private void DrawScenarioStatusBlocks()
    {
        if (scenarios.Count == 0)
        {
            return;
        }

        GUILayout.Label("Scenario Results", textStyle);
        GUILayout.Space(4f);
        GUILayout.BeginHorizontal();
        DrawScenarioColumn("Standard", 0, 3);
        GUILayout.Space(10f);
        DrawScenarioColumn("Replay", 3, 3);
        GUILayout.EndHorizontal();
    }

    private void DrawScenarioColumn(string columnTitle, int startIndex, int count)
    {
        GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
        GUILayout.Label(columnTitle, textStyle);
        GUILayout.Space(2f);

        int endIndex = Mathf.Min(scenarios.Count, startIndex + count);
        for (int i = startIndex; i < endIndex; i++)
        {
            DrawScenarioBlock(i);
        }

        GUILayout.EndVertical();
    }

    private void DrawScenarioBlock(int scenarioIndex)
    {
        ScenarioMetrics scenario = scenarios[scenarioIndex];
        float progress = Mathf.Clamp01((float)scenario.EpisodesCompleted / Mathf.Max(1, episodesPerScenario));
        string avgTimeText = float.IsNaN(scenario.AverageCompletionTime) ? "n/a" : $"{scenario.AverageCompletionTime:F2}s";
        string bestTimeText = float.IsPositiveInfinity(scenario.BestTime) ? "n/a" : $"{scenario.BestTime:F2}s";

        GUILayout.Label(scenario.Title, textStyle);
        Rect barRect = GUILayoutUtility.GetRect(10f, 22f, GUILayout.ExpandWidth(true));
        DrawProgressBar(barRect, progress, GetScenarioColor(scenarioIndex));
        GUILayout.Label(
            $"Runs: {scenario.EpisodesCompleted}/{episodesPerScenario}\n" +
            $"Completion: {scenario.Completions}/{scenario.EpisodesCompleted} ({scenario.CompletionRate:P1})\n" +
            $"Avg Successful Time: {avgTimeText}\n" +
            $"Best Time: {bestTimeText}\n" +
            $"Avg Reward: {scenario.AverageReward:F2}\n" +
            $"Avg Speed: {scenario.AverageSpeed:F2}",
            textStyle);
        GUILayout.Space(8f);
    }

    private void DrawComparisonCharts(float availableWidth)
    {
        Rect completionRect = GUILayoutUtility.GetRect(availableWidth, 150f);
        DrawBarChart(completionRect, "Completion Rate", true);

        GUILayout.Space(10f);

        Rect timeRect = GUILayoutUtility.GetRect(availableWidth, 150f);
        DrawBarChart(timeRect, "Average Successful Time (lower is better)", false);
    }

    private void DrawBarChart(Rect rect, string title, bool useCompletionRate)
    {
        GUI.Box(rect, GUIContent.none, boxStyle);
        GUI.Label(new Rect(rect.x + 10f, rect.y + 8f, rect.width - 20f, 24f), title, textStyle);

        if (scenarios.Count == 0)
        {
            return;
        }

        float chartTop = rect.y + 34f;
        float chartHeight = rect.height - 56f;
        float chartWidth = rect.width - 20f;
        float gap = 12f;
        float barWidth = (chartWidth - gap * (scenarios.Count - 1)) / Mathf.Max(1, scenarios.Count);

        float maxValue = 1f;
        if (!useCompletionRate)
        {
            maxValue = 0f;
            foreach (ScenarioMetrics scenario in scenarios)
            {
                if (!float.IsNaN(scenario.AverageCompletionTime))
                {
                    maxValue = Mathf.Max(maxValue, scenario.AverageCompletionTime);
                }
            }

            if (maxValue <= 0.0001f)
            {
                maxValue = 1f;
            }
        }

        for (int i = 0; i < scenarios.Count; i++)
        {
            ScenarioMetrics scenario = scenarios[i];
            float value = useCompletionRate ? scenario.CompletionRate : (float.IsNaN(scenario.AverageCompletionTime) ? 0f : scenario.AverageCompletionTime);
            float normalized = useCompletionRate ? value : value / maxValue;
            float barHeight = chartHeight * Mathf.Clamp01(normalized);
            float x = rect.x + 10f + i * (barWidth + gap);
            float y = chartTop + (chartHeight - barHeight);

            Color barColor = GetScenarioColor(i);
            DrawSolidRect(new Rect(x, y, barWidth, barHeight), barColor);

            string valueText = useCompletionRate ? $"{value:P0}" : (float.IsNaN(scenario.AverageCompletionTime) ? "n/a" : $"{scenario.AverageCompletionTime:F1}s");
            GUI.Label(new Rect(x, chartTop - 2f, barWidth, 20f), valueText, textStyle);
            GUI.Label(new Rect(x, rect.yMax - 26f, barWidth, 24f), GetChartLabel(i), textStyle);
        }
    }

    private string GetChartLabel(int scenarioIndex)
    {
        return scenarioIndex switch
        {
            0 => "Track A Before",
            1 => "Std Track B",
            2 => "Std Track A After",
            3 => "Replay Track A Before",
            4 => "Replay Track B",
            5 => "Replay Track A After",
            _ => $"Run {scenarioIndex + 1}"
        };
    }

    private void EnsureGuiAssets()
    {
        if (solidTexture == null)
        {
            solidTexture = new Texture2D(1, 1);
            solidTexture.SetPixel(0, 0, Color.white);
            solidTexture.Apply();
        }

        if (headerStyle == null)
        {
            headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft
            };
            headerStyle.normal.textColor = Color.white;
        }

        if (textStyle == null)
        {
            textStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                wordWrap = true,
                alignment = TextAnchor.UpperLeft
            };
            textStyle.normal.textColor = Color.white;
        }

        if (boxStyle == null)
        {
            Texture2D backgroundTexture = new Texture2D(1, 1);
            backgroundTexture.SetPixel(0, 0, new Color(0.05f, 0.05f, 0.08f, 0.9f));
            backgroundTexture.Apply();

            boxStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = backgroundTexture }
            };
        }
    }

    private void DrawSolidRect(Rect rect, Color color)
    {
        Color previousColor = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, solidTexture);
        GUI.color = previousColor;
    }

    private void DrawFullscreenResultsOverlay()
    {
        Rect overlayRect = new Rect(0f, 0f, Screen.width, Screen.height);
        DrawSolidRect(overlayRect, new Color(0f, 0f, 0f, 0.88f));

        Rect panelRect = new Rect(40f, 40f, Screen.width - 80f, Screen.height - 80f);
        GUI.Box(panelRect, GUIContent.none, boxStyle);

        GUILayout.BeginArea(new Rect(panelRect.x + 16f, panelRect.y + 16f, panelRect.width - 32f, panelRect.height - 32f));
        GUILayout.BeginHorizontal();
        GUILayout.Label("Detailed Evaluation Table", headerStyle);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Close", GUILayout.Width(100f), GUILayout.Height(30f)))
        {
            showFullscreenResults = false;
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(12f);
        fullscreenTableScrollPosition = GUILayout.BeginScrollView(fullscreenTableScrollPosition, false, true);
        DrawComparisonCharts(panelRect.width - 64f);
        GUILayout.Space(16f);
        DrawDetailedResultsTable(panelRect.width - 64f);
        GUILayout.Space(16f);
        GUILayout.TextArea(BuildDetailedTableText(), textStyle, GUILayout.ExpandHeight(true));
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private void DrawProgressBar(Rect rect, float progress, Color fillColor)
    {
        DrawSolidRect(rect, new Color(0.16f, 0.16f, 0.2f, 1f));
        float fillWidth = Mathf.Clamp01(progress) * rect.width;
        if (fillWidth > 0f)
        {
            DrawSolidRect(new Rect(rect.x, rect.y, fillWidth, rect.height), fillColor);
        }

        GUI.Box(rect, GUIContent.none, GUI.skin.box);
        GUI.Label(rect, $"{progress:P0}", textStyle);
    }

    private Color GetScenarioColor(int scenarioIndex)
    {
        return scenarioIndex switch
        {
            0 => new Color(0.28f, 0.76f, 0.47f),
            1 => new Color(0.29f, 0.63f, 0.89f),
            2 => new Color(0.85f, 0.38f, 0.32f),
            3 => new Color(0.52f, 0.78f, 0.63f),
            4 => new Color(0.65f, 0.49f, 0.89f),
            5 => new Color(0.92f, 0.66f, 0.27f),
            _ => new Color(0.75f, 0.75f, 0.75f)
        };
    }

    private void DrawDetailedResultsTable(float availableWidth)
    {
        GUILayout.Label("Detailed Results Table", textStyle);
        GUILayout.Space(6f);

        float[] columnWidths =
        {
            availableWidth * 0.28f,
            availableWidth * 0.12f,
            availableWidth * 0.12f,
            availableWidth * 0.16f,
            availableWidth * 0.12f,
            availableWidth * 0.10f,
            availableWidth * 0.10f
        };

        DrawTableRow(columnWidths, true,
            "Scenario",
            "Episodes",
            "Completion",
            "Avg Time",
            "Best Time",
            "Avg Reward",
            "Avg Speed");

        for (int i = 0; i < scenarios.Count; i++)
        {
            ScenarioMetrics scenario = scenarios[i];
            string avgTimeText = float.IsNaN(scenario.AverageCompletionTime) ? "n/a" : $"{scenario.AverageCompletionTime:F2}s";
            string bestTimeText = float.IsPositiveInfinity(scenario.BestTime) ? "n/a" : $"{scenario.BestTime:F2}s";

            DrawTableRow(columnWidths, false,
                scenario.Title,
                $"{scenario.EpisodesCompleted}/{episodesPerScenario}",
                $"{scenario.CompletionRate:P1}",
                avgTimeText,
                bestTimeText,
                $"{scenario.AverageReward:F2}",
                $"{scenario.AverageSpeed:F2}");
        }
    }

    private void DrawTableRow(float[] columnWidths, bool isHeader, params string[] cells)
    {
        Rect rowRect = GUILayoutUtility.GetRect(columnWidths[0], 28f, GUILayout.ExpandWidth(true));
        DrawSolidRect(rowRect, isHeader ? new Color(0.15f, 0.15f, 0.2f, 1f) : new Color(0.09f, 0.09f, 0.12f, 1f));

        float currentX = rowRect.x + 6f;
        for (int i = 0; i < columnWidths.Length && i < cells.Length; i++)
        {
            Rect cellRect = new Rect(currentX, rowRect.y + 4f, columnWidths[i] - 8f, rowRect.height - 8f);
            GUI.Label(cellRect, cells[i], textStyle);
            currentX += columnWidths[i];
        }
    }

    private void ApplyCameraLayout()
    {
        float rightPanelFraction = Mathf.Clamp01(panelWidthFraction);
        float leftWidthFraction = Mathf.Clamp01(1f - rightPanelFraction);

        if (evaluationCamera != null)
        {
            evaluationCamera.rect = new Rect(0f, 1f / 3f, leftWidthFraction, 2f / 3f);
        }

        if (overheadCamera != null)
        {
            overheadCamera.rect = new Rect(0f, 0f, leftWidthFraction, 1f / 3f);
        }
    }

    private void UpdateOverheadCameraFraming()
    {
        if (overheadCamera == null || trackGenerator == null)
        {
            return;
        }

        IReadOnlyList<Vector3> pathPoints = trackGenerator.PathPoints;
        if (pathPoints == null || pathPoints.Count < 2)
        {
            return;
        }

        Vector3 startPoint = trackGenerator.GetCarSpawnPosition();
        Vector3 finishPoint = trackGenerator.GetFinishPoint();
        Vector3 horizontalDirection = finishPoint - startPoint;
        horizontalDirection.y = 0f;
        if (horizontalDirection.sqrMagnitude <= 0.0001f)
        {
            horizontalDirection = trackGenerator.GetCarSpawnRotation() * Vector3.forward;
            horizontalDirection.y = 0f;
        }

        if (horizontalDirection.sqrMagnitude <= 0.0001f)
        {
            horizontalDirection = Vector3.right;
        }

        horizontalDirection.Normalize();

        Vector3 cameraForward = Vector3.down;
        Vector3 cameraUp = Vector3.Cross(cameraForward, horizontalDirection).normalized;
        if (cameraUp.sqrMagnitude <= 0.0001f)
        {
            cameraUp = Vector3.forward;
        }

        Quaternion cameraRotation = Quaternion.LookRotation(cameraForward, cameraUp);
        Vector3 cameraRight = horizontalDirection;
        Vector3 cameraVerticalAxis = cameraUp;

        float minX = float.PositiveInfinity;
        float maxX = float.NegativeInfinity;
        float minY = float.PositiveInfinity;
        float maxY = float.NegativeInfinity;

        foreach (Vector3 pathPoint in pathPoints)
        {
            Vector3 flattenedPoint = new Vector3(pathPoint.x, 0f, pathPoint.z);
            float x = Vector3.Dot(flattenedPoint, cameraRight);
            float y = Vector3.Dot(flattenedPoint, cameraVerticalAxis);
            minX = Mathf.Min(minX, x);
            maxX = Mathf.Max(maxX, x);
            minY = Mathf.Min(minY, y);
            maxY = Mathf.Max(maxY, y);
        }

        Vector3 flattenedStart = new Vector3(startPoint.x, 0f, startPoint.z);
        Vector3 flattenedFinish = new Vector3(finishPoint.x, 0f, finishPoint.z);
        float startY = Vector3.Dot(flattenedStart, cameraVerticalAxis);
        float finishY = Vector3.Dot(flattenedFinish, cameraVerticalAxis);

        float centerX = (minX + maxX) * 0.5f;
        float centerY = (startY + finishY) * 0.5f;
        Vector3 groundCenter = (cameraRight * centerX) + (cameraVerticalAxis * centerY);

        float halfWidth = Mathf.Max(centerX - minX, maxX - centerX);
        float halfHeight = Mathf.Max(centerY - minY, maxY - centerY);
        float overheadPixelWidth = Mathf.Max(1f, Screen.width * Mathf.Clamp01(1f - panelWidthFraction));
        float overheadPixelHeight = Mathf.Max(1f, Screen.height / 3f);
        float overheadAspect = overheadPixelWidth / overheadPixelHeight;
        float requiredHalfHeight = Mathf.Max(halfHeight, halfWidth / overheadAspect) + Mathf.Max(0f, overheadPadding);

        overheadCamera.orthographic = true;
        overheadCamera.nearClipPlane = overheadNearClipPlane;
        overheadCamera.farClipPlane = Mathf.Max(overheadFarClipPlane, overheadHeight + 50f);
        overheadCamera.orthographicSize = Mathf.Max(1f, requiredHalfHeight);
        overheadCamera.transform.SetPositionAndRotation(
            groundCenter + Vector3.up * overheadHeight,
            cameraRotation);
    }

    private void ApplySimulationSpeed()
    {
        float clampedSpeed = Mathf.Clamp(QuantizeSimulationSpeed(simulationSpeed), minSimulationSpeed, maxSimulationSpeed);
        simulationSpeed = clampedSpeed;
        Time.timeScale = clampedSpeed;
        // Keep the physics timestep constant in simulation time so the car
        // doesn't become numerically unstable as playback speed increases.
        Time.fixedDeltaTime = baseFixedDeltaTime;
    }

    private float QuantizeSimulationSpeed(float rawSpeed)
    {
        float clampedSpeed = Mathf.Clamp(rawSpeed, minSimulationSpeed, maxSimulationSpeed);
        if (clampedSpeed < 5.5f)
        {
            return 1f;
        }

        return Mathf.Clamp(Mathf.Round(clampedSpeed / 10f) * 10f, 10f, maxSimulationSpeed);
    }

    private void ApplyBuildPerformanceMode()
    {
        if (!useFastBuildMode)
        {
            return;
        }

        qualityLevel = Mathf.Clamp(qualityLevel, 0, Mathf.Max(0, QualitySettings.names.Length - 1));
        QualitySettings.SetQualityLevel(qualityLevel, true);

        if (disableVSync)
        {
            QualitySettings.vSyncCount = 0;
        }

        Application.targetFrameRate = targetFrameRate;
        Screen.SetResolution(
            Mathf.Max(320, buildWidth),
            Mathf.Max(180, buildHeight),
            fullscreenMode);
    }

    private void ApplyBuildFullscreen()
    {
#if !UNITY_EDITOR
        Screen.SetResolution(
            1920,
            1200,
            FullScreenMode.FullScreenWindow);
#endif
    }

    private void StopSimulationActivity()
    {
        if (carDrivingAgent != null)
        {
            carDrivingAgent.enabled = false;
        }

        if (trainingManager != null && trainingManager.CarAdapter != null)
        {
            trainingManager.CarAdapter.ClearAction();
        }

        Academy.Instance.AutomaticSteppingEnabled = false;
    }

    private void ApplyEvaluationVisualSettings()
    {
        if (hideCheckpointGizmos && trackGenerator != null)
        {
            SetPrivateField(trackGenerator, "showCheckpointGizmos", false);
        }

#if UNITY_EDITOR
        if (hideParticleSystemGizmoIcons)
        {
            AnnotationUtilityBridge.DisableParticleSystemIcons();
        }

        if (hideCanvasGizmoOutline)
        {
            AnnotationUtilityBridge.DisableCanvasSelectionOutline();
        }

        if (hideGameViewGizmosInEditor)
        {
            AnnotationUtilityBridge.DisableGameViewGizmos();
        }
#endif
    }

    private static void SetPrivateField<TTarget, TValue>(TTarget target, string fieldName, TValue value)
        where TTarget : class
    {
        if (target == null)
        {
            return;
        }

        FieldInfo field = typeof(TTarget).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null)
        {
            return;
        }

        field.SetValue(target, value);
    }

    private void OnDestroy()
    {
        Academy.Instance.AutomaticSteppingEnabled = true;
        Time.timeScale = 1f;
        Time.fixedDeltaTime = baseFixedDeltaTime;

#if UNITY_EDITOR
        if (hideGameViewGizmosInEditor)
        {
            AnnotationUtilityBridge.RestoreGameViewGizmos();
        }
#endif
    }

#if UNITY_EDITOR
    private static class AnnotationUtilityBridge
    {
        private static bool gameViewGizmoStateCaptured;
        private static bool previousGameViewGizmoState;

        public static void DisableParticleSystemIcons()
        {
            Assembly editorAssembly = typeof(Editor).Assembly;
            System.Type annotationUtilityType = editorAssembly.GetType("UnityEditor.AnnotationUtility");
            System.Type annotationType = editorAssembly.GetType("UnityEditor.Annotation");
            if (annotationUtilityType == null || annotationType == null)
            {
                return;
            }

            MethodInfo getAnnotations = annotationUtilityType.GetMethod("GetAnnotations", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo setIconEnabled = annotationUtilityType.GetMethod("SetIconEnabled", BindingFlags.Static | BindingFlags.NonPublic);
            if (getAnnotations == null || setIconEnabled == null)
            {
                return;
            }

            object annotations = getAnnotations.Invoke(null, null);
            if (annotations is not System.Array annotationArray)
            {
                return;
            }

            foreach (object annotation in annotationArray)
            {
                if (annotation == null)
                {
                    continue;
                }

                FieldInfo classIdField = annotationType.GetField("classID");
                FieldInfo scriptClassField = annotationType.GetField("scriptClass");
                if (classIdField == null || scriptClassField == null)
                {
                    continue;
                }

                int classId = (int)classIdField.GetValue(annotation);
                string scriptClass = scriptClassField.GetValue(annotation) as string;
                if (classId != 198 || !string.IsNullOrEmpty(scriptClass))
                {
                    continue;
                }

                setIconEnabled.Invoke(null, new object[] { classId, scriptClass, 0 });
            }
        }

        public static void DisableCanvasSelectionOutline()
        {
            ClearCanvasSelectionOutline();
        }

        public static void ClearCanvasSelectionOutline()
        {
            if (Selection.activeGameObject == null)
            {
                return;
            }

            if (Selection.activeGameObject.GetComponentInParent<Canvas>() == null)
            {
                return;
            }

            Selection.activeGameObject = null;
        }

        public static void DisableGameViewGizmos()
        {
            SetGameViewGizmosEnabled(false, capturePreviousState: true);
        }

        public static void RestoreGameViewGizmos()
        {
            if (!gameViewGizmoStateCaptured)
            {
                return;
            }

            SetGameViewGizmosEnabled(previousGameViewGizmoState, capturePreviousState: false);
            gameViewGizmoStateCaptured = false;
        }

        private static void SetGameViewGizmosEnabled(bool isEnabled, bool capturePreviousState)
        {
            Assembly editorAssembly = typeof(Editor).Assembly;
            System.Type gameViewType = editorAssembly.GetType("UnityEditor.GameView");
            if (gameViewType == null)
            {
                return;
            }

            MethodInfo getMainGameView = gameViewType.GetMethod("GetMainGameView", BindingFlags.Static | BindingFlags.NonPublic);
            EditorWindow gameViewWindow = getMainGameView?.Invoke(null, null) as EditorWindow;
            if (gameViewWindow == null)
            {
                return;
            }

            PropertyInfo showGizmosProperty = gameViewType.GetProperty("showGizmos", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (showGizmosProperty == null || !showGizmosProperty.CanRead || !showGizmosProperty.CanWrite)
            {
                return;
            }

            bool currentState = (bool)showGizmosProperty.GetValue(gameViewWindow);
            if (capturePreviousState && !gameViewGizmoStateCaptured)
            {
                previousGameViewGizmoState = currentState;
                gameViewGizmoStateCaptured = true;
            }

            if (currentState == isEnabled)
            {
                return;
            }

            showGizmosProperty.SetValue(gameViewWindow, isEnabled);
            gameViewWindow.Repaint();
        }
    }
#endif
}

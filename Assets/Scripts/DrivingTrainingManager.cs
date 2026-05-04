using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

public class DrivingTrainingManager : MonoBehaviour
{
    public enum TrainingMode
    {
        StandardTrackOnly,
        ContinualReplay,
        Evaluation
    }

    [Header("References")]
    [SerializeField] private ProceduralTrackGenerator trackGenerator;
    [SerializeField] private CarControlAdapter carAdapter;

    [Header("Track Library")]
    [SerializeField] private string trackFolder = "TrainingData/Tracks";
    [SerializeField] private string streamingTrackFolder = "StreamingAssets/TrainingData/Tracks";
    [SerializeField] private string resultsFolder = "TrainingData/Results";

    [Header("Experiment Setup")]
    [SerializeField] private TrainingMode trainingMode = TrainingMode.StandardTrackOnly;
    [SerializeField] private string currentTrainingTrackId = "TrackA";
    [SerializeField] private List<string> replayTrackIds = new List<string>();
    [SerializeField] [Range(0f, 1f)] private float replayTrackProbability = 0.5f;
    [SerializeField] private bool saveGeneratedTrackWhenMissing = true;
    [SerializeField] private bool allowTrackGenerationFallback = true;

    [Header("Evaluation")]
    [SerializeField] private List<string> evaluationTrackIds = new List<string> { "TrackA" };
    [SerializeField] private int evaluationEpisodesPerTrack = 5;
    [SerializeField] private string evaluationResultsFileName = "evaluation_results.txt";

    [Header("Training Stats")]
    [SerializeField] private bool showTrainingStatsOverlay = true;
    [SerializeField] [Range(5, 200)] private int rollingStatsWindow = 25;
    [SerializeField] private Vector2 statsOverlaySize = new Vector2(500f, 520f);

    private readonly List<string> evaluationSchedule = new List<string>();
    private readonly Queue<CarDrivingAgent.EpisodeResult> recentEpisodeResults = new Queue<CarDrivingAgent.EpisodeResult>();
    private int evaluationScheduleIndex;
    private int totalEpisodes;
    private int completedEpisodes;
    private int totalSteps;
    private float bestCompletionTime = float.PositiveInfinity;
    private float bestReward = float.NegativeInfinity;
    private float lastEpisodeTime;
    private float lastEpisodeReward;
    private float lastEpisodeAverageSpeed;
    private string lastTerminationReason = "n/a";
    private string lastTrackId = "n/a";
    private Vector2 statsScrollPosition;
    private GUIStyle overlayStyle;
    private GUIStyle overlayBackgroundStyle;

    public event Action<CarDrivingAgent.EpisodeResult> EpisodeResultRecorded;

    public TrainingMode Mode => trainingMode;
    public CarControlAdapter CarAdapter => carAdapter;
    public ProceduralTrackGenerator TrackGenerator => trackGenerator;
    public string ActiveTrackId { get; private set; }

    private void Awake()
    {
        if (trackGenerator == null)
        {
            trackGenerator = FindFirstObjectByType<ProceduralTrackGenerator>();
        }

        if (carAdapter == null)
        {
            carAdapter = FindFirstObjectByType<CarControlAdapter>();
        }
    }

    [ContextMenu("Generate And Save Track A")]
    private void GenerateAndSaveTrackAContextMenu()
    {
        GenerateAndSaveTrack("TrackA");
    }

    [ContextMenu("Generate And Save Track B")]
    private void GenerateAndSaveTrackBContextMenu()
    {
        GenerateAndSaveTrack("TrackB");
    }

    [ContextMenu("Load Track A")]
    private void LoadTrackAContextMenu()
    {
        LoadTrackById("TrackA");
    }

    [ContextMenu("Load Track B")]
    private void LoadTrackBContextMenu()
    {
        LoadTrackById("TrackB");
    }

    public void ResetEnvironment()
    {
        if (trackGenerator == null || carAdapter == null)
        {
            Debug.LogWarning("DrivingTrainingManager is missing required references.");
            return;
        }

        string requestedTrackId = ResolveNextTrackId();
        if (!LoadTrackById(requestedTrackId))
        {
            if (!allowTrackGenerationFallback)
            {
                Debug.LogError($"Required track '{requestedTrackId}' could not be loaded. Track generation fallback is disabled.", this);
                return;
            }

            if (saveGeneratedTrackWhenMissing)
            {
                trackGenerator.GenerateTrack();
                SaveCurrentTrack(requestedTrackId);
                ActiveTrackId = requestedTrackId;
            }
            else
            {
                Debug.LogWarning($"Track '{requestedTrackId}' was not found and auto-generation is disabled.");
                trackGenerator.GenerateTrack();
                ActiveTrackId = trackGenerator.CurrentTrackId;
            }
        }

        carAdapter.ResetVehiclePose(trackGenerator.GetCarSpawnPosition(), trackGenerator.GetCarSpawnRotation());
    }

    public void SaveCurrentTrack(string trackId = null)
    {
        if (trackGenerator == null)
        {
            return;
        }

        string resolvedTrackId = string.IsNullOrWhiteSpace(trackId) ? trackGenerator.CurrentTrackId : trackId.Trim();
        trackGenerator.SaveCurrentTrackToJson(resolvedTrackId, trackFolder);
        trackGenerator.SaveCurrentTrackToJson(resolvedTrackId, streamingTrackFolder);
        ActiveTrackId = resolvedTrackId;
    }

    public void GenerateAndSaveTrack(string trackId)
    {
        if (trackGenerator == null)
        {
            return;
        }

        trackGenerator.GenerateTrack();
        SaveCurrentTrack(trackId);
        ActiveTrackId = trackId;
    }

    public bool LoadTrackById(string trackId)
    {
        if (string.IsNullOrWhiteSpace(trackId))
        {
            return false;
        }

        string filePath = GetTrackFilePath(trackId.Trim());
        bool loaded = trackGenerator.TryLoadTrackFromJson(filePath);
        if (loaded)
        {
            ActiveTrackId = trackId.Trim();
        }

        return loaded;
    }

    public void RecordEpisodeResult(CarDrivingAgent.EpisodeResult result)
    {
        TrackEpisodeStats(result);
        EpisodeResultRecorded?.Invoke(result);

        if (trainingMode != TrainingMode.Evaluation)
        {
            return;
        }

        string resultsPath = GetResultsFilePath(evaluationResultsFileName);
        string directory = Path.GetDirectoryName(resultsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        bool writeHeader = !File.Exists(resultsPath);
        var builder = new StringBuilder();

        if (writeHeader)
        {
            builder.AppendLine("timestamp_utc,track_id,completed,termination_reason,total_reward,steps,elapsed_seconds,max_progress,average_speed");
        }

        builder.AppendLine(
            $"{System.DateTime.UtcNow:O}," +
            $"{result.trackId}," +
            $"{result.completed}," +
            $"{result.terminationReason}," +
            $"{result.totalReward:F6}," +
            $"{result.stepCount}," +
            $"{result.elapsedSeconds:F4}," +
            $"{result.maxProgress:F4}," +
            $"{result.averageSpeed:F4}");

        File.AppendAllText(resultsPath, builder.ToString());
    }

    public void ConfigureStandardTrack(string trackId)
    {
        trainingMode = TrainingMode.StandardTrackOnly;
        currentTrainingTrackId = string.IsNullOrWhiteSpace(trackId) ? currentTrainingTrackId : trackId.Trim();
        ActiveTrackId = currentTrainingTrackId;
    }

    public void SetStatsOverlayVisible(bool isVisible)
    {
        showTrainingStatsOverlay = isVisible;
    }

    public void SetTrackGenerationFallbackAllowed(bool isAllowed)
    {
        allowTrackGenerationFallback = isAllowed;
    }

    private void OnGUI()
    {
        if (!showTrainingStatsOverlay || !Application.isPlaying)
        {
            return;
        }

        EnsureOverlayStyles();
        Rect panelRect = new Rect(12f, 12f, statsOverlaySize.x, statsOverlaySize.y);
        GUI.Box(panelRect, GUIContent.none, overlayBackgroundStyle);

        Rect contentRect = new Rect(panelRect.x + 10f, panelRect.y + 10f, panelRect.width - 20f, panelRect.height - 20f);
        GUILayout.BeginArea(contentRect);
        statsScrollPosition = GUILayout.BeginScrollView(statsScrollPosition, false, true);
        GUILayout.Label(BuildStatsOverlayText(), overlayStyle, GUILayout.ExpandHeight(true));
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    private void TrackEpisodeStats(CarDrivingAgent.EpisodeResult result)
    {
        totalEpisodes++;
        totalSteps += result.stepCount;
        lastEpisodeTime = result.elapsedSeconds;
        lastEpisodeReward = result.totalReward;
        lastEpisodeAverageSpeed = result.averageSpeed;
        lastTerminationReason = string.IsNullOrWhiteSpace(result.terminationReason) ? "unknown" : result.terminationReason;
        lastTrackId = string.IsNullOrWhiteSpace(result.trackId) ? "unknown" : result.trackId;
        bestReward = Mathf.Max(bestReward, result.totalReward);

        if (result.completed)
        {
            completedEpisodes++;
            bestCompletionTime = Mathf.Min(bestCompletionTime, result.elapsedSeconds);
        }

        recentEpisodeResults.Enqueue(result);
        while (recentEpisodeResults.Count > Mathf.Max(1, rollingStatsWindow))
        {
            recentEpisodeResults.Dequeue();
        }
    }

    private void EnsureOverlayStyles()
    {
        if (overlayStyle == null)
        {
            overlayStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                richText = true,
                wordWrap = true,
                alignment = TextAnchor.UpperLeft
            };
            overlayStyle.normal.textColor = Color.white;
        }

        if (overlayBackgroundStyle == null)
        {
            Texture2D backgroundTexture = new Texture2D(1, 1);
            backgroundTexture.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.72f));
            backgroundTexture.Apply();

            overlayBackgroundStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = backgroundTexture }
            };
        }
    }

    private string BuildStatsOverlayText()
    {
        float averageReward = 0f;
        float averageTime = 0f;
        float averageSpeed = 0f;
        float averageProgress = 0f;
        int completedInWindow = 0;

        foreach (CarDrivingAgent.EpisodeResult result in recentEpisodeResults)
        {
            averageReward += result.totalReward;
            averageTime += result.elapsedSeconds;
            averageSpeed += result.averageSpeed;
            averageProgress += result.maxProgress;
            if (result.completed)
            {
                completedInWindow++;
            }
        }

        int windowCount = recentEpisodeResults.Count;
        if (windowCount > 0)
        {
            averageReward /= windowCount;
            averageTime /= windowCount;
            averageSpeed /= windowCount;
            averageProgress /= windowCount;
        }

        float completionRate = totalEpisodes > 0 ? (float)completedEpisodes / totalEpisodes : 0f;
        float windowCompletionRate = windowCount > 0 ? (float)completedInWindow / windowCount : 0f;
        string bestTimeText = float.IsPositiveInfinity(bestCompletionTime) ? "n/a" : $"{bestCompletionTime:F2}s";

        var builder = new StringBuilder(512);
        builder.AppendLine("<b>Training Stats</b>");
        builder.AppendLine($"Mode: {trainingMode}");
        builder.AppendLine($"Track: {ActiveTrackId}");
        builder.AppendLine($"Episodes: {totalEpisodes}");
        builder.AppendLine($"Total Steps: {totalSteps}");
        builder.AppendLine($"Completions: {completedEpisodes} ({completionRate:P1})");
        builder.AppendLine($"Best Time: {bestTimeText}");
        builder.AppendLine($"Best Reward: {bestReward:F2}");
        builder.AppendLine($"Window Avg Reward: {averageReward:F2}");
        builder.AppendLine($"Window Avg Time: {averageTime:F2}s");
        builder.AppendLine($"Window Avg Speed: {averageSpeed:F2}");
        builder.AppendLine($"Window Avg Progress: {averageProgress:F2}");
        builder.AppendLine($"Window Completion: {windowCompletionRate:P1}");
        builder.AppendLine($"Last Reward: {lastEpisodeReward:F2}");
        builder.AppendLine($"Last Time: {lastEpisodeTime:F2}s");
        builder.AppendLine($"Last Avg Speed: {lastEpisodeAverageSpeed:F2}");
        builder.AppendLine($"Last End: {lastTerminationReason}");
        builder.AppendLine($"Last Track: {lastTrackId}");
        return builder.ToString();
    }

    private string ResolveNextTrackId()
    {
        switch (trainingMode)
        {
            case TrainingMode.StandardTrackOnly:
                return currentTrainingTrackId;
            case TrainingMode.ContinualReplay:
                return ResolveReplayTrackId();
            case TrainingMode.Evaluation:
                return ResolveEvaluationTrackId();
            default:
                return currentTrainingTrackId;
        }
    }

    private string ResolveReplayTrackId()
    {
        bool canReplay = replayTrackIds != null && replayTrackIds.Count > 0;
        bool chooseReplay = canReplay && UnityEngine.Random.value < replayTrackProbability;
        if (!chooseReplay)
        {
            return currentTrainingTrackId;
        }

        int index = UnityEngine.Random.Range(0, replayTrackIds.Count);
        string replayTrackId = replayTrackIds[index];
        return string.IsNullOrWhiteSpace(replayTrackId) ? currentTrainingTrackId : replayTrackId.Trim();
    }

    private string ResolveEvaluationTrackId()
    {
        EnsureEvaluationSchedule();
        if (evaluationSchedule.Count == 0)
        {
            return currentTrainingTrackId;
        }

        string trackId = evaluationSchedule[evaluationScheduleIndex];
        evaluationScheduleIndex = (evaluationScheduleIndex + 1) % evaluationSchedule.Count;
        return trackId;
    }

    private void EnsureEvaluationSchedule()
    {
        if (evaluationSchedule.Count > 0)
        {
            return;
        }

        int repeats = Mathf.Max(1, evaluationEpisodesPerTrack);
        if (evaluationTrackIds == null || evaluationTrackIds.Count == 0)
        {
            evaluationSchedule.Add(currentTrainingTrackId);
            return;
        }

        for (int i = 0; i < evaluationTrackIds.Count; i++)
        {
            string trackId = evaluationTrackIds[i];
            if (string.IsNullOrWhiteSpace(trackId))
            {
                continue;
            }

            for (int episode = 0; episode < repeats; episode++)
            {
                evaluationSchedule.Add(trackId.Trim());
            }
        }
    }

    private string GetTrackFilePath(string trackId)
    {
        string streamingPath = Path.Combine(Application.dataPath, streamingTrackFolder, $"{trackId}.json");
#if UNITY_EDITOR
        string assetPath = Path.Combine(Application.dataPath, trackFolder, $"{trackId}.json");
        return File.Exists(streamingPath) ? streamingPath : assetPath;
#else
        return streamingPath;
#endif
    }

    private string GetResultsFilePath(string fileName)
    {
        return Path.Combine(Application.dataPath, resultsFolder, fileName);
    }
}

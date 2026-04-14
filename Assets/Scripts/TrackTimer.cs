using UnityEngine;
using UnityEngine.UI;

public class TrackTimer : MonoBehaviour
{
    [SerializeField] private Text timerText;
    [SerializeField] private string timePrefix = "Time: ";

    private bool isRunning;
    private float elapsedTime;
    private Canvas timerCanvas;
    private PrometeoCarController linkedCarController;

    private void Awake()
    {
        EnsureTimerText();
        UpdateDisplay();
    }

    private void Update()
    {
        EnsureTimerText();

        if (!isRunning)
        {
            UpdateDisplay();
            return;
        }

        elapsedTime += Time.deltaTime;
        UpdateDisplay();
    }

    public void ResetTimer()
    {
        isRunning = false;
        elapsedTime = 0f;
        UpdateDisplay();
    }

    public void StartTimer()
    {
        elapsedTime = 0f;
        isRunning = true;
        UpdateDisplay();
    }

    public void FinishTimer()
    {
        isRunning = false;
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        if (timerText == null)
        {
            return;
        }

        timerText.text = $"{timePrefix}{elapsedTime:000.00}s";
    }

    private void EnsureTimerText()
    {
        if (timerText != null)
        {
            return;
        }

        linkedCarController = FindFirstObjectByType<PrometeoCarController>();
        if (linkedCarController != null && linkedCarController.carSpeedText != null)
        {
            linkedCarController.useUI = false;
            timerText = linkedCarController.carSpeedText;
            timerText.alignment = TextAnchor.UpperLeft;
            ConfigureTimerText();
            HideUnitsText();
            return;
        }

        Text[] existingTexts = FindObjectsByType<Text>(FindObjectsSortMode.None);
        foreach (Text text in existingTexts)
        {
            if (text == null)
            {
                continue;
            }

            if (text.name == "Speed Text")
            {
                timerText = text;
                timerText.alignment = TextAnchor.UpperLeft;
                ConfigureTimerText();
                HideUnitsText();
                return;
            }
        }

        if (timerCanvas == null)
        {
            GameObject canvasObject = new GameObject("Track Timer Canvas");
            canvasObject.transform.SetParent(transform, false);
            timerCanvas = canvasObject.AddComponent<Canvas>();
            timerCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            timerCanvas.sortingOrder = 1000;

            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            canvasObject.AddComponent<GraphicRaycaster>();
        }

        GameObject textObject = new GameObject("Track Timer");
        textObject.transform.SetParent(timerCanvas.transform, false);

        RectTransform rectTransform = textObject.AddComponent<RectTransform>();
        rectTransform.anchorMin = new Vector2(1f, 1f);
        rectTransform.anchorMax = new Vector2(1f, 1f);
        rectTransform.pivot = new Vector2(1f, 1f);
        rectTransform.anchoredPosition = new Vector2(-24f, -24f);
        rectTransform.sizeDelta = new Vector2(260f, 60f);

        timerText = textObject.AddComponent<Text>();
        timerText.alignment = TextAnchor.UpperLeft;
        timerText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        timerText.fontSize = 16;
        timerText.color = Color.white;
        timerText.horizontalOverflow = HorizontalWrapMode.Overflow;
        timerText.verticalOverflow = VerticalWrapMode.Overflow;
        timerText.raycastTarget = false;
        ConfigureTimerText();
    }

    private void HideUnitsText()
    {
        Text[] allTexts = FindObjectsByType<Text>(FindObjectsSortMode.None);
        foreach (Text text in allTexts)
        {
            if (text == null || text == timerText)
            {
                continue;
            }

            if (text.name == "Units Text" || text.text.Trim().ToUpperInvariant() == "KPH")
            {
                text.gameObject.SetActive(false);
            }
        }
    }

    private void ConfigureTimerText()
    {
        if (timerText == null)
        {
            return;
        }

        timerText.horizontalOverflow = HorizontalWrapMode.Overflow;
        timerText.verticalOverflow = VerticalWrapMode.Overflow;
        timerText.resizeTextForBestFit = false;
        timerText.supportRichText = false;
        timerText.fontSize = 16;

        RectTransform rectTransform = timerText.rectTransform;
        rectTransform.pivot = new Vector2(0f, 1f);
    }
}

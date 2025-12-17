using UnityEngine;
using UnityEngine.UI;
using Fusion;

public class CoLocationUI : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private ColocationNetworkManager networkManager;
    [SerializeField] private Button hostButton;
    [SerializeField] private Button joinButton;
    [SerializeField] private Text statusText;
    [SerializeField] private Text instructionText; // ユーザーへの操作指示用（画面中央などに配置）

    [SerializeField] private SharedAnchorManager sharedAnchorManager;

    private System.Text.StringBuilder _logBuilder = new System.Text.StringBuilder();
    private const int MaxLogLines = 40;

    private void Start()
    {
        if (networkManager == null) networkManager = FindFirstObjectByType<ColocationNetworkManager>();
        if (sharedAnchorManager == null) sharedAnchorManager = FindFirstObjectByType<SharedAnchorManager>();

        if (instructionText != null)
        {
            instructionText.text = string.Empty;
        }

        if (sharedAnchorManager != null)
        {
            sharedAnchorManager.OnStatusMessage -= ShowInstruction;
            sharedAnchorManager.OnStatusMessage += ShowInstruction;
        }

        if (hostButton != null)
        {
            hostButton.onClick.AddListener(() =>
            {
                if (networkManager.Runner == null)
                {
                    networkManager.StartHost();
                    Log("Starting Host...");
                }
            });
        }

        if (joinButton != null)
        {
            joinButton.onClick.AddListener(() =>
            {
                if (networkManager.Runner == null)
                {
                    networkManager.StartClient();
                    Log("Starting Client...");
                }
            });
        }

        // シーン再読み込み後など、すでに Runner が動いているケース
        if (networkManager != null && networkManager.Runner != null && networkManager.Runner.IsRunning)
        {
            Log($"Session Active: {networkManager.Runner.GameMode}");
        }
    }

    private void Update()
    {
        if (statusText != null)
        {
            string statusLine = "State: Ready";
            if (networkManager != null &&
                networkManager.Runner != null &&
                networkManager.Runner.IsRunning &&
                networkManager.Object != null &&
                networkManager.Object.IsValid)
            {
                string anchorStatus = string.IsNullOrEmpty(networkManager.AnchorUuid.ToString()) ? "None" : "Shared";
                // ★修正：ローカライズ完了状態も表示
                string localizedStatus = "No";
                if (sharedAnchorManager != null && sharedAnchorManager.PrimaryAnchor != null)
                {
                    localizedStatus = "Yes";
                }
                statusLine = $"Mode: {networkManager.Runner.GameMode}\nAnchor: {anchorStatus}\nLocalized: {localizedStatus}";
            }

            statusText.text = $"{statusLine}\n\n{_logBuilder.ToString()}";
        }
    }

    private void OnEnable()
    {
        Application.logMessageReceived += HandleLog;
    }

    private void OnDisable()
    {
        Application.logMessageReceived -= HandleLog;

        if (sharedAnchorManager != null)
        {
            sharedAnchorManager.OnStatusMessage -= ShowInstruction;
        }
    }

    private void ShowInstruction(string msg)
    {
        if (instructionText == null) return;

        instructionText.text = msg;

        // 必要なら一定時間後に消すロジックを追加してもよい
        // StartCoroutine(ClearInstructionAfterDelay(5f));
    }

    /*
    private IEnumerator ClearInstructionAfterDelay(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        if (instructionText != null)
        {
            instructionText.text = string.Empty;
        }
    }
    */

    private void HandleLog(string logString, string stackTrace, UnityEngine.LogType type)
    {
        // ★修正：OutmeshNetworkSync のログも表示してデバッグしやすくする
        bool isRelevant = logString.Contains("[CoLocationUI]") ||
                          logString.Contains("[ColocationNetworkManager]") ||
                          logString.Contains("[SharedAnchorManager]") ||
                          logString.Contains("[OutmeshNetworkSync]") ||
                          type == UnityEngine.LogType.Error ||
                          type == UnityEngine.LogType.Exception;

        if (!isRelevant) return;

        string color = "white";
        if (type == UnityEngine.LogType.Error || type == UnityEngine.LogType.Exception) color = "red";
        else if (type == UnityEngine.LogType.Warning) color = "yellow";

        _logBuilder.AppendLine($"<color={color}>{logString}</color>");

        // Keep only last N lines
        string[] lines = _logBuilder.ToString().Split('\n');
        if (lines.Length > MaxLogLines)
        {
            _logBuilder.Clear();
            for (int i = lines.Length - MaxLogLines; i < lines.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]))
                    _logBuilder.AppendLine(lines[i]);
            }
        }
    }

    private void Log(string msg)
    {
        Debug.Log($"[CoLocationUI] {msg}");
    }
}

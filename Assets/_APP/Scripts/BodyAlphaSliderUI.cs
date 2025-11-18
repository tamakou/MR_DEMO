using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;

[DefaultExecutionOrder(1000)] // JSON適用などが済んだ後に動かす
public sealed class BodyAlphaSliderUI : MonoBehaviour
{
  [Header("Targets")]
  [Tooltip("outmesh.glb のルート。未指定ならこのオブジェクト配下を走査")]
  public Transform outmeshRoot;
  [Tooltip("ボディを特定する名前キー（大/小文字や _- は無視して部分一致）")]
  public string bodyNameKey = "body";

  [Header("UI")]
  [Tooltip("起動時の初期アルファ（0～1）。未指定時は現在の値を採用")]
  public float initialAlpha = 0.6f;
  [Tooltip("UIのアンカー領域（画面左下に小さなパネルを出します）")]
  public Vector2 panelAnchorMin = new Vector2(0.02f, 0.02f);
  public Vector2 panelAnchorMax = new Vector2(0.36f, 0.16f);
  public int sortingOrder = 32767;

  [Header("Rendering (optional)")]
  [Tooltip("Body を臓器より後で描画するためのキュー。Transparent(3000)+50=3100 推奨")]
  public int bodyRenderQueue = 3100;
  [Tooltip("背面カリング維持（片面描画）")]
  public bool forceCullBack = true;

  // URP共通プロパティ
  static readonly int ID_BaseColor = Shader.PropertyToID("_BaseColor");
  static readonly int ID_Cull = Shader.PropertyToID("_Cull");

  readonly List<(Renderer r, int submesh)> _targets = new();
  readonly MaterialPropertyBlock _mpb = new();

  Slider _slider;
  Text _valueText;

  // ★ ネットワーク用フック
  public event Action<float> SliderValueChanged;
  public float CurrentAlpha { get; private set; }

  void Start()
  {
    if (outmeshRoot == null) outmeshRoot = transform;

    CollectBodyRenderers();
    EnsureBodyMaterialsSetup();

    var startAlpha = DetermineCurrentAlpha();
    if (float.IsNaN(startAlpha))
      startAlpha = Mathf.Clamp01(initialAlpha);

    BuildUI(startAlpha);
    ApplyAlpha(startAlpha);
  }

  // Body の Renderer（サブメッシュ含む）を収集
  void CollectBodyRenderers()
  {
    string key = Normalize(bodyNameKey);
    foreach (var r in outmeshRoot.GetComponentsInChildren<Renderer>(true))
    {
      bool match = false;
      for (var t = r.transform; t != null && t != outmeshRoot.parent; t = t.parent)
      {
        if (Normalize(t.name).Contains(key)) { match = true; break; }
      }
      if (!match) continue;

      int count = r.sharedMaterials != null ? r.sharedMaterials.Length : 0;
      for (int i = 0; i < count; i++)
        _targets.Add((r, i));
    }
  }

  // Body マテリアルの描画順／片面化を一度だけ整える
  void EnsureBodyMaterialsSetup()
  {
    foreach (var (r, i) in _targets)
    {
      var mats = r.sharedMaterials;
      var m = mats[i];
      if (m == null) continue;

      // Body を最後に描画（透明の衝突を避ける）
      if (bodyRenderQueue > 0)
        m.renderQueue = bodyRenderQueue; // Transparent(3000)+α

      // 片面描画（軽量）
      if (forceCullBack && m.HasProperty(ID_Cull))
        m.SetInt(ID_Cull, (int)CullMode.Back);
    }
  }

  // 現在のアルファ（_BaseColor.a）を推定
  float DetermineCurrentAlpha()
  {
    if (_targets.Count == 0) return float.NaN;

    var (r, i) = _targets[0];
    _mpb.Clear();
    r.GetPropertyBlock(_mpb, i);

    Color c;
    if (_mpb.HasColor(ID_BaseColor))
      c = _mpb.GetColor(ID_BaseColor);
    else
    {
      var m = r.sharedMaterials[i];
      c = (m != null && m.HasProperty(ID_BaseColor)) ? m.GetColor(ID_BaseColor) : Color.white;
    }
    return Mathf.Clamp01(c.a);
  }

  // 画面下部にシンプルなスライダーUIを生成（uGUI）
  void BuildUI(float startAlpha)
  {
    var cgo = new GameObject("BodyAlpha_UI");
    var canvas = cgo.AddComponent<Canvas>();
    canvas.renderMode = RenderMode.ScreenSpaceOverlay;
    canvas.sortingOrder = sortingOrder;
    cgo.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
    cgo.AddComponent<GraphicRaycaster>();

    if (FindFirstObjectByType<EventSystem>() == null)
    {
      var es = new GameObject("EventSystem");
      es.AddComponent<EventSystem>();
      es.AddComponent<StandaloneInputModule>();
    }

    var panel = new GameObject("Panel", typeof(Image)).GetComponent<Image>();
    panel.transform.SetParent(cgo.transform, false);
    panel.color = new Color(0, 0, 0, 0.5f);
    var pr = panel.rectTransform;
    pr.anchorMin = panelAnchorMin;
    pr.anchorMax = panelAnchorMax;
    pr.offsetMin = pr.offsetMax = Vector2.zero;

    var label = new GameObject("Label", typeof(Text)).GetComponent<Text>();
    label.transform.SetParent(panel.transform, false);
    label.alignment = TextAnchor.UpperLeft;
    label.text = "Body Alpha";
    label.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
    var lr = label.rectTransform;
    lr.anchorMin = new Vector2(0, 0.5f);
    lr.anchorMax = new Vector2(1, 1);
    lr.offsetMin = new Vector2(10, -10);
    lr.offsetMax = new Vector2(-10, -10);

    _slider = new GameObject("Slider", typeof(Slider)).GetComponent<Slider>();
    _slider.transform.SetParent(panel.transform, false);
    var sr = _slider.GetComponent<RectTransform>();
    sr.anchorMin = new Vector2(0, 0);
    sr.anchorMax = new Vector2(1, 0.5f);
    sr.offsetMin = new Vector2(10, 10);
    sr.offsetMax = new Vector2(-10, -10);

    // スライダー見た目（簡易）
    var bg = new GameObject("Background", typeof(Image)).GetComponent<Image>();
    bg.transform.SetParent(_slider.transform, false);
    bg.rectTransform.anchorMin = new Vector2(0, 0);
    bg.rectTransform.anchorMax = new Vector2(1, 1);
    bg.rectTransform.offsetMin = bg.rectTransform.offsetMax = Vector2.zero;
    bg.color = new Color(1, 1, 1, 0.25f);

    var fillArea = new GameObject("Fill Area", typeof(RectTransform)).GetComponent<RectTransform>();
    fillArea.SetParent(_slider.transform, false);
    fillArea.anchorMin = new Vector2(0, 0);
    fillArea.anchorMax = new Vector2(1, 1);
    fillArea.offsetMin = new Vector2(10, 10);
    fillArea.offsetMax = new Vector2(-10, -10);

    var fill = new GameObject("Fill", typeof(Image)).GetComponent<Image>();
    fill.transform.SetParent(fillArea, false);
    fill.rectTransform.anchorMin = new Vector2(0, 0);
    fill.rectTransform.anchorMax = new Vector2(0, 1);
    fill.rectTransform.offsetMin = Vector2.zero;
    fill.rectTransform.offsetMax = Vector2.zero;
    _slider.fillRect = fill.rectTransform;

    var handleSlideArea = new GameObject("Handle Slide Area", typeof(RectTransform)).GetComponent<RectTransform>();
    handleSlideArea.SetParent(_slider.transform, false);
    handleSlideArea.anchorMin = new Vector2(0, 0);
    handleSlideArea.anchorMax = new Vector2(1, 1);
    handleSlideArea.offsetMin = new Vector2(10, 10);
    handleSlideArea.offsetMax = new Vector2(-10, -10);

    var handle = new GameObject("Handle", typeof(Image)).GetComponent<Image>();
    handle.transform.SetParent(handleSlideArea, false);
    handle.rectTransform.sizeDelta = new Vector2(20, 20);
    _slider.targetGraphic = handle;
    _slider.handleRect = handle.rectTransform;

    _valueText = new GameObject("Value", typeof(Text)).GetComponent<Text>();
    _valueText.transform.SetParent(panel.transform, false);
    _valueText.alignment = TextAnchor.LowerRight;
    _valueText.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
    var vr = _valueText.rectTransform;
    vr.anchorMin = new Vector2(0, 0);
    vr.anchorMax = new Vector2(1, 0.5f);
    vr.offsetMin = new Vector2(10, 10);
    vr.offsetMax = new Vector2(-10, 10);

    _slider.minValue = 0f;
    _slider.maxValue = 1f;
    _slider.wholeNumbers = false;
    _slider.value = startAlpha;
    _slider.onValueChanged.AddListener(OnSliderChanged);

    UpdateValueText(startAlpha);
  }

  void OnSliderChanged(float v)
  {
    ApplyAlpha(v);
    UpdateValueText(v);

    // ネットワーク側へ通知
    SliderValueChanged?.Invoke(v);
  }

  void UpdateValueText(float v)
  {
    _valueText.text = $"α = {v:0.00}  (A ≈ {Mathf.RoundToInt(v * 255f)})";
  }

  // MPB で _BaseColor.a を更新（マテリアル複製なし）
  void ApplyAlpha(float alpha)
  {
    alpha = Mathf.Clamp01(alpha);
    CurrentAlpha = alpha;

    foreach (var (r, i) in _targets)
    {
      _mpb.Clear();
      r.GetPropertyBlock(_mpb, i);

      Color c;
      if (_mpb.HasColor(ID_BaseColor))
        c = _mpb.GetColor(ID_BaseColor);
      else
      {
        var m = r.sharedMaterials[i];
        c = (m != null && m.HasProperty(ID_BaseColor))
            ? m.GetColor(ID_BaseColor)
            : Color.white;
      }

      c.a = alpha;
      _mpb.SetColor(ID_BaseColor, c);
      r.SetPropertyBlock(_mpb, i);
    }
  }

  /// <summary>
  /// ネットワークから受け取った α(0..1) を UI + Body に反映する。
  /// </summary>
  public void ApplyAlphaFromNetwork(float alpha)
  {
    alpha = Mathf.Clamp01(alpha);

    if (_slider != null)
      _slider.SetValueWithoutNotify(alpha);

    ApplyAlpha(alpha);
    UpdateValueText(alpha);
  }

  static string Normalize(string s)
  {
    if (string.IsNullOrEmpty(s)) return string.Empty;
    s = s.ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
    return s;
  }
}

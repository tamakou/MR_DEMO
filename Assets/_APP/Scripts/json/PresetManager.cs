using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Resources/xxx.json を読み、outmesh 内の各臓器へ RGB と A(透過) を適用するマネージャ。
/// - JSON の Name を outmesh 階層名（祖先名を含む部分一致）でマッチさせる
/// - A < 255 のときだけ URP Transparent へ切替、A=255 で元の Opaque に復帰
/// - 色は MaterialPropertyBlock で _BaseColor を RGBA 丸ごと上書き（マテリアル複製なし）
/// - UI 同期用に「臓器ごとの最終適用 α(0..255)」をイベントで通知（スライダー同期用）
///
/// 使い方：
///  1) シーンの outmesh ルートに本コンポーネントを付ける（または空Objに付けて outmeshRoot を割当て）
///  2) UI などから ApplyPresetResource("preset7") を呼ぶ
/// </summary>
[DisallowMultipleComponent]
public sealed class PresetManager : MonoBehaviour
{
  [Header("対象 outmesh のルート（未指定なら自身）")]
  [SerializeField] private Transform outmeshRoot;

  [Header("ログ出力")]
  [SerializeField] private bool logSummary = true;

  // ---- URP 標準プロパティID（Simple Lit / Lit / Unlit 共通の "_BaseColor"）
  static readonly int ID_BaseColor = Shader.PropertyToID("_BaseColor"); // URPのベース色。:contentReference[oaicite:2]{index=2}
  static readonly int ID_Surface = Shader.PropertyToID("_Surface");   // 0: Opaque, 1: Transparent
  static readonly int ID_Blend = Shader.PropertyToID("_Blend");     // 0: Alpha（存在する場合）
  static readonly int ID_SrcBlend = Shader.PropertyToID("_SrcBlend");
  static readonly int ID_DstBlend = Shader.PropertyToID("_DstBlend");
  static readonly int ID_AlphaClip = Shader.PropertyToID("_AlphaClip");
  static readonly int ID_ZWriteCtl = Shader.PropertyToID("_ZWriteControl"); // 0: Auto
  static readonly int ID_Cull = Shader.PropertyToID("_Cull");      // Back=2（片面描画）:contentReference[oaicite:3]{index=3}
  const string KW_SURFACE_TRANSPARENT = "_SURFACE_TYPE_TRANSPARENT";

  // 透明バリアントのキャッシュ（同じ元マテリアルから何度も複製しない）
  readonly Dictionary<Material, Material> _transparentCache = new();
  // 復帰用に、初回適用時の共有マテリアルを保持
  readonly Dictionary<Renderer, Material[]> _originalShared = new();

  // UI 同期用：直近に適用した α（0..255）を臓器キーごとに保存
  readonly Dictionary<string, int> _lastAlphaByKey = new();

  // UI に α 適用を通知（key=臓器名の正規化、小文字・記号除去）
  public event Action<string, int> OnAlphaApplied;

  // JSON モデル
  [Serializable] class FileModel { public string version; public string Name; public Preset[] Presets; }
  [Serializable] class Preset { public string Name; public int Display; public MLut MLut; }
  [Serializable] class MLut { public int R, G, B, A; }

  MaterialPropertyBlock _mpb;

  void Awake()
  {
    if (outmeshRoot == null) outmeshRoot = transform;
    _mpb = new MaterialPropertyBlock();
  }

  /// <summary>
  /// Resources/{presetName}.json を読み、RGB+A を outmesh に適用。
  /// </summary>
  public void ApplyPresetResource(string presetName)
  {
    if (string.IsNullOrEmpty(presetName))
    {
      Debug.LogError("[PresetManager] presetName が空です");
      return;
    }

    var ta = Resources.Load<TextAsset>(presetName);
    if (ta == null)
    {
      Debug.LogError($"[PresetManager] Resources/{presetName}.json が見つかりません");
      return;
    }

    FileModel data;
    try { data = JsonUtility.FromJson<FileModel>(ta.text); }
    catch (Exception e) { Debug.LogError($"[PresetManager] JSON パース失敗: {e.Message}"); return; }
    if (data?.Presets == null)
    {
      Debug.LogError("[PresetManager] JSON に Presets がありません");
      return;
    }

    var renderers = outmeshRoot.GetComponentsInChildren<Renderer>(includeInactive: true);

    int applied = 0, hidden = 0, missing = 0;

    foreach (var p in data.Presets)
    {
      string key = Normalize(p.Name);            // 例: "body" → "body"
      var targets = FindRenderersByAncestorName(renderers, key, outmeshRoot);
      if (targets.Count == 0) { missing++; continue; }

      // JSON → 0..1 へ変換
      float rf = Mathf.Clamp01(p.MLut.R / 255f);
      float gf = Mathf.Clamp01(p.MLut.G / 255f);
      float bf = Mathf.Clamp01(p.MLut.B / 255f);
      int a255 = Mathf.Clamp(p.MLut.A, 0, 255);
      float af = a255 / 255f;

      bool visible = p.Display != 0;
      bool needsTransparent = a255 < 255; // 透明ブレンドが必要

      foreach (var r in targets)
      {
        r.enabled = visible;
        if (!visible) { hidden++; continue; }

        // 復帰用に初回の共有マテリアルを保存
        if (!_originalShared.ContainsKey(r))
          _originalShared[r] = r.sharedMaterials;

        // Aに応じて Transparent 切替／復帰
        if (needsTransparent) AssignTransparentVariants(r);
        else RestoreOriginalMaterials(r);

        // 片面（Backface Culling）を維持して負荷削減
        ForceCullBack(r);

        // ---- ここが本修正：RGB+A を JSON 値で丸ごと上書き ----
        var targetColor = new Color(rf, gf, bf, af);
        var mats = r.sharedMaterials;
        for (int i = 0; i < mats.Length; i++)
        {
          _mpb.Clear();
          _mpb.SetColor(ID_BaseColor, targetColor);     // `_BaseColor` を直接上書き（軽量・確実）:contentReference[oaicite:4]{index=4}
          r.SetPropertyBlock(_mpb, i);                  // Renderer 単位で適用（マテリアルは共有のまま）:contentReference[oaicite:5]{index=5}
        }
        applied++;
      }

      // α を記録＆UI へ通知（スライダーをプリセット値に同期させる）
      _lastAlphaByKey[key] = a255;
      OnAlphaApplied?.Invoke(key, a255);
    }

    if (logSummary)
      Debug.Log($"[PresetManager] '{presetName}' 適用: 適用={applied}, 非表示={hidden}, 見つからず={missing}");
  }

  /// <summary>直近に適用した α(0..255) を取得（UI初期化用）</summary>
  public bool TryGetAppliedAlpha(string organKey, out int a255)
      => _lastAlphaByKey.TryGetValue(Normalize(organKey), out a255);

  // ----------------- 内部ユーティリティ -----------------

  static string Normalize(string s)
  {
    if (string.IsNullOrEmpty(s)) return string.Empty;
    s = s.ToLowerInvariant();
    return s.Replace(" ", "").Replace("_", "").Replace("-", "");
  }

  static List<Renderer> FindRenderersByAncestorName(Renderer[] all, string normalizedKey, Transform root)
  {
    var list = new List<Renderer>();
    if (string.IsNullOrEmpty(normalizedKey)) return list;

    foreach (var r in all)
    {
      Transform t = r.transform;
      while (t != null && t != root.parent)
      {
        if (Normalize(t.name).Contains(normalizedKey)) { list.Add(r); break; }
        t = t.parent;
      }
    }
    return list;
  }

  void RestoreOriginalMaterials(Renderer r)
  {
    if (_originalShared.TryGetValue(r, out var orig))
      r.sharedMaterials = orig;
  }

  void AssignTransparentVariants(Renderer r)
  {
    var shared = r.sharedMaterials; if (shared == null) return;
    bool changed = false;

    for (int i = 0; i < shared.Length; i++)
    {
      var src = shared[i]; if (src == null) continue;
      if (IsTransparent(src)) continue;

      if (!_transparentCache.TryGetValue(src, out var variant))
      {
        variant = CreateTransparentVariant(src);
        _transparentCache[src] = variant;
      }
      shared[i] = variant; changed = true;
    }
    if (changed) r.sharedMaterials = shared;
  }

  static bool IsTransparent(Material m)
  {
    bool surface = m.HasProperty(ID_Surface) && m.GetFloat(ID_Surface) > 0.5f;
    bool kw = m.IsKeywordEnabled(KW_SURFACE_TRANSPARENT);
    string rt = m.GetTag("RenderType", false);
    return surface || kw || string.Equals(rt, "Transparent", StringComparison.OrdinalIgnoreCase);
  }

  static Material CreateTransparentVariant(Material src)
  {
    var m = new Material(src) { name = src.name + " (TransparentVariant)" };

    // URP Transparent の必須設定（Alphaブレンド）
    if (m.HasProperty(ID_Surface)) m.SetFloat(ID_Surface, 1f); // Transparent
    if (m.HasProperty(ID_Blend)) m.SetFloat(ID_Blend, 0f); // Alpha
    if (m.HasProperty(ID_AlphaClip)) m.SetFloat(ID_AlphaClip, 0f);
    if (m.HasProperty(ID_SrcBlend)) m.SetInt(ID_SrcBlend, (int)BlendMode.SrcAlpha);
    if (m.HasProperty(ID_DstBlend)) m.SetInt(ID_DstBlend, (int)BlendMode.OneMinusSrcAlpha);
    if (m.HasProperty(ID_ZWriteCtl)) m.SetInt(ID_ZWriteCtl, 0);  // Auto

    // 片面カリング（負荷削減）
    if (m.HasProperty(ID_Cull)) m.SetInt(ID_Cull, (int)CullMode.Back); // Backface culling（既定）:contentReference[oaicite:6]{index=6}

    m.EnableKeyword(KW_SURFACE_TRANSPARENT);
    m.SetOverrideTag("RenderType", "Transparent");
    m.renderQueue = (int)RenderQueue.Transparent; // 3000（不透明の後に描画）:contentReference[oaicite:7]{index=7}

    return m;
  }

  static void ForceCullBack(Renderer r)
  {
    var mats = r.sharedMaterials; if (mats == null) return;
    bool changed = false;
    for (int i = 0; i < mats.Length; i++)
    {
      var m = mats[i];
      if (m != null && m.HasProperty(ID_Cull))
      {
        int cur = (int)m.GetFloat(ID_Cull);
        if (cur != (int)CullMode.Back) { m.SetInt(ID_Cull, (int)CullMode.Back); changed = true; }
      }
    }
    if (changed) r.sharedMaterials = mats;
  }
}


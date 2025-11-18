using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 起動時に Resources/preset7.json を読み、outmesh.glb 内の各臓器に
/// RGB と A(透過) を適用する最小・軽量の実装。
/// ・色は MaterialPropertyBlock(_BaseColor) で反映（マテリアル複製を避ける）
/// ・A<255 の場合のみ、元の共有マテリアルから透明バリアントを1度作成して差し替え（キャッシュ再利用）
/// ・Display=0 は Renderer を無効化
/// アタッチ先：outmesh.glb のルート（推奨）
/// JSON 置き場所：Assets/Resources/preset7.json
/// </summary>
public sealed class ApplyPresetFromJson : MonoBehaviour
{
  [Header("JSON (Resources path, without .json)")]
  [SerializeField] private string jsonResourcePath = "preset6";

  [Header("対象ルート (未指定時は自身)")]
  [SerializeField] private Transform outmeshRoot;

  [Header("ログ")]
  [SerializeField] private bool logSummary = true;

  // --- URP共通プロパティID（コスト削減のためキャッシュ）
  static readonly int ID_BaseColor = Shader.PropertyToID("_BaseColor");
  static readonly int ID_Surface = Shader.PropertyToID("_Surface");   // 0:Opaque, 1:Transparent
  static readonly int ID_Cull = Shader.PropertyToID("_Cull");      // CullMode.Back=2
  static readonly int ID_SrcBlend = Shader.PropertyToID("_SrcBlend");
  static readonly int ID_DstBlend = Shader.PropertyToID("_DstBlend");
  static readonly int ID_ZWriteCtl = Shader.PropertyToID("_ZWriteControl"); // 0:Auto
  static readonly int ID_AlphaClip = Shader.PropertyToID("_AlphaClip");

  // URP Transparent 用キーワード
  const string KW_SURFACE_TRANSPARENT = "_SURFACE_TYPE_TRANSPARENT";

  // 透明バリアントのキャッシュ（同じ元マテリアルを何度も複製しない）
  private readonly Dictionary<Material, Material> _transparentVariantCache = new Dictionary<Material, Material>();

  private MaterialPropertyBlock _mpb;

  // JSON モデル ------------------------------------------------------------
  [Serializable]
  private class PresetFile
  {
    public string version;
    public string Name;
    public Preset[] Presets;
  }
  [Serializable]
  private class Preset
  {
    public string Name;
    public int Display;
    public MLut MLut;
  }
  [Serializable]
  private class MLut
  {
    public int R, G, B, A;
  }

  void Awake()
  {
    if (outmeshRoot == null) outmeshRoot = transform;
    _mpb = new MaterialPropertyBlock();

    var text = Resources.Load<TextAsset>(jsonResourcePath);
    if (text == null)
    {
      Debug.LogError($"[ApplyPresetFromJson] JSONが見つかりません: Resources/{jsonResourcePath}.json");
      return;
    }

    PresetFile file;
    try
    {
      file = JsonUtility.FromJson<PresetFile>(text.text);
    }
    catch (Exception e)
    {
      Debug.LogError($"[ApplyPresetFromJson] JSON パースに失敗: {e.Message}");
      return;
    }
    if (file == null || file.Presets == null)
    {
      Debug.LogError("[ApplyPresetFromJson] JSON 内容が不正です（Presets が見つかりません）");
      return;
    }

    // 走査を1回で済ませるため、シーン内のレンダラーを先に収集
    var renderers = outmeshRoot.GetComponentsInChildren<Renderer>(includeInactive: true);

    int appliedCount = 0, hiddenCount = 0, missingCount = 0;

    foreach (var preset in file.Presets)
    {
      // 名前マッチは大文字小文字/記号を無視してゆるく判定
      var targetKey = Normalize(preset.Name);

      // 対象レンダラー抽出（名前に含まれるものを採用）
      var targets = FindMatchingRenderers(renderers, targetKey);

      if (targets.Count == 0)
      {
        // 見つからない場合は警告だけ（GLB側の命名差異）
        if (logSummary) Debug.LogWarning($"[ApplyPresetFromJson] マッチするオブジェクトが見つかりません: {preset.Name}");
        missingCount++;
        continue;
      }

      // 表示/非表示
      bool visible = preset.Display != 0;

      // 色/アルファ
      float rf = Mathf.Clamp01(preset.MLut.R / 255f);
      float gf = Mathf.Clamp01(preset.MLut.G / 255f);
      float bf = Mathf.Clamp01(preset.MLut.B / 255f);
      float af = Mathf.Clamp01(preset.MLut.A / 255f);

      bool needsTransparent = af < 0.999f; // 255未満なら透明扱い

      foreach (var r in targets)
      {
        if (!visible)
        {
          r.enabled = false;
          hiddenCount++;
          continue;
        }
        r.enabled = true;

        // アルファが必要な場合のみ、透明バリアントに差し替え
        if (needsTransparent) AssignTransparentVariant(r);
        // 片面描画（背面カリング）は維持
        ForceCullBackOnAllShared(r);

        // _BaseColor を MPB で上書き（各サブメッシュに適用）
        var mats = r.sharedMaterials;
        for (int i = 0; i < mats.Length; i++)
        {
          _mpb.Clear();
          _mpb.SetColor(ID_BaseColor, new Color(rf, gf, bf, af));
          r.SetPropertyBlock(_mpb, i);
        }
        appliedCount++;
      }
    }

    if (logSummary)
    {
      Debug.Log($"[ApplyPresetFromJson] 適用完了: 適用={appliedCount}, 非表示={hiddenCount}, 見つからず={missingCount}");
    }
  }

  // --- 補助: 名前正規化（小文字化＋空白/記号削除）
  private static string Normalize(string s)
  {
    if (string.IsNullOrEmpty(s)) return string.Empty;
    s = s.ToLowerInvariant();
    s = s.Replace(" ", "").Replace("_", "").Replace("-", "");
    return s;
  }

  // --- 補助: 対象抽出（名前に正規化キーを含む Renderer を抽出）
  private static List<Renderer> FindMatchingRenderers(Renderer[] all, string normalizedKey)
  {
    var list = new List<Renderer>();
    if (string.IsNullOrEmpty(normalizedKey)) return list;

    for (int i = 0; i < all.Length; i++)
    {
      var goName = Normalize(all[i].gameObject.name);
      if (goName.Contains(normalizedKey))
        list.Add(all[i]);
    }
    return list;
  }

  // --- 透明バリアントを割り当て（1つの元マテリアルにつき1回だけ生成）
  private void AssignTransparentVariant(Renderer r)
  {
    var shared = r.sharedMaterials;
    bool changed = false;

    for (int i = 0; i < shared.Length; i++)
    {
      var src = shared[i];
      if (src == null) continue;

      // 既に Transparent 設定ならそのまま
      if (IsTransparent(src)) continue;

      // 透明バリアントをキャッシュから取得 or 生成
      if (!_transparentVariantCache.TryGetValue(src, out var variant))
      {
        variant = CreateTransparentVariant(src);
        _transparentVariantCache[src] = variant;
      }
      shared[i] = variant;
      changed = true;
    }

    if (changed) r.sharedMaterials = shared;
  }

  // --- マテリアルが透明設定か簡易判定
  private static bool IsTransparent(Material m)
  {
    // URPは _Surface==1 か、Transparentタグ/キーワードで判定可能
    var hasSurface = m.HasProperty(ID_Surface) && m.GetFloat(ID_Surface) > 0.5f;
    var hasKeyword = m.IsKeywordEnabled(KW_SURFACE_TRANSPARENT);
    var renderType = m.GetTag("RenderType", false);
    return hasSurface || hasKeyword || string.Equals(renderType, "Transparent", StringComparison.OrdinalIgnoreCase);
  }

  // --- 透明バリアントの生成（URP既定のアルファブレンド）
  private static Material CreateTransparentVariant(Material src)
  {
    var m = new Material(src) { name = src.name + " (TransparentVariant)" };

    if (m.HasProperty(ID_Surface)) m.SetFloat(ID_Surface, 1f); // Transparent
    if (m.HasProperty(ID_AlphaClip)) m.SetFloat(ID_AlphaClip, 0f);
    m.EnableKeyword(KW_SURFACE_TRANSPARENT);

    // ブレンド（Alpha）
    if (m.HasProperty(ID_SrcBlend)) m.SetInt(ID_SrcBlend, (int)BlendMode.SrcAlpha);
    if (m.HasProperty(ID_DstBlend)) m.SetInt(ID_DstBlend, (int)BlendMode.OneMinusSrcAlpha);
    if (m.HasProperty(ID_ZWriteCtl)) m.SetInt(ID_ZWriteCtl, 0); // Auto

    // 片面描画（背面カリング）を維持
    if (m.HasProperty(ID_Cull)) m.SetInt(ID_Cull, (int)CullMode.Back);

    // RenderType/Queue を Transparent に
    m.SetOverrideTag("RenderType", "Transparent");
    m.renderQueue = (int)RenderQueue.Transparent;

    return m;
  }

  // --- 共有マテリアルに背面カリングを強制（軽量化・裏面非描画）
  private static void ForceCullBackOnAllShared(Renderer r)
  {
    var mats = r.sharedMaterials;
    bool changed = false;
    for (int i = 0; i < mats.Length; i++)
    {
      var m = mats[i];
      if (m == null || !m.HasProperty(ID_Cull)) continue;
      if ((int)m.GetFloat(ID_Cull) != (int)CullMode.Back)
      {
        m.SetInt(ID_Cull, (int)CullMode.Back);
        changed = true;
      }
    }
    if (changed) r.sharedMaterials = mats;
  }
}

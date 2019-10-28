using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// use multi steps bake to avoid Unity crash...
/// </summary>
public class PVSBakerAvoidCrashWindow : EditorWindow
{
  private PVSManager _pvsManager;
  private Vector2 _scrollPosition;

  /// <summary>
  /// show bake window.
  /// </summary>
  [MenuItem("PVS/Bake Window")]
  public static void ShowBakeWindow()
  {
    var window = (PVSBakerAvoidCrashWindow)GetWindow(typeof(PVSBakerAvoidCrashWindow));
    window.Show();
  }

  /// <summary>
  /// Unity OnGUI.
  /// </summary>
  public void OnGUI()
  {
    _pvsManager = EditorGUILayout.ObjectField("PVS Manager", _pvsManager, typeof(PVSManager), true) as PVSManager;

    if (!_pvsManager || null == _pvsManager.visibleFlags || _pvsManager.visibleFlags.Length <= 0) return;

    if (GUILayout.Button("Init"))
    {
      PVSBaker.InitPVSManager(_pvsManager);
    }

    _scrollPosition = GUILayout.BeginScrollView(_scrollPosition);
    try
    {
      for (var i = 0; i < _pvsManager.renderers.Count; i += 3)
      {
        if (!GUILayout.Button($"Bake {i}-{i + 3}")) continue;

        _pvsManager.BuildAccelerationStructure();
        try
        {
          for (var index = i; index < Mathf.Min(_pvsManager.renderers.Count, i + 3); ++index)
          {
            _pvsManager.accelerationStructure.Build();
            PVSBaker.BakeVisibleFlagForRenderer(_pvsManager, index, index / 3.0f);
          }

          UnityEditor.SceneManagement.EditorSceneManager.SaveOpenScenes();
        }
        finally
        {
          EditorUtility.ClearProgressBar();
          _pvsManager.ReleaseBakeResource();
        }
      }
    }
    finally
    {
      GUILayout.EndScrollView();
    }
  }
}

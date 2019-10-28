using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// the eye.
/// </summary>
public class Eye : MonoBehaviour
{
  public bool isDrawGizmos = true;

  /// <summary>
  /// Unity OnDrawGizmos.
  /// </summary>
  public void OnDrawGizmos()
  {
    if (isDrawGizmos)
      Gizmos.DrawIcon(transform.position, "eye.png", true, Color.yellow);
    Gizmos.color = Color.white;
  }
}

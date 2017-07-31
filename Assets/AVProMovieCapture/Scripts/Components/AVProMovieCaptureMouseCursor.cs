using UnityEngine;
using System.Collections;

//-----------------------------------------------------------------------------
// Copyright 2012-2017 RenderHeads Ltd.  All rights reserved.
//-----------------------------------------------------------------------------

public class AVProMovieCaptureMouseCursor : MonoBehaviour
{
	[SerializeField]
	private Texture2D _texture;

	[SerializeField]
	private int _depth = -9999;

	private GUIContent _content;

	void Start()
	{
		SetTexture(_texture);
	}

	public void SetTexture(Texture2D texture)
	{
		if (texture != null)
		{
			_content = new GUIContent(texture);
			_texture = texture;
		}
	}
	
	private void OnGUI()
	{
		if (_content != null)
		{
			GUI.depth = _depth;

			Vector2 p = Event.current.mousePosition;

			Rect rect = new Rect(p.x, p.y, _texture.width, _texture.height);

			GUI.Label(rect, _content);		
		}
	}
}

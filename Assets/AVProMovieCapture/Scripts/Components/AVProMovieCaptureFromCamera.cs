#if UNITY_5
	#if !UNITY_5_0 && !UNITY_5_1
		#define AVPRO_MOVIECAPTURE_GLISSUEEVENT_52
	#endif
#endif
using UnityEngine;

//-----------------------------------------------------------------------------
// Copyright 2012-2017 RenderHeads Ltd.  All rights reserved.
//-----------------------------------------------------------------------------

[RequireComponent(typeof(Camera))]
[AddComponentMenu("AVPro Movie Capture/From Camera (requires Camera)")]
public class AVProMovieCaptureFromCamera : AVProMovieCaptureBase
{
	private RenderTexture _target;
	private Camera _camera;
	private System.IntPtr _targetNativePointer = System.IntPtr.Zero;

	// If we're forcing a resolution or AA change then we have to render the camera again to the new target
	// If we try to just set the targetTexture of the camera and grab it in OnRenderImage we can't render it to the screen as before :(
	public override void UpdateFrame()
	{
		if (_capturing && !_paused && _camera != null)
		{
			bool canGrab = true;

			if (IsUsingMotionBlur())
			{
				// If the motion blur is still accumulating, don't grab this frame
				canGrab = _motionBlur.IsFrameAccumulated;
			}

			if (canGrab)
			{		
				while (_handle >= 0 && !AVProMovieCapturePlugin.IsNewFrameDue(_handle))
				{
					System.Threading.Thread.Sleep(1);
				}
				if (_handle >= 0)
				{
					// Render the camera
					{
						RenderTexture prev = _camera.targetTexture;
						// Reset the viewport rect as we're rendering to a texture captures the full viewport
						Rect prevRect = _camera.rect;
						CameraClearFlags prevClear = _camera.clearFlags;
						Color prevColor = _camera.backgroundColor;
						bool clearChanged = false;
						if (_camera.clearFlags == CameraClearFlags.Nothing || _camera.clearFlags == CameraClearFlags.Depth)
						{
							clearChanged = true;
							_camera.clearFlags = CameraClearFlags.SolidColor;
							_camera.backgroundColor = Color.black;
						}

						// Render
						_camera.rect = new Rect(0f, 0f, 1f, 1f);
						_camera.targetTexture = _target;
						_camera.Render();

						// Restore camera
						{
							_camera.rect = prevRect;
							if (clearChanged)
							{
								_camera.clearFlags = prevClear;
								_camera.backgroundColor = prevColor;
							}
							_camera.targetTexture = prev;
						}
					}

					// NOTE: If support for captures to survive through alt-tab events, or window resizes where the GPU resources are recreated
					// is required, then this line is needed.  It is very expensive though as it does a sync with the rendering thread.
					_targetNativePointer = _target.GetNativeTexturePtr();

					AVProMovieCapturePlugin.SetTexturePointer(_handle, _targetNativePointer);

#if AVPRO_MOVIECAPTURE_GLISSUEEVENT_52
					GL.IssuePluginEvent(_renderEventFunction, AVProMovieCapturePlugin.PluginID | (int)AVProMovieCapturePlugin.PluginEvent.CaptureFrameBuffer | _handle);
#else
					GL.IssuePluginEvent(AVProMovieCapturePlugin.PluginID | (int)AVProMovieCapturePlugin.PluginEvent.CaptureFrameBuffer | _handle);
#endif

					if (IsRecordingUnityAudio())
					{
						int audioDataLength = 0;
						System.IntPtr audioDataPtr = _audioCapture.ReadData(out audioDataLength);
						if (audioDataLength > 0)
						{
							AVProMovieCapturePlugin.EncodeAudio(_handle, audioDataPtr, (uint)audioDataLength);
						}
					}

					UpdateFPS();
				}
			}
		}
		base.UpdateFrame();
	}

#if false
	private void OnRenderImage(RenderTexture source, RenderTexture dest)
	{
		if (_capturing && !_paused)
		{
#if true
			while (_handle >= 0 && !AVProMovieCapturePlugin.IsNewFrameDue(_handle))
			{
				System.Threading.Thread.Sleep(1);
			}
			if (_handle >= 0)
			{
                if (_audioCapture && _audioDeviceIndex < 0 && !_noAudio && _isRealTime)
                {
					int audioDataLength = 0;
					System.IntPtr audioDataPtr = _audioCapture.ReadData(out audioDataLength);
					if (audioDataLength > 0)
					{
						AVProMovieCapturePlugin.EncodeAudio(_handle, audioDataPtr, (uint)audioDataLength);
					}
                }

                // In Direct3D the RT can be flipped vertically
                /*if (source.texelSize.y < 0)
                {

                }*/

				Graphics.Blit(source, dest);

				_lastSource = source;
				_lastDest = dest;

				if (dest != _originalTarget)
				{
					Graphics.Blit(dest, _originalTarget);
				}

#if AVPRO_MOVIECAPTURE_GLISSUEEVENT_52
				GL.IssuePluginEvent(AVProMovieCapturePlugin.GetRenderEventFunc(), AVProMovieCapturePlugin.PluginID | (int)AVProMovieCapturePlugin.PluginEvent.CaptureFrameBuffer | _handle);
#else
                GL.IssuePluginEvent(AVProMovieCapturePlugin.PluginID | (int)AVProMovieCapturePlugin.PluginEvent.CaptureFrameBuffer | _handle);
#endif
				GL.InvalidateState();
				
				UpdateFPS();

				return;
			}
#endif
		}

		// Pass-through
		Graphics.Blit(source, dest);

		_lastSource = source;
		_lastDest = dest;
	}
#endif

	public override void UnprepareCapture()
	{
		AVProMovieCapturePlugin.SetTexturePointer(_handle, System.IntPtr.Zero);

		if (_target != null)
		{
			_target.DiscardContents();
		}

		base.UnprepareCapture();
	}

	public override bool PrepareCapture()
	{
		if (_capturing)
		{
			return false;
		}

		if (SystemInfo.graphicsDeviceVersion.StartsWith("OpenGL"))
		{
			Debug.LogError("[AVProMovieCapture] OpenGL not yet supported for AVProMovieCaptureFromCamera component, please use Direct3D instead");
			return false;
		}

		// Setup material
		_pixelFormat = AVProMovieCapturePlugin.PixelFormat.RGBA32;
        _isTopDown = true;

		_camera = this.GetComponent<Camera>();
		int width = Mathf.FloorToInt(_camera.pixelRect.width);
		int height = Mathf.FloorToInt(_camera.pixelRect.height);

		
		// Setup rendering a different render target if we're overriding resolution or anti-aliasing
		//if (_renderResolution != Resolution.Original || (_renderAntiAliasing > 0 && _renderAntiAliasing != QualitySettings.antiAliasing))
		{
			if (_renderResolution == Resolution.Custom)
			{
				width = _renderWidth;
				height = _renderHeight;
			}
			else if (_renderResolution != Resolution.Original)
			{
				GetResolution(_renderResolution, ref width, ref height);
			}

			int aaLevel = QualitySettings.antiAliasing;
			if (aaLevel == 0)
			{
				aaLevel = 1;
			}
			if (_renderAntiAliasing > 0)
			{
				aaLevel = _renderAntiAliasing;
			}

			if (aaLevel != 1 && aaLevel != 2 && aaLevel != 4 && aaLevel != 8)
			{
				Debug.LogError("[AVProMovieCapture] Invalid antialiasing value, must be 1, 2, 4 or 8.  Defaulting to 1. >> " + aaLevel);
				aaLevel = 1;
			}

			// Create the render target
			if (_target != null)
			{
				_target.DiscardContents();
				if (_target.width != width || _target.height != height || _target.antiAliasing != aaLevel)
				{
					_targetNativePointer = System.IntPtr.Zero;
					RenderTexture.ReleaseTemporary(_target);
					_target = null;
				}
			}
			if (_target == null)
			{
				_target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.Default, RenderTextureReadWrite.Default, aaLevel);
				_target.name = "[AVProMovieCapture] Camera Target";
				
				_target.Create();
				_targetNativePointer = _target.GetNativeTexturePtr();
			}

			//camera.targetTexture = _target;

			// Adjust size for camera rectangle
			/*if (camera.rect.width < 1f || camera.rect.height < 1f)
			{
				float rectWidth = Mathf.Clamp01(camera.rect.width + camera.rect.x) - Mathf.Clamp01(camera.rect.x);
				float rectHeight = Mathf.Clamp01(camera.rect.height + camera.rect.y) - Mathf.Clamp01(camera.rect.y);
				width = Mathf.FloorToInt(width * rectWidth);
				height = Mathf.FloorToInt(height * rectHeight);
			}*/

			if (_useMotionBlur)
			{
				_motionBlurCameras = new Camera[1];
				_motionBlurCameras[0] = _camera;
			}
		}

		SelectRecordingResolution(width, height);

		GenerateFilename();

		return base.PrepareCapture();
	}

	public override void OnDestroy()
	{
		if (_target != null)
		{
			_targetNativePointer = System.IntPtr.Zero;
			RenderTexture.ReleaseTemporary(_target);
			_target = null;
		}

		base.OnDestroy();
	}
}
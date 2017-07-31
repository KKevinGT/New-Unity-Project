#if UNITY_5
	#if !UNITY_5_0 && !UNITY_5_1
		#define AVPRO_MOVIECAPTURE_GLISSUEEVENT_52
	#endif
#endif

using UnityEngine;

//-----------------------------------------------------------------------------
// Copyright 2012-2017 RenderHeads Ltd.  All rights reserved.
//-----------------------------------------------------------------------------

[AddComponentMenu("AVPro Movie Capture/From Texture")]
public class AVProMovieCaptureFromTexture : AVProMovieCaptureBase
{
	private Texture _sourceTexture;
	private RenderTexture _renderTexture;

	public void SetSourceTexture(Texture texture)
	{
		_sourceTexture = texture;
	}
		
	public override void UpdateFrame()
	{
		if (_capturing && !_paused && _sourceTexture)
		{
			bool canGrab = true;

			// If motion blur is enabled, wait until all frames are accumulated
			if (IsUsingMotionBlur())
			{
				// If the motion blur is still accumulating, don't grab this frame
				canGrab = _motionBlur.IsFrameAccumulated;
			}

			if (canGrab)
			{
				// Wait for the encoder to be ready for another frame
				while (_handle >= 0 && !AVProMovieCapturePlugin.IsNewFrameDue(_handle))
				{
					System.Threading.Thread.Sleep(1);
				}
				if (_handle >= 0)
				{
					// If motion blur is enabled, use the motion blur result
					Texture sourceTexture = _sourceTexture;
					if (IsUsingMotionBlur())
					{
						sourceTexture = _motionBlur.FinalTexture;
					}

					// If the texture isn't a RenderTexture then blit it to the Rendertexture so the native plugin can grab it
					if (sourceTexture is RenderTexture)
					{
						AVProMovieCapturePlugin.SetTexturePointer(_handle, sourceTexture.GetNativeTexturePtr());
					}
					else
					{
						Graphics.Blit(sourceTexture, _renderTexture);
						AVProMovieCapturePlugin.SetTexturePointer(_handle, _renderTexture.GetNativeTexturePtr());
					}

#if AVPRO_MOVIECAPTURE_GLISSUEEVENT_52
					GL.IssuePluginEvent(_renderEventFunction, AVProMovieCapturePlugin.PluginID | (int)AVProMovieCapturePlugin.PluginEvent.CaptureFrameBuffer | _handle);
#else
					GL.IssuePluginEvent(AVProMovieCapturePlugin.PluginID | (int)AVProMovieCapturePlugin.PluginEvent.CaptureFrameBuffer | _handle);
#endif

					// Handle audio from Unity
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

	private void LateUpdate()
	{
		if (_motionBlur != null)
		{
			_motionBlur.Accumulate(_sourceTexture);
		}
	}

	public override bool PrepareCapture()
	{
		if (_capturing)
		{
			return false;
		}

		if (_sourceTexture == null)
		{
			Debug.LogError("[AVProMovieCapture] No texture set to record");
			return false;
		}

		if (SystemInfo.graphicsDeviceVersion.StartsWith("OpenGL"))
		{
			Debug.LogError("[AVProMovieCapture] OpenGL not yet supported for AVProMovieCaptureFromTexture component, please use Direct3D instead");
			return false;
		}

		_pixelFormat = AVProMovieCapturePlugin.PixelFormat.RGBA32;

		SelectRecordingResolution(_sourceTexture.width, _sourceTexture.height);
		
		_renderTexture = RenderTexture.GetTemporary(_targetWidth, _targetHeight, 0, RenderTextureFormat.ARGB32);
		_renderTexture.Create();

		GenerateFilename();

		return base.PrepareCapture();
	}

	public override void UnprepareCapture()
	{
		AVProMovieCapturePlugin.SetTexturePointer(_handle, System.IntPtr.Zero);

		if (_renderTexture != null)
		{
			RenderTexture.ReleaseTemporary(_renderTexture);
			_renderTexture = null;
		}

		base.UnprepareCapture();
	}
}
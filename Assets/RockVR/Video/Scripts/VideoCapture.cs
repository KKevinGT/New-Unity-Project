using UnityEngine;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.InteropServices;

namespace RockVR.Video
{
    /// <summary>
    /// <c>VideoCapture</c> component.
    /// Place this script to target <c>Camera</c> component, this will capture
    /// camera's render texture and encode to video file.
    /// </summary>
    public class VideoCapture : VideoCaptureBase
    {
        /// <summary>
        /// Get or set the current status.
        /// </summary>
        /// <value>The current status.</value>
        public VideoCaptureCtrl.StatusType status { get; set; }
        /// <summary>
        /// For generate equirectangular video.
        /// </summary>
        public Material cubemap2Equirectangular;
        /// <summary>
        /// The stereo offset value.
        /// </summary>
        public float stereoSeparation = 1f;
        /// <summary>
        /// Setup Time.maximumDeltaTime to avoiding nasty stuttering.
        /// https://docs.unity3d.com/ScriptReference/Time-maximumDeltaTime.html
        /// </summary>
        public bool offlineRender = false;
        /// <summary>
        /// The texture holding the video frame data.
        /// </summary>
        private Texture2D frameTexture;
        private RenderTexture frameRenderTexture;
        private Cubemap frameCubemap;
        /// <summary>
        /// The material for copy stereo video.
        /// </summary>
        private Material blitMaterial;
        /// <summary>
        /// The material for processing stereoscopic video format.
        /// </summary>
        private Material stereoPackMaterial;
        /// <summary>
        /// The stereo target texture.
        /// </summary>
        private RenderTexture stereoTargetTexture;
        /// <summary>
        /// The finally stereo target.
        /// </summary>
        private RenderTexture finalTarget;
        /// <summary>
        /// Whether or not there is a frame capturing now.
        /// </summary>
        private bool isCapturingFrame;
        /// <summary>
        /// The time spent during capturing.
        /// </summary>
        private float capturingTime;
        /// <summary>
        /// Frame statistics info.
        /// </summary>
        private int capturedFrameCount;
        private int encodedFrameCount;
        /// <summary>
        /// Reference to native lib API.
        /// </summary>
        private System.IntPtr libAPI;
        /// <summary>
        /// The original maximum delta time.
        /// </summary>
        private float originalMaximumDeltaTime;
        /// <summary>
        /// Frame data will be sent to frame encode queue.
        /// </summary>
        private struct FrameData
        {
            /// <summary>
            /// The RGB pixels will be encoded.
            /// </summary>a
            public byte[] pixels;
            /// <summary>
            /// How many this frame will be counted.
            /// </summary>
            public int count;
            /// <summary>
            /// Constructor.
            /// </summary>
            public FrameData(byte[] p, int c)
            {
                pixels = p;
                count = c;
            }
        }
        /// <summary>
        /// The frame encode queue.
        /// </summary>
        private Queue<FrameData> frameQueue;
        /// <summary>
        /// The frame encode thread.
        /// </summary>
        private Thread encodeThread;
        /// <summary>
        /// Cleanup this instance.
        /// </summary>
        public void Cleanup()
        {
            if (mode != ModeType.LIVE_STREAMING)
            {
                if (File.Exists(filePath)) File.Delete(filePath);
            }
            if (mode == ModeType.LIVE_STREAMING)
            {
                LibVideoStreamingAPI_Clean(libAPI);
            }
            else
            {
                LibVideoCaptureAPI_Clean(libAPI);
            }
        }
        /// <summary>
        /// Start capture video.
        /// </summary>
        public override void StartCapture()
        {
            // Check if we can start capture session.
            if (status != VideoCaptureCtrl.StatusType.NOT_START &&
                status != VideoCaptureCtrl.StatusType.FINISH)
            {
                Debug.LogWarning("[VideoCapture::StartCapture] Previous " +
                                 " capture not finish yet!");
                return;
            }
            if (mode == ModeType.LIVE_STREAMING)
            {
                if (!StringUtils.IsRtmpAddress(streamingAddress))
                {
                    Debug.LogWarning(
                       "[VideoCapture::StartCapture] Video live streaming " +
                       "require rtmp server address setup!"
                    );
                    return;
                }
            }
            if (format == FormatType.PANORAMA && !isDedicated)
            {
                Debug.LogWarning(
                    "[VideoCapture::StartCapture] Capture equirectangular " +
                    "video always require dedicated camera!"
                );
                isDedicated = true;
            }
            if (mode == ModeType.LOCAL)
            {
                filePath = PathConfig.saveFolder + StringUtils.GetMp4FileName(StringUtils.GetRandomString(5));
            }
            // Create a RenderTexture with desired frame size for dedicated
            // camera capture to store pixels in GPU.
            if (isDedicated)
            {
                // Use Camera.targetTexture as RenderTexture if already existed.
                if (captureCamera.targetTexture != null)
                {
                    // Use binded rendertexture will ignore antiAliasing config.
                    frameRenderTexture = captureCamera.targetTexture;
                }
                else
                {
                    // Create a rendertexture for video capture.
                    // Size it according to the desired video frame size.
                    frameRenderTexture = new RenderTexture(frameWidth, frameHeight, 24);
                    frameRenderTexture.antiAliasing = antiAliasing;
                    frameRenderTexture.wrapMode = TextureWrapMode.Clamp;
                    frameRenderTexture.filterMode = FilterMode.Trilinear;
                    frameRenderTexture.anisoLevel = 0;
                    frameRenderTexture.hideFlags = HideFlags.HideAndDontSave;
                    // Make sure the rendertexture is created.
                    frameRenderTexture.Create();
                    captureCamera.targetTexture = frameRenderTexture;
                }
            }
            // For capturing normal 2D video, use frameTexture(Texture2D) for
            // intermediate cpu saving, frameRenderTexture(RenderTexture) store
            // the pixels read by frameTexture.
            if (format == FormatType.NORMAL)
            {
                if (isDedicated)
                {
                    // Set the aspect ratio of the camera to match the rendertexture.
                    captureCamera.aspect = frameWidth / ((float)frameHeight);
                    captureCamera.targetTexture = frameRenderTexture;
                }
            }
            // For capture panorama video:
            // EQUIRECTANGULAR: use frameCubemap(Cubemap) for intermediate cpu
            // saving.
            // CUBEMAP: use frameTexture(Texture2D) for intermediate cpu saving.
            else if (format == FormatType.PANORAMA)
            {
                // Create render cubemap.
                frameCubemap = new Cubemap(cubemapSize, TextureFormat.RGB24, false);
                // Setup camera as required for panorama capture.
                captureCamera.aspect = 1.0f;
                captureCamera.fieldOfView = 90;
            }
            blitMaterial = new Material(Shader.Find("Hidden/BlitCopy"));
            if (stereoFormat != StereoFormat.NONE)
            {
                stereoPackMaterial = new Material(Shader.Find("RockVR/Stereoscopic"));
                stereoPackMaterial.DisableKeyword("STEREOPACK_TOP");
                stereoPackMaterial.DisableKeyword("STEREOPACK_BOTTOM");
                stereoPackMaterial.DisableKeyword("STEREOPACK_LEFT");
                stereoPackMaterial.DisableKeyword("STEREOPACK_RIGHT");

                if (stereoTargetTexture == null)
                {
                    stereoTargetTexture = new RenderTexture(frameWidth, frameHeight, 24);
                    stereoTargetTexture.name = "stereo Target";
                    stereoTargetTexture.isPowerOfTwo = true;
                    stereoTargetTexture.dimension = UnityEngine.Rendering.TextureDimension.Tex2D;
                    stereoTargetTexture.useMipMap = false;
                    stereoTargetTexture.antiAliasing = antiAliasing;
                    stereoTargetTexture.wrapMode = TextureWrapMode.Clamp;
                    stereoTargetTexture.filterMode = FilterMode.Trilinear;
                }
                if (finalTarget == null)
                {
                    finalTarget = new RenderTexture(frameWidth, frameHeight, 24);
                    finalTarget.name = "stereo Target";
                    finalTarget.isPowerOfTwo = true;
                    finalTarget.dimension = UnityEngine.Rendering.TextureDimension.Tex2D;
                    finalTarget.useMipMap = false;
                    finalTarget.antiAliasing = antiAliasing;
                    finalTarget.wrapMode = TextureWrapMode.Clamp;
                    finalTarget.filterMode = FilterMode.Trilinear;
                }
            }
            // Pixels stored in frameRenderTexture(RenderTexture) always read by frameTexture(Texture2D).
            // NORMAL:
            // camera render -> frameRenderTexture -> frameTexture -> frameQueue
            // CUBEMAP:
            // 6 cameras render -> 6 faceRenderTexture -> frameTexture -> frameQueue
            // EQUIRECTANGULAR:
            // 6 camera render -> 6 faceRenderTexture-> frameCubemap -> Cubemap2Equirect -> 
            // frameRenderTexture -> frameTexture -> frameQueue
            frameTexture = new Texture2D(frameWidth, frameHeight, TextureFormat.RGB24, false);
            frameTexture.hideFlags = HideFlags.HideAndDontSave;
            frameTexture.wrapMode = TextureWrapMode.Clamp;
            frameTexture.filterMode = FilterMode.Trilinear;
            frameTexture.hideFlags = HideFlags.HideAndDontSave;
            frameTexture.anisoLevel = 0;
            // Reset tempory variables.
            capturingTime = 0f;
            capturedFrameCount = 0;
            encodedFrameCount = 0;
            frameQueue = new Queue<FrameData>();
            // Projection info for native plugin.
            int proj = 0;
            if (format == FormatType.PANORAMA)
            {
                if (panoramaProjection == PanoramaProjectionType.EQUIRECTANGULAR)
                    proj = 1;
                if (panoramaProjection == PanoramaProjectionType.CUBEMAP)
                    proj = 2;
            }
            if (mode == ModeType.LIVE_STREAMING)
            {
                libAPI = LibVideoStreamingAPI_Get(
                    frameWidth,
                    frameHeight,
                    targetFramerate,
                    proj,
                    streamingAddress,
                    PathConfig.ffmpegPath);
            }
            else
            {
                libAPI = LibVideoCaptureAPI_Get(
                    frameWidth,
                    frameHeight,
                    targetFramerate,
                    proj,
                    filePath,
                    PathConfig.ffmpegPath);
            }
            if (libAPI == System.IntPtr.Zero)
            {
                Debug.LogWarning("[VideoCapture::StartCapture] Get native " +
                                 "capture api failed!");
                return;
            }
            if (offlineRender)
            {
                // Backup maximumDeltaTime states.
                originalMaximumDeltaTime = Time.maximumDeltaTime;
                Time.maximumDeltaTime = Time.fixedDeltaTime;
            }
            // Start encoding thread.
            encodeThread = new Thread(FrameEncodeThreadFunction);
            encodeThread.Priority = System.Threading.ThreadPriority.Lowest;
            encodeThread.IsBackground = true;
            encodeThread.Start();
            // Update current status.
            status = VideoCaptureCtrl.StatusType.STARTED;
        }
        /// <summary>
        /// Stop capture video.
        /// </summary>
        public override void StopCapture()
        {
            if (status != VideoCaptureCtrl.StatusType.STARTED)
            {
                Debug.LogWarning("[VideoCapture::StopCapture] capture session " +
                                 "not start yet!");
                return;
            }
            if (offlineRender)
            {
                // Restore maximumDeltaTime states.
                Time.maximumDeltaTime = originalMaximumDeltaTime;
            }
            // Update current status.
            status = VideoCaptureCtrl.StatusType.STOPPED;
        }

        #region Unity Lifecycle
        /// <summary>
        /// Called before any Start functions and also just after a prefab is instantiated.
        /// </summary>
        private new void Awake()
        {
            base.Awake();
            status = VideoCaptureCtrl.StatusType.NOT_START;
        }
        /// <summary>
        /// Called after a camera finishes rendering the scene.
        /// </summary>
        private void OnPostRender()
        {
            //Stereo normal video capture process not run in OnPostRender.
            if (stereoFormat != VideoCapture.StereoFormat.NONE)
                return;
            // Normal video capture process run in OnPostRender.
            if (format != FormatType.NORMAL ||
                status != VideoCaptureCtrl.StatusType.STARTED) // Capture not started yet.
            {
                return;
            }
            capturingTime += Time.deltaTime;
            if (!isCapturingFrame)
            {
                int totalRequiredFrameCount =
                    (int)(capturingTime / deltaFrameTime);
                // Skip frames if we already got enough.
                if (totalRequiredFrameCount > capturedFrameCount)
                {
                    StartCoroutine(CaptureFrameAsync());
                }
            }
        }
        /// <summary>
        /// Called once per frame, after Update has finished.
        /// </summary>
        private void LateUpdate()
        {
            if (stereoFormat == VideoCapture.StereoFormat.NONE)
            {
                // Equirectangular run in LateUpdate.
                if (format != FormatType.PANORAMA ||
                    status != VideoCaptureCtrl.StatusType.STARTED) // Capture not started yet.
                {
                    return;
                }
                capturingTime += Time.deltaTime;
                if (!isCapturingFrame)
                {
                    int totalRequiredFrameCount =
                        (int)(capturingTime / deltaFrameTime);
                    // Skip frames if we already got enough.
                    if (totalRequiredFrameCount > capturedFrameCount)
                    {
                        CaptureCubemapFrameSync();
                    }
                }
            }
            else
            {
                // Stereo normal video capture process run in OnPostRender.
                if (format != FormatType.NORMAL ||
                    status != VideoCaptureCtrl.StatusType.STARTED) // Capture not started yet.
                {
                    return;
                }
                capturingTime += Time.deltaTime;
                if (!isCapturingFrame)
                {
                    int totalRequiredFrameCount =
                        (int)(capturingTime / deltaFrameTime);
                    // Skip frames if we already got enough.
                    if (totalRequiredFrameCount > capturedFrameCount)
                    {
                        StartCoroutine(CaptureFrameAsync());
                    }
                }
            }
        }
        /// <summary>
        /// Sent to all game objects before the application is quit.
        /// </summary>
        private void OnApplicationQuit()
        {
            if (status == VideoCaptureCtrl.StatusType.STARTED)
            {
                StopCapture();
            }
        }
        #endregion // Unity Lifecycle

        #region Video Capture Core
        /// <summary>
        ///  Capture frame Async impl.
        /// </summary>
        private IEnumerator CaptureFrameAsync()
        {
            isCapturingFrame = true;
            if (status == VideoCaptureCtrl.StatusType.STARTED)
            {
                CopyFrameTexture();
                yield return new WaitForEndOfFrame();
                EnqueueFrameTexture();
            }
            isCapturingFrame = false;
        }
        /// <summary>
        /// Capture frame Sync impl.
        /// </summary>
        private void CaptureCubemapFrameSync()
        {
            int width = cubemapSize;
            int height = cubemapSize;

            CubemapFace[] faces = new CubemapFace[] {
                CubemapFace.PositiveX,
                CubemapFace.NegativeX,
                CubemapFace.PositiveY,
                CubemapFace.NegativeY,
                CubemapFace.PositiveZ,
                CubemapFace.NegativeZ
            };
            Vector3[] faceAngles = new Vector3[] {
                new Vector3(0.0f, 90.0f, 0.0f),
                new Vector3(0.0f, -90.0f, 0.0f),
                new Vector3(-90.0f, 0.0f, 0.0f),
                new Vector3(90.0f, 0.0f, 0.0f),
                new Vector3(0.0f, 0.0f, 0.0f),
                new Vector3(0.0f, 180.0f, 0.0f)
            };

            // Reset capture camera rotation.
            captureCamera.transform.eulerAngles = new Vector3(0.0f, 0.0f, 0.0f);

            // Create cubemap face render texture.
            RenderTexture faceTexture = new RenderTexture(width, height, 24);
            faceTexture.antiAliasing = antiAliasing;
#if !(UNITY_5_0 || UNITY_5_1 || UNITY_5_2 || UNITY_5_3)
            faceTexture.dimension = UnityEngine.Rendering.TextureDimension.Tex2D;
#endif
            faceTexture.hideFlags = HideFlags.HideAndDontSave;
            // For intermediate saving
            Texture2D swapTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
            swapTexture.hideFlags = HideFlags.HideAndDontSave;
            // Prepare for target render texture.
            captureCamera.targetTexture = faceTexture;

            // TODO, make this into shader for GPU fast processing.
            if (panoramaProjection == PanoramaProjectionType.CUBEMAP)
            {
                for (int i = 0; i < faces.Length; i++)
                {
                    captureCamera.transform.eulerAngles = faceAngles[i];
                    captureCamera.Render();
                    RenderTexture.active = faceTexture;
                    swapTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                    Color[] pixels = swapTexture.GetPixels();
                    switch (i)
                    {
                        case (int)CubemapFace.PositiveX:
                            frameTexture.SetPixels(0, height, width, height, pixels);
                            break;
                        case (int)CubemapFace.NegativeX:
                            frameTexture.SetPixels(width, height, width, height, pixels);
                            break;
                        case (int)CubemapFace.PositiveY:
                            frameTexture.SetPixels(width * 2, height, width, height, pixels);
                            break;
                        case (int)CubemapFace.NegativeY:
                            frameTexture.SetPixels(0, 0, width, height, pixels);
                            break;
                        case (int)CubemapFace.PositiveZ:
                            frameTexture.SetPixels(width, 0, width, height, pixels);
                            break;
                        case (int)CubemapFace.NegativeZ:
                            frameTexture.SetPixels(width * 2, 0, width, height, pixels);
                            break;
                    }
                }
                frameTexture.Apply();
            }
            else if (panoramaProjection == PanoramaProjectionType.EQUIRECTANGULAR)
            {
                Color[] mirroredPixels = new Color[swapTexture.height * swapTexture.width];
                for (int i = 0; i < faces.Length; i++)
                {
                    captureCamera.transform.eulerAngles = faceAngles[i];
                    captureCamera.Render();
                    RenderTexture.active = faceTexture;
                    swapTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                    // Mirror vertically to meet the standard of unity cubemap.
                    Color[] OrignalPixels = swapTexture.GetPixels();
                    for (int y1 = 0; y1 < height; y1++)
                    {
                        for (int x1 = 0; x1 < width; x1++)
                        {
                            mirroredPixels[y1 * width + x1] =
                                OrignalPixels[((height - 1 - y1) * width) + x1];
                        }
                    }
                    frameCubemap.SetPixels(mirroredPixels, faces[i]);
                }
                frameCubemap.SmoothEdges();
                frameCubemap.Apply();
                // Convert to equirectangular projection.
                Graphics.Blit(frameCubemap, frameRenderTexture, cubemap2Equirectangular);
                // From frameRenderTexture to frameTexture.
                CopyFrameTexture();
            }

            RenderTexture.active = null;
            captureCamera.targetTexture = null;

            // Clean temp texture.
            DestroyImmediate(swapTexture);
            DestroyImmediate(faceTexture);

            // Send for encoding.
            EnqueueFrameTexture();
        }
        /// <summary>
        /// Copy the frame texture from GPU to CPU.
        /// </summary>
        void CopyFrameTexture()
        {
            if (stereoFormat == StereoFormat.NONE)
            {
                // Bind texture.
                RenderTexture.active = frameRenderTexture;
            }
            else
            {
                SetStereoVideoFormat();
            }
            // TODO, remove expensive step of copying pixel data from GPU to CPU.
            frameTexture.ReadPixels(new Rect(0, 0, frameWidth, frameHeight), 0, 0, false);
            frameTexture.Apply();
            // Restore RenderTexture states.
            RenderTexture.active = null;
        }
        /// <summary>
        /// Send the captured frame texture to encode queue.
        /// </summary>
        void EnqueueFrameTexture()
        {
            int totalRequiredFrameCount = (int)(capturingTime / deltaFrameTime);
            int requiredFrameCount = totalRequiredFrameCount - capturedFrameCount;
            lock (this)
            {
                frameQueue.Enqueue(
                    new FrameData(frameTexture.GetRawTextureData(), requiredFrameCount));
            }
            capturedFrameCount = totalRequiredFrameCount;
        }
        /// <summary>
        /// Frame encoding thread impl.
        /// </summary>
        private void FrameEncodeThreadFunction()
        {
            while (status == VideoCaptureCtrl.StatusType.STARTED || frameQueue.Count > 0)
            {
                if (frameQueue.Count > 0)
                {
                    FrameData frame;
                    lock (this)
                    {
                        frame = frameQueue.Dequeue();
                    }
                    if (mode == ModeType.LIVE_STREAMING)
                    {
                        LibVideoStreamingAPI_SendFrames(libAPI, frame.pixels, frame.count);
                    }
                    else
                    {
                        LibVideoCaptureAPI_SendFrames(libAPI, frame.pixels, frame.count);
                    }
                    encodedFrameCount++;
                    if (VideoCaptureCtrl.instance.debug)
                    {
                        Debug.Log(
                            "[VideoCapture::FrameEncodeThreadFunction] Encoded " +
                            encodedFrameCount + " frames. " + frameQueue.Count +
                            " frames remaining."
                        );
                    }
                }
                else
                {
                    // Wait 1 second for captured frame.
                    Thread.Sleep(1000);
                }
            }
            // Notify native encoding process finish.
            if (mode == ModeType.LIVE_STREAMING)
            {
                LibVideoStreamingAPI_Close(libAPI);
            }
            else
            {
                LibVideoCaptureAPI_Close(libAPI);
            }
            // Notify caller video capture complete.
            if (eventDelegate.OnComplete != null)
            {
                eventDelegate.OnComplete();
            }
            if (VideoCaptureCtrl.instance.debug)
            {
                Debug.Log("[VideoCapture::FrameEncodeThreadFunction] Encode " +
                          "process finish!");
            }
            // Update current status.
            status = VideoCaptureCtrl.StatusType.FINISH;
        }

        /// <summary>
        /// Conversion video format to stereo.
        /// </summary>
        private void SetStereoVideoFormat()
        {
            Vector3 cameraPosition = captureCamera.transform.position;
            //Left eye
            captureCamera.transform.Translate(new Vector3(-stereoSeparation, 0, 0), Space.Self);
            RenderCameraToRenderTexture(captureCamera, stereoTargetTexture);
            if (stereoFormat == StereoFormat.TOPBOTTOM)
            {
                stereoPackMaterial.DisableKeyword("STEREOPACK_BOTTOM");
                stereoPackMaterial.EnableKeyword("STEREOPACK_TOP");
            }
            else if (stereoFormat == StereoFormat.LEFTRIGHT)
            {
                stereoPackMaterial.DisableKeyword("STEREOPACK_RIGHT");
                stereoPackMaterial.EnableKeyword("STEREOPACK_LEFT");
            }
            Graphics.Blit(stereoTargetTexture, finalTarget, stereoPackMaterial);
            // Right eye
            captureCamera.transform.localPosition = cameraPosition;
            captureCamera.transform.Translate(new Vector3(stereoSeparation, 0f, 0f), Space.Self);
            RenderCameraToRenderTexture(captureCamera, stereoTargetTexture);
            if (stereoFormat == StereoFormat.TOPBOTTOM)
            {
                stereoPackMaterial.EnableKeyword("STEREOPACK_BOTTOM");
                stereoPackMaterial.DisableKeyword("STEREOPACK_TOP");
            }
            else if (stereoFormat == StereoFormat.LEFTRIGHT)
            {
                stereoPackMaterial.EnableKeyword("STEREOPACK_RIGHT");
                stereoPackMaterial.DisableKeyword("STEREOPACK_LEFT");
            }
            Graphics.Blit(stereoTargetTexture, finalTarget, stereoPackMaterial);
            // Restore camera state
            captureCamera.transform.localPosition = cameraPosition;
            RenderTexture.active = finalTarget;
        }
        /// <summary>
        /// Get render data from camera to render texture.
        /// </summary>
        /// <param name="camera">The render camera</param>
        /// <param name="stereoTarget">The stereo target</param>
        private void RenderCameraToRenderTexture(Camera camera, RenderTexture stereoTarget)
        {
            camera.CopyFrom(captureCamera);
            camera.targetTexture = frameRenderTexture;
            camera.Render();
            Graphics.SetRenderTarget(stereoTarget);
            Graphics.Blit(frameRenderTexture, blitMaterial);
            Graphics.SetRenderTarget(null);
        }
        #endregion // Video Capture Core

        #region Dll Import
        [DllImport("VideoCaptureLib")]
        static extern System.IntPtr LibVideoCaptureAPI_Get(int width, int height, int rate, int proj, string path, string ffpath);

        [DllImport("VideoCaptureLib")]
        static extern void LibVideoCaptureAPI_SendFrames(System.IntPtr api, byte[] data, int count);

        [DllImport("VideoCaptureLib")]
        static extern void LibVideoCaptureAPI_Close(System.IntPtr api);

        [DllImport("VideoCaptureLib")]
        static extern void LibVideoCaptureAPI_Clean(System.IntPtr api);

        [DllImport("VideoCaptureLib")]
        static extern System.IntPtr LibVideoStreamingAPI_Get(int width, int height, int rate, int proj, string address, string ffpath);

        [DllImport("VideoCaptureLib")]
        static extern void LibVideoStreamingAPI_SendFrames(System.IntPtr api, byte[] data, int count);

        [DllImport("VideoCaptureLib")]
        static extern void LibVideoStreamingAPI_Close(System.IntPtr api);

        [DllImport("VideoCaptureLib")]
        static extern void LibVideoStreamingAPI_Clean(System.IntPtr api);
        #endregion // Dll Import
    }

    /// <summary>
    /// <c>VideoMuxing</c> is processed after temp video captured, with or without
    /// temp audio captured. If audio captured, it will mux the video and audio
    /// into the same file.
    /// </summary>
    public class VideoMuxing
    {
        /// <summary>
        /// The merged video file path.
        /// </summary>
        public string filePath;
        /// <summary>
        /// The capture video instance.
        /// </summary>
        private VideoCapture videoCapture;
        /// <summary>
        /// The capture audio instance.
        /// </summary>
        private AudioCapture audioCapture;
        /// <summary>
        /// Initializes a new instance of the <see cref="T:RockVR.Video.VideoMuxing"/> class.
        /// </summary>
        /// <param name="videoCapture">Video capture.</param>
        /// <param name="audioCapture">Audio capture.</param>
        public VideoMuxing(VideoCapture videoCapture, AudioCapture audioCapture)
        {
            this.videoCapture = videoCapture;
            this.audioCapture = audioCapture;
        }
        /// <summary>
        /// Video/Audio mux function impl.
        /// Blocking function.
        /// </summary>
        public bool Muxing()
        {
            filePath = PathConfig.saveFolder + StringUtils.GetMp4FileName(StringUtils.GetRandomString(5));
            System.IntPtr libAPI = LibVideoMergeAPI_Get(
                videoCapture.bitrate,
                filePath,
                videoCapture.filePath,
                audioCapture.filePath,
                PathConfig.ffmpegPath);
            if (libAPI == System.IntPtr.Zero)
            {
                Debug.LogWarning("[VideoMuxing::Muxing] Get native LibVideoMergeAPI failed!");
                return false;
            }
            LibVideoMergeAPI_Merge(libAPI);
            // Make sure generated the merge file.
            int waitCount = 0;
            while (!File.Exists(filePath))
            {
                if (waitCount++ < 100)
                    Thread.Sleep(500);
                else
                {
                    Debug.LogWarning("[VideoMuxing::Muxing] Mux process failed!");
                    LibVideoMergeAPI_Clean(libAPI);
                    return false;
                }
            }
            LibVideoMergeAPI_Clean(libAPI);
            if (VideoCaptureCtrl.instance.debug)
            {
                Debug.Log("[VideoMuxing::Muxing] Mux process finish!");
            }
            return true;
        }

        #region Dll Import
        [DllImport("VideoCaptureLib")]
        static extern System.IntPtr LibVideoMergeAPI_Get(int rate, string path, string vpath, string apath, string ffpath);

        [DllImport("VideoCaptureLib")]
        static extern void LibVideoMergeAPI_Merge(System.IntPtr api);

        [DllImport("VideoCaptureLib")]
        static extern void LibVideoMergeAPI_Clean(System.IntPtr api);
        #endregion // Dll Import
    }
}
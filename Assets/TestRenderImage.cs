using RockVR.Video;
using UnityEngine;

public class TestRenderImage : MonoBehaviour
{
    public RenderTexture frameRenderTexture;

    public int stereoResolution = 2048;
    public float stereoSeparation = 0.03f;
    public Shader shader;
    public Material blitMaterial;
    public Material stereoPackMaterial;
    public RenderTexture stereoTargetTexture;
    public RenderTexture finalTarget;
    public Material go;
    public AudioCapture audioCapture;
    private void UpdateTexture()
    {
        Camera camera = this.GetComponent<Camera>();
        // Save camera state
        Vector3 cameraPosition = camera.transform.localPosition;
        //Left eye
        camera.transform.Translate(new Vector3(-stereoSeparation, 0f, 0f));
        RenderCameraToRenderTexture(camera);
        stereoPackMaterial.DisableKeyword("STEREOPACK_TOP");
        stereoPackMaterial.EnableKeyword("STEREOPACK_BOTTOM");
        Graphics.Blit(stereoTargetTexture, finalTarget, stereoPackMaterial);
        // Right eye
        camera.transform.localPosition = cameraPosition;
        camera.transform.Translate(new Vector3(stereoSeparation, 0f, 0f));
        RenderCameraToRenderTexture(camera);
        stereoPackMaterial.DisableKeyword("STEREOPACK_BOTTOM");
        stereoPackMaterial.EnableKeyword("STEREOPACK_TOP");
        Graphics.Blit(stereoTargetTexture, finalTarget, stereoPackMaterial);
        // Restore camera state
        camera.transform.localPosition = cameraPosition;
        RenderTexture.active = finalTarget;
        go.SetTexture("_MainTex", finalTarget);
    }

    private void RenderCameraToRenderTexture(Camera camera)
    {
        camera.targetTexture = frameRenderTexture;
        camera.Render();
        Graphics.SetRenderTarget(stereoTargetTexture);
        Graphics.Blit(frameRenderTexture, blitMaterial);
        Graphics.SetRenderTarget(null);
    }


    public void PrepareCapture()
    {
        stereoPackMaterial = new Material(Shader.Find("RockVR/Stereoscopic"));
        blitMaterial = new Material(Shader.Find("Hidden/BlitCopy"));
        stereoPackMaterial.DisableKeyword("STEREOPACK_TOP");
        stereoPackMaterial.DisableKeyword("STEREOPACK_BOTTOM");
        stereoPackMaterial.DisableKeyword("STEREOPACK_LEFT");
        stereoPackMaterial.DisableKeyword("STEREOPACK_RIGHT");

        frameRenderTexture = new RenderTexture(1920, 1080, 24);
        frameRenderTexture.antiAliasing = 1;
        frameRenderTexture.wrapMode = TextureWrapMode.Clamp;
        frameRenderTexture.filterMode = FilterMode.Trilinear;
        frameRenderTexture.anisoLevel = 0;
        frameRenderTexture.hideFlags = HideFlags.HideAndDontSave;
        // Make sure the rendertexture is created.
        frameRenderTexture.Create();
        if (stereoTargetTexture == null)
        {
            stereoTargetTexture = new RenderTexture(1960, 1080, 24);
            stereoTargetTexture.name = "stereo Target";
            stereoTargetTexture.isPowerOfTwo = true;
            stereoTargetTexture.dimension = UnityEngine.Rendering.TextureDimension.Tex2D;
            stereoTargetTexture.useMipMap = false;
            stereoTargetTexture.antiAliasing = 1;
            stereoTargetTexture.wrapMode = TextureWrapMode.Clamp;
            stereoTargetTexture.filterMode = FilterMode.Trilinear;
        }
        if (finalTarget == null)
        {
            finalTarget = new RenderTexture(1960, 1080, 24);
            finalTarget.name = "stereo Target";
            finalTarget.isPowerOfTwo = true;
            finalTarget.dimension = UnityEngine.Rendering.TextureDimension.Tex2D;
            finalTarget.useMipMap = false;
            finalTarget.antiAliasing = 1;
            finalTarget.wrapMode = TextureWrapMode.Clamp;
            finalTarget.filterMode = FilterMode.Trilinear;
        }
    }

    void Start()
    {
        
        //PrepareCapture();
    }
    bool a = false;
    private void Update()
    {
        //if (Input.GetKeyDown(KeyCode.A))
        //{
        //    PrepareCapture();
        //    a = true;
        //}
        //if (a)
        //{
        //    UpdateTexture();
        //}
        //if (Input.GetKeyDown(KeyCode.Space))
        //{
        //    RockVR.Video.VideoCaptureCtrl.instance.videoCaptures[0].frameSize = RockVR.Video.VideoCaptureBase.FrameSizeType._1920x1080;
        //}
        //if (Input.GetKeyDown(KeyCode.A))
        //{
        //    audioCapture.StartCapture();
        //}
        //if (Input.GetKeyDown(KeyCode.B))
        //{
        //    audioCapture.StopCapture();
        //}

    }
    private void OnDestroy()
    {
        if (frameRenderTexture != null)
        {
            //Destroy(stereoFaceTarget);
            frameRenderTexture = null;
        }
        if (stereoTargetTexture != null)
        {
            Destroy(stereoTargetTexture);
            stereoTargetTexture = null;
        }
    }
}
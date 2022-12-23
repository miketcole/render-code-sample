#if UNITY_EDITOR
namespace Candy.Renderer {
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading;
    
    using Unity.EditorCoroutines.Editor;
    using UnityEditor;
    using UnityEngine;

    public class GenlockRecorder : MonoBehaviour {
        private double m_genlockRate = 60; // Desired rendering framerate relative to the system clock (simulates an external Genlock)
        private double m_gameTimeRate = 60; // Desired game time framerate (ie. specifies how much game time should elapse between each frame render)
        private int m_frameWidth;
        private int m_frameHeight;
        private CandyPlaybackEngine m_playbackEngine;
        private Camera m_camera;
        private EditorCoroutine m_editorCoroutine;
        private Coroutine m_coroutine;
        private readonly Queue<RenderTexture> m_frames = new Queue<RenderTexture>();
        private ulong m_lastFakeGenLockCount = 0;
        private RenderTexture m_lastRender;
        private double m_currentGenlockRate = 0;
        private int m_framesWritten = 1;
        private GenlockMemoryManager m_memoryManager;
        private AudioRecorder m_audioRecorder;
        private bool m_shouldRecordFrames = false;
        private VideoRenderer[] m_videoRenderers;
        private TextureFormat m_outputTextureFormat;
        
        /// <summary>
        /// Gets the current number of frames in memory.
        /// </summary>
        public int FrameCount => m_frames.Count;
        
        /// <summary>
        /// Gets the total number of frames that have been rendered.
        /// </summary>
        public int TotalFramesRendered => m_framesWritten + m_frames.Count;

        private void Awake() {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 9999;
            m_genlockRate = CandyRenderer.Settings.FPS;
            m_gameTimeRate = CandyRenderer.Settings.FPS;
            m_frameWidth = CandyRenderer.Settings.OutputWidth;
            m_frameHeight = CandyRenderer.Settings.OutputHeight;
            m_outputTextureFormat = CandyRenderer.Settings.IncludeAlpha ? TextureFormat.RGBA32 : TextureFormat.RGB24;
            m_playbackEngine = CandyRenderer.cpe;
            m_camera = m_playbackEngine.Camera.SceneCamera;
            m_memoryManager = new GameObject("MemoryManager").AddComponent<GenlockMemoryManager>();
            m_memoryManager.Initialize(this);
            if (CandyRenderer.Settings.IncludeAudioPass) {
                m_audioRecorder = new AudioRecorder();
            }

            m_videoRenderers = GameObject.FindObjectsOfType<VideoRenderer>();
            m_shouldRecordFrames = CandyRenderer.Settings.IncludeFramesPass;
            m_coroutine = StartCoroutine(WaitForNextFrame());
        }
        
        private IEnumerator WaitForNextFrame() {
            if (m_audioRecorder != null) {
                m_audioRecorder.StartRecording();
            }

            if (m_shouldRecordFrames) {
                m_camera.targetTexture = new RenderTexture(m_frameWidth, m_frameHeight, 0, RenderTextureFormat.ARGB32);
            }
            
            foreach (VideoRenderer videoRenderer in m_videoRenderers) {
                videoRenderer.OnRenderingStarted();
            }

            while (true) {
                yield return new WaitForEndOfFrame();

                if (m_audioRecorder != null) {
                    m_audioRecorder.CaptureFrame();
                }

                if (m_shouldRecordFrames) {
                    m_lastRender = m_camera.targetTexture;

                    if (m_camera.targetTexture == null || m_camera.targetTexture.width != m_frameWidth || m_camera.targetTexture.height != m_frameHeight) {
                        m_camera.targetTexture = new RenderTexture(m_frameWidth, m_frameHeight, 0, RenderTextureFormat.ARGB32);
                    }

                    m_frames.Enqueue(m_lastRender);
                    m_camera.targetTexture = new RenderTexture(m_frameWidth, m_frameHeight, 0, RenderTextureFormat.ARGB32);
                }

                foreach (VideoRenderer videoRenderer in m_videoRenderers) {
                    videoRenderer.StepForward();
                }
                
                WaitForNextGenLock();
                Time.captureDeltaTime = 1.0f / (float)m_gameTimeRate;
            }
        }
        
        private void WaitForNextGenLock() {
            if (m_currentGenlockRate != m_genlockRate) {
                if (m_genlockRate < 1) {
                    m_genlockRate = 1;
                }

                m_lastFakeGenLockCount = 0;
                m_currentGenlockRate = m_genlockRate;
            }

            double t = Time.realtimeSinceStartup;
            double nextGenLockTime = (m_lastFakeGenLockCount + 1) / m_genlockRate;
            if (t > nextGenLockTime) {
                // This shouldn't normally happen.
                if (m_lastFakeGenLockCount != 0) {
                    CandyRenderer.LogWarning("Frame drop: Rendering too slow (at " + t.ToString("0.000") + ")");
                }

                m_lastFakeGenLockCount = (ulong)(t * m_genlockRate + 0.5);
                return;
            }


            // This is the actual sleep/busy loop simulating waiting for the genlock signal.
            var sleepTime = nextGenLockTime - t - 0.01f; // conservative sleep
            if (sleepTime > 0) {
                Thread.Sleep((int) (sleepTime * 1000));
            }

            do // busy-loop the remaining time for accuracy
            {
                t = Time.realtimeSinceStartup;
            } while (t < nextGenLockTime);
            ++m_lastFakeGenLockCount;

            if (t > nextGenLockTime + 0.1 / m_genlockRate) {
                // This shouldn't normally happen.
                if (t > nextGenLockTime + 1.0 / m_genlockRate) {
                    m_lastFakeGenLockCount = (ulong) (t * m_genlockRate + 0.5);
                    CandyRenderer.LogWarning("Frame drop: Waiting for GenLock too slow (at " + t.ToString("0.000") + ")");
                    return;
                }

                CandyRenderer.LogWarning(
                    "Waited " + ((t - nextGenLockTime) / (1.0 / m_genlockRate) * 100.0).ToString("0") +
                    "% past next GenLock (at " + t.ToString("0.000") + ")");
            }
        }
        
        private IEnumerator WriteRemainingFrames(Action callback) {
            while (m_frames.Count > 0) {
                RenderTexture renderTexture = m_frames.Dequeue();
                WriteToDisk(renderTexture);
                renderTexture.Release();
                renderTexture = null;
                // Wait one frame to allow Unity to release the memory for the just written out texture
                yield return null;
            }
            
            callback?.Invoke();
            Destroy(gameObject);
        }
        
        private void WriteToDisk(RenderTexture frame) {
            CandyRendererSettings settings = CandyRenderer.Settings;
            string filename = settings.CaptureID + "_" + m_framesWritten.ToString("00000") + ".png";
            
            string root_path = settings.FramesPathMP4;
            
            if (!Directory.Exists(root_path)) {
                Directory.CreateDirectory(root_path);
            }
            
            RenderTexture originalRenderTexture = RenderTexture.active;
            RenderTexture.active = frame;
            Texture2D texture = new Texture2D(m_frameWidth, m_frameHeight, m_outputTextureFormat, false);
            texture.ReadPixels(new Rect(0, 0, m_frameWidth, m_frameHeight), 0, 0);
            texture.Apply();
            RenderTexture.active = originalRenderTexture;
            
            byte[] png_bytes = texture.EncodeToPNG();
            CandyRenderer.Log($"Writing frame {m_framesWritten.ToString("00000")} to disk.", true);
            File.WriteAllBytes(Path.Combine(root_path, filename), png_bytes);
            Destroy(texture);
            m_framesWritten++;
        }

        public void Flush() {
            // Write all frames to disk synchronously
            while (m_frames.Count > 0) {
                RenderTexture renderTexture = m_frames.Dequeue();
                WriteToDisk(renderTexture);
                renderTexture.Release();
                renderTexture = null;
            }
            
            m_frames.Clear();
            
            foreach (VideoRenderer videoRenderer in m_videoRenderers) {
                if (videoRenderer.IsPlaying) {
                    videoRenderer.LoadNextFrameBatch();
                }
            }
        }

        public void StopRecording(Action onComplete) {
            if (m_audioRecorder != null) {
                m_audioRecorder.StopRecording();
                m_audioRecorder = null;
            }

            StopCoroutine(m_coroutine);
            Destroy(m_memoryManager.gameObject);

            if (m_videoRenderers != null) {
                foreach (VideoRenderer videoRenderer in m_videoRenderers) {
                    videoRenderer.Stop();
                    videoRenderer.Dispose();
                    Destroy(videoRenderer);
                }
            }

            m_videoRenderers = null;
            
            if (CandyRenderer.Settings.IncludeFramesPass) {
                StartCoroutine(WriteRemainingFrames(onComplete));
            } else {
                onComplete?.Invoke();
            }
        }
    }
}
#endif

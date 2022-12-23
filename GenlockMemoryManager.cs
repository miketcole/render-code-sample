#if UNITY_EDITOR
namespace Candy.Renderer {

    using System.Collections;
    
    using UnityEditor;
    
    using Unity.EditorCoroutines.Editor;
    
    using UnityEngine;
    
    public class GenlockMemoryManager : MonoBehaviour {
        private GenlockRecorder m_recorder;
        private EditorCoroutine m_coroutine;
        private int m_framesLimit;

        public const float SECONDS_PER_CHUNK = 1.0f;
        
        private void Start() {
            m_framesLimit = (int) (SECONDS_PER_CHUNK * CandyRenderer.Settings.FPS);
            m_coroutine = EditorCoroutineUtility.StartCoroutine(ManageMemory(), this);
        }
            
        private void OnDestroy() {
            EditorCoroutineUtility.StopCoroutine(m_coroutine);
        }
        
        private IEnumerator ManageMemory() {
                
            while (true) {
                if (m_recorder.FrameCount >= m_framesLimit) {
                    EditorApplication.isPaused = true;
                    m_recorder.Flush();
                    EditorApplication.isPaused = false;
                    yield return new WaitForEndOfFrame();
                }

                yield return null;
            }
        }
        
        public void Initialize(GenlockRecorder recorder) {
            m_recorder = recorder;
        }
    }
}

#endif
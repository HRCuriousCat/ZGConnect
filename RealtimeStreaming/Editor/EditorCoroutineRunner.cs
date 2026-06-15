using System;
using System.Collections;
using UnityEditor;

namespace ZGConnect.RealtimeStreaming.Editor
{
    sealed class EditorCoroutineRunner
    {
        IEnumerator _routine;
        readonly Action _onComplete;
        readonly Action<Exception> _onError;

        EditorCoroutineRunner(IEnumerator routine, Action onComplete, Action<Exception> onError)
        {
            _routine = routine;
            _onComplete = onComplete;
            _onError = onError;
        }

        public static EditorCoroutineRunner Start(
            IEnumerator routine,
            Action onComplete = null,
            Action<Exception> onError = null)
        {
            var runner = new EditorCoroutineRunner(routine, onComplete, onError);
            EditorApplication.update += runner.Tick;
            return runner;
        }

        public void Stop()
        {
            EditorApplication.update -= Tick;
            _routine = null;
        }

        void Tick()
        {
            if (_routine == null)
            {
                Stop();
                return;
            }

            try
            {
                if (!_routine.MoveNext())
                {
                    Stop();
                    _onComplete?.Invoke();
                }
            }
            catch (Exception ex)
            {
                Stop();
                _onError?.Invoke(ex);
            }
        }
    }
}


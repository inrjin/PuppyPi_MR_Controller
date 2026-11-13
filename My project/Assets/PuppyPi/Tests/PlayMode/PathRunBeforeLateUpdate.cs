#if UNITY_EDITOR
using System;
using UnityEngine;

namespace PuppyPi.Tests
{
    // Reproduces an input callback after the map client consumed a new frame,
    // but before the map window's LateUpdate presented that frame.
    [DefaultExecutionOrder(25)]
    public sealed class PathRunBeforeLateUpdate : MonoBehaviour
    {
        public PuppyPiMapDataClient Map;
        public PuppyPiMapWindow Window;
        public long AfterRevision;
        public bool Armed, Invoked, ObservedStaleBypass;
        public Action Click;
        public double LastFrameGap { get; private set; }
        public double MaximumFrameGap { get; private set; }
        public double LastFrameAt { get; private set; } = double.NaN;

        private void Update()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (!double.IsNaN(LastFrameAt))
            {
                LastFrameGap = now - LastFrameAt;
                MaximumFrameGap = Math.Max(MaximumFrameGap, LastFrameGap);
            }
            LastFrameAt = now;
            if (!Armed || Map == null || Window == null || Map.Revision <= AfterRevision) return;
            Armed = false;
            Invoked = true;
            ObservedStaleBypass = !Window.BypassPreviewReady;
            Click?.Invoke();
        }
    }
}
#endif

using System;
using UnityEngine;

namespace Hai.LightboxViewer.Scripts.Runtime
{
#if IS_RESILIENCE_DEV
    [AddComponentMenu("Haï/Lightbox Viewer Definition")]
#else
    [AddComponentMenu("/")]
#endif
    public class LightboxViewerDefinition : MonoBehaviour
    {
        public GameObject[] lightboxes;
        public GameObject[] hideInRenders;
        public LightboxViewerViewGroup[] viewGroups;

        [Serializable]
        public struct LightboxViewerViewGroup
        {
            public string title;
            public string key;
            public GameObject[] members;
        }
    }
}
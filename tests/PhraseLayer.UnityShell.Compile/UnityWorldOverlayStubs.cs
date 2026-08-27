namespace UnityEngine
{
    public enum TextAnchor
    {
        UpperLeft = 0,
        UpperCenter = 1,
        UpperRight = 2,
        MiddleLeft = 3,
        MiddleCenter = 4,
        MiddleRight = 5,
        LowerLeft = 6,
        LowerCenter = 7,
        LowerRight = 8,
    }

    public enum TextAlignment
    {
        Left = 0,
        Center = 1,
        Right = 2,
    }

    public enum QueryTriggerInteraction
    {
        UseGlobal = 0,
        Ignore = 1,
        Collide = 2,
    }

    public sealed class TextMesh : Component
    {
        public string text { get; set; }
        public TextAnchor anchor { get; set; }
        public TextAlignment alignment { get; set; }
        public FontStyle fontStyle { get; set; }
        public int fontSize { get; set; }
        public float characterSize { get; set; }
    }

    public struct RaycastHit
    {
        public Vector3 point;
        public Vector3 normal;
        public float distance;
    }

    public static class Physics
    {
        public static bool Raycast(
            Ray ray,
            out RaycastHit hitInfo,
            float maxDistance,
            int layerMask,
            QueryTriggerInteraction queryTriggerInteraction)
        {
            hitInfo = default(RaycastHit);
            return false;
        }
    }
}

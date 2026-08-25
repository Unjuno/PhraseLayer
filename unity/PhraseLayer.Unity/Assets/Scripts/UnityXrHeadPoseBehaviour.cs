using UnityEngine;
using UnityEngine.XR;

namespace PhraseLayer.Unity
{
    /// <summary>
    /// Applies the XR head pose to the camera transform without depending on an input-system or Meta-specific rig.
    /// Position/rotation are updated only when the active XR device exposes the corresponding feature.
    /// </summary>
    public sealed class UnityXrHeadPoseBehaviour : MonoBehaviour
    {
        [SerializeField] private bool trackPosition = true;
        [SerializeField] private bool trackRotation = true;

        private InputDevice headDevice;

        private void OnEnable()
        {
            RefreshDevice();
            ApplyHeadPose();
        }

        private void LateUpdate()
        {
            if (!headDevice.isValid)
                RefreshDevice();
            ApplyHeadPose();
        }

        private void RefreshDevice()
        {
            headDevice = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        }

        private void ApplyHeadPose()
        {
            if (trackPosition && headDevice.TryGetFeatureValue(CommonUsages.devicePosition, out var position))
                transform.localPosition = position;

            if (trackRotation && headDevice.TryGetFeatureValue(CommonUsages.deviceRotation, out var rotation))
                transform.localRotation = rotation;
        }
    }
}

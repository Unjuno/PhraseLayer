using System;

namespace UnityEngine.Android
{
    public sealed class PermissionCallbacks
    {
        public event Action<string> PermissionGranted;
        public event Action<string> PermissionDenied;

        public void RaiseGranted(string permission) => PermissionGranted?.Invoke(permission);
        public void RaiseDenied(string permission) => PermissionDenied?.Invoke(permission);
    }

    public static class Permission
    {
        public static bool HasUserAuthorizedPermission(string permission) => false;

        public static void RequestUserPermissions(
            string[] permissions,
            PermissionCallbacks callbacks)
        {
        }
    }
}
